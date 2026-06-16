using System;
using Vortice.Direct3D12;
using Vortice.Dxc;
using Vortice.DXGI;

namespace BallisticEngine.DX12;

// Self-test for the DX12 SM6.6 BINDLESS foundation (run with BALLISTIC_DX12_BINDLESS_TEST=1): creates a
// 1x1 texture with a known RGBA, mirrors its SRV into Dx12Backend.BindlessHeap, then a compute shader
// reads it via ResourceDescriptorHeap[index] (dynamic resources) and writes the texel back. Asserts the
// readback equals the known value — proving the ...HeapDirectlyIndexed root flag + ResourceDescriptorHeap
// indexing + shader-visible-heap binding work in Vortice 3.8.3, BEFORE wiring bindless into the GPU-driven
// geometry pass (one ExecuteIndirect across materials). Kept as a permanent test door.
public static class Dx12BindlessProbe {
    public static bool SelfTest(Dx12Device dev) {
        byte[] known = { 200, 100, 50, 255 };   // R,G,B,A
        bool ok = true;
        ID3D12Resource tex = null;   // held alive past the dispatch (a local would be GC-finalized = freed)
        try {
            // 1x1 R8G8B8A8_UNorm texture with the known texel (AllShaderResource for the compute read).
            int bindlessIdx = CreateKnownTexture(dev, known, out tex);

            // Compute root sig: b0 = TexIndex (root const), u0 = Out (root UAV); directly-indexed heap flag.
            var idxConst = new RootParameter1(new RootConstants(0, 0, 1), ShaderVisibility.All);
            var uav = new RootParameter1(RootParameterType.UnorderedAccessView, new RootDescriptor1(0, 0), ShaderVisibility.All);
            using ID3D12RootSignature rootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
                new RootSignatureDescription1(
                    RootSignatureFlags.ConstantBufferViewShaderResourceViewUnorderedAccessViewHeapDirectlyIndexed,
                    new[] { idxConst, uav })));

            string hlsl = EmbeddedShaderSource.ReadHlsl("BindlessProbe.hlsl");
            byte[] cs = Dx12ShaderCompiler.Compile(DxcShaderStage.Compute, hlsl, "CSMain", "BindlessProbe.hlsl");
            using ID3D12PipelineState pso = dev.Device.CreateComputePipelineState(
                new ComputePipelineStateDescription { RootSignature = rootSig, ComputeShader = cs });

            var zeros = new uint[4];
            using ID3D12Resource outBuf = dev.CreateUavBuffer<uint>(zeros, ResourceStates.UnorderedAccess);
            using ID3D12Resource outRb = dev.CreateReadbackBuffer(4 * sizeof(uint));

            dev.ExecuteSync(cl => {
                // GOTCHA: SetDescriptorHeaps MUST precede SetComputeRootSignature when using
                // ResourceDescriptorHeap — binding the bindless heap after the root sig read zeros (the
                // heap the dynamic-resource flag references wasn't bound yet). This was THE bindless bug.
                cl.SetDescriptorHeaps(Dx12Backend.BindlessHeap.Heap);
                cl.SetComputeRootSignature(rootSig);
                cl.SetPipelineState(pso);
                cl.SetComputeRoot32BitConstant(0, (uint)bindlessIdx, 0);
                cl.SetComputeRootUnorderedAccessView(1, outBuf.GPUVirtualAddress);
                cl.Dispatch(1, 1, 1);
                cl.ResourceBarrierTransition(outBuf, ResourceStates.UnorderedAccess, ResourceStates.CopySource);
                cl.CopyBufferRegion(outRb, 0, outBuf, 0, 4 * sizeof(uint));
            });

            GC.KeepAlive(tex);   // must survive the GPU read above
            Span<uint> o = outRb.Map<uint>(0, 4);
            uint r = o[0], g = o[1], b = o[2], a = o[3];
            outRb.Unmap(0);
            if (r != known[0] || g != known[1] || b != known[2] || a != known[3]) {
                ok = false;
                Console.WriteLine($"[Dx12BindlessProbe] FAIL: got ({r},{g},{b},{a}), expected ({known[0]},{known[1]},{known[2]},{known[3]}).");
            }
        } catch (Exception e) {
            ok = false;
            Console.WriteLine($"[Dx12BindlessProbe] FAIL (exception): {e.Message}");
            Console.WriteLine(dev.DrainDebugMessages());
        } finally {
            tex?.Dispose();
        }

        Console.WriteLine(ok
            ? "[Dx12BindlessProbe] PASS: ResourceDescriptorHeap (SM6.6 dynamic resources) + HeapDirectlyIndexed verified."
            : "[Dx12BindlessProbe] FAIL: bindless foundation broken (see above).");
        return ok;
    }

    // Create a 1x1 R8G8B8A8_UNorm texture with `rgba`, transition it to AllShaderResource (compute-readable),
    // make a persistent SRV, mirror it into the bindless heap, and return the bindless index.
    static unsafe int CreateKnownTexture(Dx12Device dev, byte[] rgba, out ID3D12Resource texOut) {
        const Format fmt = Format.R8G8B8A8_UNorm;
        var desc = ResourceDescription.Texture2D(fmt, 1, 1, 1, 1);
        ID3D12Resource tex = dev.Device.CreateCommittedResource(
            HeapProperties.DefaultHeapProperties, HeapFlags.None, desc, ResourceStates.CopyDest);
        texOut = tex;

        var footprints = new PlacedSubresourceFootPrint[1];
        var rowCounts = new uint[1];
        var rowSizes = new ulong[1];
        dev.Device.GetCopyableFootprints(desc, 0, 1, 0, footprints, rowCounts, rowSizes, out ulong total);

        using ID3D12Resource upload = dev.Device.CreateCommittedResource(
            HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer(total), ResourceStates.GenericRead);
        byte* dst = upload.Map<byte>(0);
        for (int i = 0; i < 4; i++) dst[(long)footprints[0].Offset + i] = rgba[i];
        upload.Unmap(0);

        dev.ExecuteUpload(cl => {
            var d = new TextureCopyLocation(tex, 0);
            var s = new TextureCopyLocation(upload, footprints[0]);
            cl.CopyTextureRegion(d, 0, 0, 0, s, null);
            cl.ResourceBarrierTransition(tex, ResourceStates.CopyDest, ResourceStates.AllShaderResource);
        });

        // Create the SRV in the persistent (CPU-only) SrvStore, then mirror it into the bindless heap via
        // CopyDescriptorsSimple — the exact path the real material table uses (textures keep their SrvStore
        // home; the bindless table is a copy). Confirms copy-into-bindless works for dynamic indexing.
        int srvIdx = Dx12Backend.SrvStore.Allocate();
        dev.Device.CreateShaderResourceView(tex, new ShaderResourceViewDescription {
            Format = fmt, ViewDimension = ShaderResourceViewDimension.Texture2D,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Texture2D = new Texture2DShaderResourceView { MipLevels = 1, MostDetailedMip = 0 },
        }, Dx12Backend.SrvStore.Cpu(srvIdx));
        return Dx12Backend.RegisterBindless(Dx12Backend.SrvStore.Cpu(srvIdx));
    }
}
