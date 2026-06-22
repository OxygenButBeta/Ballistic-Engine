using System.Text;
using Vortice.Direct3D12;
using Vortice.Dxc;

namespace BallisticEngine.DX12;

// FAZ -1 — Render-graph v2 GPU-EXECUTE self-test. Where Dx12RgSelfTest.Run exercises only the
// COMPILE pipeline (DAG / cull / lifetimes / aliasing math) and realises the heap but records NO
// GPU work, this test drives REAL GPU work THROUGH the graph's Execute() path and reads back a real
// result. Its whole purpose is to catch bugs in EmitBarriers (automatic state transitions),
// transient placement/aliasing, Resolve(), and the per-pass record loop — so it FAILS LOUD and never
// reports PASS unless the bytes coming back from the GPU are actually correct.
//
// Graph built (2 passes, real work):
//   Pass A (compute):  transient UAV buffer 'rg.work' (256 uints). Bind it as a root UAV, dispatch a
//                      trivial CS that writes work[i] = i*2+1. Declares Write(work, UnorderedAccess).
//   Pass B (graphics): transient buffer 'rg.copy' (CopyDst). Reads 'rg.work' as CopySrc and copies it
//                      into 'rg.copy'. Declares Read(work, CopySrc) + Write(copy, CopyDst).
//
// The graph derives the UAV->CopySrc transition on 'rg.work' AUTOMATICALLY between the two passes
// (Granite invalidate/flush) — that derived barrier is exactly what we want to prove fires correctly.
// 'rg.copy' is imported NOWHERE and nobody reads it, so to keep both passes alive we ALSO read 'rg.copy'
// from a NeverCull "readback" pass that CopyBufferRegion's it into a CPU readback heap, then we Map +
// verify work[i] == i*2+1 for every element.
//
// Execute path: a standalone self-test has no frame open, so Dx12RgGraph.Execute() takes the
// dev.ExecuteSync(per-pass) branch — each pass records onto its own command list and the device flushes
// the GPU between passes. The registry's per-resource CurrentState tracking persists across those
// separate lists (D3D12 resource state is sticky), so the derived barriers stay correct. Execute(ctx)
// reads ctx?.FrameCounter ?? 0, so we pass null (no Dx12FrameContext needed standalone).
//
// Trigger: BALLISTIC_DX12_RG_SELFTEST=1 also runs this (wired in DirectXRenderAsset, after the
// compile-only Run); report is appended to the same BALLISTIC_DX12_RG_SELFTEST_OUT file.

public static class Dx12RgExecuteSelfTest {
    const int Count = 256;

    // Inline HLSL: write Output[i] = i*2+1 via a root UAV (raw buffer). Mirrors ComputeProbe but is
    // self-contained so the test owns its shader and can't drift from an embedded resource.
    const string Hlsl =
        "cbuffer Params : register(b0) { uint Count; uint3 _pad; };\n" +
        "RWStructuredBuffer<uint> Output : register(u0);\n" +
        "[numthreads(64,1,1)]\n" +
        "void CSMain(uint3 id : SV_DispatchThreadID) {\n" +
        "    uint i = id.x;\n" +
        "    if (i >= Count) return;\n" +
        "    Output[i] = i * 2u + 1u;\n" +
        "}\n";

    public static string RunExecute(Dx12Device dev) {
        var sb = new StringBuilder();
        sb.AppendLine("[Dx12RgExecuteSelfTest] GPU-execute path verification:");

        bool ok = true;
        void Check(bool cond, string label) {
            sb.AppendLine($"  {(cond ? "PASS" : "FAIL")}: {label}");
            if (!cond) ok = false;
        }

        ID3D12RootSignature rootSig = null;
        ID3D12PipelineState pso = null;
        ID3D12Resource readback = null;
        Dx12RgGraph g = null;
        try {
            // --- compute PSO + root sig (copied from Dx12ComputeProbe's proven calls) ---------------
            var countConst = new RootParameter1(new RootConstants(0, 0, 1), ShaderVisibility.All);
            var uav0 = new RootParameter1(RootParameterType.UnorderedAccessView, new RootDescriptor1(0, 0), ShaderVisibility.All);
            rootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
                new RootSignatureDescription1(RootSignatureFlags.None, new[] { countConst, uav0 })));

            byte[] cs = Dx12ShaderCompiler.Compile(DxcShaderStage.Compute, Hlsl, "CSMain", "RgExecuteSelfTest.hlsl");
            pso = dev.Device.CreateComputePipelineState(
                new ComputePipelineStateDescription { RootSignature = rootSig, ComputeShader = cs });

            // CPU readback heap (NOT graph-owned — the destination we Map). Drained after Execute.
            readback = dev.CreateReadbackBuffer(Count * sizeof(uint));

            // --- build the graph --------------------------------------------------------------------
            g = new Dx12RgGraph(dev);
            g.Reset();

            Dx12RgHandle work = default, copy = default;

            // Pass A (compute): create the UAV work buffer, dispatch the pattern writer.
            g.AddPass("RgExec.Compute", Dx12RgQueue.Graphics, b => {
                work = b.CreateTransient(Dx12RgResourceDesc.Buffer("rg.work", Count * sizeof(uint), uav: true));
                b.Write(work, Dx12RgResourceState.UnorderedAccess);
            }, ec => {
                var res = ec.Resolve(work);
                ec.List.SetComputeRootSignature(rootSig);
                ec.List.SetPipelineState(pso);
                ec.List.SetComputeRoot32BitConstant(0, Count, 0);
                ec.List.SetComputeRootUnorderedAccessView(1, res.GPUVirtualAddress);
                ec.List.Dispatch((Count + 63) / 64, 1, 1);
            });

            // Pass B (graphics): copy work -> copy. Forces the graph to derive UAV->CopySrc on 'work'
            // and CopyDst activation on 'copy' (its first use on freshly-placed/aliased memory).
            g.AddPass("RgExec.Copy", Dx12RgQueue.Graphics, b => {
                copy = b.CreateTransient(Dx12RgResourceDesc.Buffer("rg.copy", Count * sizeof(uint)));
                b.Read(work, Dx12RgResourceState.CopySrc);
                b.Write(copy, Dx12RgResourceState.CopyDst);
            }, ec => {
                ec.List.CopyBufferRegion(ec.Resolve(copy), 0, ec.Resolve(work), 0, (ulong)(Count * sizeof(uint)));
            });

            // Pass C (NeverCull readback): read 'copy' as CopySrc and copy it into the CPU readback
            // heap. NeverCull + reading 'copy' keeps both producers alive (else B and the whole chain
            // would be culled because nothing observable consumes 'copy').
            g.AddPass("RgExec.Readback", Dx12RgQueue.Graphics, b => {
                b.Read(copy, Dx12RgResourceState.CopySrc);
                b.NeverCull();
            }, ec => {
                ec.List.CopyBufferRegion(readback, 0, ec.Resolve(copy), 0, (ulong)(Count * sizeof(uint)));
            });

            g.Compile();

            // --- EXECUTE the graph (the path under test) --------------------------------------------
            // No frame open standalone -> per-pass ExecuteSync, each of which flushes the GPU. Pass
            // null Dx12FrameContext (Execute uses ctx?.FrameCounter ?? 0).
            g.Execute(null);

            // Belt-and-braces: ensure all submitted work is retired before we Map the readback.
            dev.Flush();

            // --- read back + verify EVERY element ---------------------------------------------------
            Span<uint> got = readback.Map<uint>(0, Count);
            int firstBad = -1; uint badExpected = 0, badActual = 0; int badCount = 0;
            for (int i = 0; i < Count; i++) {
                uint expected = (uint)i * 2u + 1u;
                if (got[i] != expected) {
                    if (firstBad < 0) { firstBad = i; badExpected = expected; badActual = got[i]; }
                    badCount++;
                }
            }
            readback.Unmap(0);

            if (firstBad < 0) {
                Check(true, $"all {Count} elements correct after Compute(UAV write) -> Copy(UAV->CopySrc derived) -> readback");
            } else {
                Check(false, $"{badCount}/{Count} elements wrong — first mismatch at index {firstBad}: expected {badExpected}, got {badActual}");
            }
        } catch (Exception ex) {
            ok = false;
            sb.AppendLine($"  FAIL (exception during execute path): {ex.GetType().Name}: {ex.Message}");
            sb.AppendLine(ex.StackTrace);
            try { sb.AppendLine("  D3D12 debug messages:\n" + dev.DrainDebugMessages()); } catch { }
        } finally {
            // Graph owns its placed transients + heaps + descriptor cache -> Dispose frees them.
            g?.Dispose();
            readback?.Dispose();
            pso?.Dispose();
            rootSig?.Dispose();
        }

        sb.AppendLine(ok
            ? "[Dx12RgExecuteSelfTest] PASS: graph Execute path (EmitBarriers + transient placement + Resolve + per-pass record) verified on GPU."
            : "[Dx12RgExecuteSelfTest] FAIL: graph Execute path is broken (see above).");
        return sb.ToString();
    }
}
