using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D;          // PrimitiveTopology
using Vortice.Direct3D12;
using Vortice.Dxc;
using Vortice.DXGI;

namespace BallisticEngine.DX12;

// Lumen FAZ 8 — LUMEN REFLECTIONS driver. Mirrors Dx12ReflectionsPass' RT path but swaps the per-ray closest-hit
// re-shade for the shared LumenTrace abstraction (HW TLAS or SW global-SDF → the LIT surface cache FinalLighting):
// the trace IS the reflection color (pre-lit + multi-bounce), so a mirror surface reflects the lit walls with the
// cache's GI color bleed — exactly how the Lumen screen-probe diffuse gather reads the same cache.
//
// Flow each armed frame (after the screen-probe combine at event 500):
//   CSReflect (half-res compute) → reflection target (rgb color, a strength) → [temporal EMA] → SSR PSCombine
//   (depth-aware upsample + Fresnel-lerp into ctx.SceneColor). The compute trace binding mirrors the screen probe
//   (LUMEN_TRACE_PARAMS CB + TLAS/Cards/Pages/Ranges + clipmap/FinalLighting bindless); the combine/temporal REUSE
//   Ssr.hlsl's PSCombine/PSTemporal byte-for-byte (the same shader the existing reflections pass uses), pointed at
//   the Lumen reflection buffer.
//
// The deferred pass writes NO specular IBL (UseIBLSpecular=0), so this is the sole specular reflection contributor
// when Lumen owns the frame — the existing Dx12ReflectionsPass yields (its WouldRun checks
// !Dx12LumenGiPass.ReflectionsActive(ctx)) so reflections are never double-composited.
//
// Default-off: nothing is built/allocated until the first armed frame (Ensure lazily builds PSOs + resources).
internal sealed class Dx12LumenReflections : IDisposable
{
    readonly Dx12Device dev;

    // Reflection trace (compute, HeapDirectlyIndexed so the LumenTrace include's clipmap/FinalLighting bindless reads
    // resolve from ResourceDescriptorHeap[]).
    ID3D12RootSignature reflRootSig;
    ID3D12PipelineState reflPso;
    Dx12FrameCb<ReflConstants> reflCb;

    // SSR combine + temporal (reuses Ssr.hlsl's PSCombine/PSTemporal/VSMain — identical to Dx12ReflectionsPass).
    ID3D12RootSignature ssrRootSig;
    ID3D12PipelineState ssrCombinePso, ssrTemporalPso;
    ID3D12Resource ssrCb;
    unsafe byte* ssrCbMapped;
    int ssrCbStride;
    long SsrCbOffset => (long)dev.FrameSlot * ssrCbStride;
    Dx12DescriptorHeap ssrSrvVisible;

    // Half-res reflection target + temporal history ping-pong.
    Dx12OffscreenTarget reflTarget;     // half-res RGBA16F UAV (CSReflect writes)
    Dx12OffscreenTarget reflScene;      // full-res combine output
    Dx12OffscreenTarget reflHistoryA, reflHistoryB;
    bool reflHistWriteB, reflHistValid;

    int builtW = -1, builtH = -1;
    bool built;
    bool loggedRun;

    const int ReflTableBase = Dx12BindlessTail.LumenReflTableBase;
    long reflDescStamp = -1;

    [StructLayout(LayoutKind.Sequential)]
    struct ReflConstants
    {
        // --- LumenTrace parameter block (MUST be first; the include reads these by name) ---
        public Vector3 LtClipOrigin;   public float LtVoxelSize;
        public Vector3 LtCamPosUnused; public float LtClipHalfExtent;
        public uint LtClipResX, LtClipResY, LtClipResZ; public float LtMaxTraceDist;
        public uint LtAtlasSize, LtCardCount, LtInstanceCount, LtFinalReadIdx;
        public uint LtClipmapIdx, LtFinalValid, LtHasTlas, LtSkyIdx;
        public float LtSkyIntensity, LtUseSky, LtSurfBias, LtPad0;
        // --- reflection params ---
        public Matrix4x4 InvViewProj;
        public Vector3 CameraPos;   public float Intensity;
        public Vector2 HalfTexel;   public float FrameIndex; public float PreferSW;
        public uint FullW;          public uint FullH;       public uint HalfW;            public uint HalfH;
        public float MaxRayDist;    public float NormalBias; public float RoughnessOverride; public float MetallicOverride;
        public uint DebugRaw;       public float ReflPad0;   public float ReflPad1;        public float ReflPad2;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SsrConstants
    {
        public Matrix4x4 Projection; public Matrix4x4 InvProjection; public Matrix4x4 ViewMatrix;
        public float Intensity; public Vector3 Pad;
        public Vector2 TexelSize; public Vector2 Pad2;
    }

    public Dx12LumenReflections(Dx12Device device) { dev = device; }

    static float EnvF(string name, float fallback) =>
        float.TryParse(Environment.GetEnvironmentVariable(name), System.Globalization.CultureInfo.InvariantCulture,
            out float v) ? v : fallback;

    static bool? reflTemporal;
    static bool TemporalEnabled => reflTemporal ??= Environment.GetEnvironmentVariable("BALLISTIC_DX12_LUMEN_REFL_NOTEMPORAL") != "1";

    // OPTIONAL HIT LIGHTING (BALLISTIC_DX12_LUMEN_REFL_HITLIGHT) — v1 STUB. The DEFAULT reflection color is the
    // pre-lit surface-cache FinalLighting sampled by LumenTrace at the hit (lower flicker, multi-bounce, the same
    // radiance the diffuse gather sees). The hit-lighting variant would instead re-evaluate the material at the
    // reflection hit — albedo*(sun shadow-ray + cache ambient), like DxrReflections' ClosestHit — for sharper/fresher
    // mirror reflections. That requires LumenTrace to surface the hit's geometry attributes (instance/prim/bary →
    // interpolated normal/UV + material) which the current LumenTraceResult does NOT expose (it returns only the
    // sampled cache radiance). Wiring a full re-shade means either extending LumenTrace to return a hit descriptor or
    // duplicating the RT closest-hit geometry fetch — deferred. Door is read + logged so the path is discoverable;
    // when set, this v1 falls back to the cache-sample default (documented, no behaviour change).
    static bool? hitLightLogged;
    static void NoteHitLightDoor() {
        if (hitLightLogged.HasValue) return;
        bool on = Environment.GetEnvironmentVariable("BALLISTIC_DX12_LUMEN_REFL_HITLIGHT") == "1";
        hitLightLogged = on;
        if (on) Console.WriteLine("[LumenReflections] BALLISTIC_DX12_LUMEN_REFL_HITLIGHT=1 requested — v1 STUB: " +
            "reflection color falls back to the surface-cache sample (full material re-shade at the reflection hit is a follow-up).");
    }

    // RUN the Lumen reflections. Returns true if it composited reflections. `cards` must be a valid, FinalLighting-lit
    // surface cache; `globalSdf` may be null (HW backend) but is needed for the SW backend. Caller gates on Lumen
    // armed + the reflections door.
    public unsafe bool Run(Dx12FrameContext ctx, Dx12LumenCardScene cards, Dx12GlobalSdf globalSdf)
    {
        if (ctx.SceneColor == null || ctx.GBuffer == null) return false;
        if (cards is null || !cards.Valid || cards.CardCount == 0 || !cards.FinalValid) return false;

        Dx12SceneAS sceneAS = ctx.Dxr?.SceneAS;
        bool hasTlas = sceneAS != null && sceneAS.Valid;
        bool forceSW = Environment.GetEnvironmentVariable("BALLISTIC_DX12_LUMEN_REFL_SW") == "1";
        bool preferSW = forceSW || !hasTlas;
        bool sdfReady = globalSdf != null && globalSdf.Valid && globalSdf.ClipmapSrvBindless >= 0;
        if ((preferSW && !sdfReady) || (!preferSW && !hasTlas))
        {
            if (!loggedRun) { loggedRun = true;
                Console.WriteLine($"[LumenReflections] SKIP no backend hasTlas={hasTlas} preferSW={preferSW} sdfReady={sdfReady}"); }
            return false;
        }

        EnsureBuilt();
        EnsureSized(ctx.SceneColor.Width, ctx.SceneColor.Height);
        NoteHitLightDoor();
        globalSdf?.ToPixelShaderResource();   // SW march reads the clipmap as a (bindless) SRV.

        var gbuffer = ctx.GBuffer;
        var target = ctx.SceneColor;
        gbuffer.ToShaderResource();   // depth/normal/material read from the compute stage.

        Matrix4x4.Invert(ctx.ViewProj, out Matrix4x4 invVP);
        float intensity = EnvF("BALLISTIC_DX12_LUMEN_REFL_INTENSITY", 1f);
        float maxDist = EnvF("BALLISTIC_DX12_LUMEN_REFL_MAXDIST",
            globalSdf != null ? globalSdf.ClipWorldExtent * 1.8f : 1e4f);
        int clipIdx = globalSdf?.ClipmapSrvBindless ?? -1;
        // CornellBox is matte — the FX overrides force the floor/walls reflective for the GPU verification (see brief).
        float roughOverride = EnvF("BALLISTIC_FX_ROUGHNESS", -1f);
        float metalOverride = EnvF("BALLISTIC_FX_METALLIC", -1f);
        bool debugRaw = Environment.GetEnvironmentVariable("BALLISTIC_DX12_LUMEN_REFL_DEBUG") == "1";
        float frameIndex = ctx.DeterministicCapture ? -1f : (ctx.FrameCounter & 1023);

        reflCb.Write(new ReflConstants
        {
            LtClipOrigin = globalSdf?.ClipOrigin ?? Vector3.Zero,
            LtVoxelSize = globalSdf?.ClipVoxelSize ?? 1f,
            LtClipHalfExtent = globalSdf?.ClipHalf ?? 1f,
            LtClipResX = (uint)(globalSdf?.ClipRes ?? 1), LtClipResY = (uint)(globalSdf?.ClipRes ?? 1),
            LtClipResZ = (uint)(globalSdf?.ClipRes ?? 1), LtMaxTraceDist = maxDist,
            LtAtlasSize = (uint)cards.AtlasSize, LtCardCount = (uint)cards.CardCount,
            LtInstanceCount = (uint)cards.InstanceCount, LtFinalReadIdx = (uint)Math.Max(cards.FinalReadSrvIdx, 0),
            LtClipmapIdx = (uint)Math.Max(clipIdx, 0), LtFinalValid = cards.FinalValid ? 1u : 0u,
            LtHasTlas = hasTlas ? 1u : 0u, LtSkyIdx = 0u,
            LtSkyIntensity = 0f, LtUseSky = 0f, LtSurfBias = 0.03f, LtPad0 = 0f,
            InvViewProj = Matrix4x4.Transpose(invVP),
            CameraPos = ctx.CamPos, Intensity = intensity,
            HalfTexel = new Vector2(1f / reflTarget.Width, 1f / reflTarget.Height),
            FrameIndex = frameIndex, PreferSW = preferSW ? 1f : 0f,
            FullW = (uint)ctx.SceneColor.Width, FullH = (uint)ctx.SceneColor.Height,
            HalfW = (uint)reflTarget.Width, HalfH = (uint)reflTarget.Height,
            MaxRayDist = maxDist, NormalBias = 0.05f,
            RoughnessOverride = roughOverride, MetallicOverride = metalOverride,
            DebugRaw = debugRaw ? 1u : 0u,
        });

        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        Dx12DescriptorHeap bindless = Dx12Backend.BindlessHeap;
        // Persistent reserved-tail table: t4 depth / t5 normal / t6 material SRVs + u0 reflection-target UAV.
        // Re-stamped only when a source handle changes (resize / scene swap).
        long descStamp = (long)gbuffer.DepthSrvCpu.Ptr ^ ((long)gbuffer.ColorSrvCpu(1).Ptr << 1)
            ^ ((long)gbuffer.ColorSrvCpu(2).Ptr << 2) ^ ((long)reflTarget.ColorSrvCpu.Ptr << 3);
        if (descStamp != reflDescStamp)
        {
            reflDescStamp = descStamp;
            dev.Device.CopyDescriptorsSimple(1, bindless.Cpu(ReflTableBase + 0), gbuffer.DepthSrvCpu, heapType);     // t4 depth
            dev.Device.CopyDescriptorsSimple(1, bindless.Cpu(ReflTableBase + 1), gbuffer.ColorSrvCpu(1), heapType);  // t5 normal
            dev.Device.CopyDescriptorsSimple(1, bindless.Cpu(ReflTableBase + 2), gbuffer.ColorSrvCpu(2), heapType);  // t6 material
            dev.Device.CreateUnorderedAccessView(reflTarget.RenderTarget, null, new UnorderedAccessViewDescription
            { Format = Dx12OffscreenTarget.HdrFormat, ViewDimension = UnorderedAccessViewDimension.Texture2D },
                bindless.Cpu(ReflTableBase + 3));   // u0 reflection target
        }

        if (!loggedRun) { loggedRun = true;
            Console.WriteLine($"[LumenReflections] RUN backend={(preferSW?"SW":"HW")} half={reflTarget.Width}x{reflTarget.Height} " +
                $"cards={cards.CardCount} inst={cards.InstanceCount} finalReadIdx={cards.FinalReadSrvIdx} clipIdx={clipIdx} " +
                $"maxDist={maxDist:0.#} roughOvr={roughOverride:0.##} metalOvr={metalOverride:0.##} debug={debugRaw}"); }

        ulong tlasAddr = hasTlas ? sceneAS.TlasAddress : 0;
        ulong cardAddr = cards.CardBufferGpuAddress;
        ulong pageAddr = cards.PageBufferGpuAddress;
        ulong rangeAddr = cards.RangeBufferGpuAddress != 0 ? cards.RangeBufferGpuAddress : cardAddr;
        var slotTable = bindless.Gpu(ReflTableBase);

        reflTarget.ColorToUnorderedAccess();
        dev.ExecuteSync(cl =>
        {
            cl.SetDescriptorHeaps(bindless.Heap);
            cl.SetComputeRootSignature(reflRootSig);
            cl.SetPipelineState(reflPso);
            cl.SetComputeRootConstantBufferView(0, reflCb.Gpu);
            if (tlasAddr != 0) cl.SetComputeRootShaderResourceView(1, tlasAddr);   // t0 TLAS
            cl.SetComputeRootShaderResourceView(2, cardAddr);                       // t1 Cards
            cl.SetComputeRootShaderResourceView(3, pageAddr);                       // t2 Pages
            cl.SetComputeRootShaderResourceView(4, rangeAddr);                      // t3 InstanceRanges
            cl.SetComputeRootDescriptorTable(5, slotTable);                         // t4/t5/t6 + u0
            cl.Dispatch((uint)((reflTarget.Width + 7) / 8), (uint)((reflTarget.Height + 7) / 8), 1);
        });
        reflTarget.ColorToShaderResource();

        // DEBUG view: blit the raw reflection target to scene color (depth-aware upsample with strength=1 from the
        // shader so even matte surfaces show the reflection). Uses the same SSR combine but the reflection .a is 1.
        Dx12OffscreenTarget reflForCombine = (!debugRaw && TemporalEnabled)
            ? DenoiseTemporal(ctx, gbuffer)
            : reflTarget;

        CompositeIntoScene(ctx, gbuffer, target, reflForCombine);
        frameCounter++;
        return true;
    }

    int frameCounter;

    // Reproject + EMA the half-res reflection target (Ssr.hlsl PSTemporal). Identical to Dx12ReflectionsPass.
    unsafe Dx12OffscreenTarget DenoiseTemporal(Dx12FrameContext ctx, Dx12GBuffer gbuffer)
    {
        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        Dx12OffscreenTarget histRead = reflHistWriteB ? reflHistoryA : reflHistoryB;
        Dx12OffscreenTarget histWrite = reflHistWriteB ? reflHistoryB : reflHistoryA;
        histRead.ColorToShaderResource();
        *(SsrConstants*)(ssrCbMapped + SsrCbOffset) = new SsrConstants
        {
            Intensity = reflHistValid ? 1f : 0f, TexelSize = new Vector2(1f / reflTarget.Width, 1f / reflTarget.Height),
        };
        ssrSrvVisible.Reset();
        int tb = ssrSrvVisible.AllocateRange(5);
        dev.Device.CopyDescriptorsSimple(1, ssrSrvVisible.Cpu(tb + 0), reflTarget.ColorSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, ssrSrvVisible.Cpu(tb + 1), histRead.ColorSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, ssrSrvVisible.Cpu(tb + 2), gbuffer.ColorSrvCpu(Dx12GBuffer.MotionRtIndex), heapType);
        dev.Device.CopyDescriptorsSimple(1, ssrSrvVisible.Cpu(tb + 3), reflTarget.ColorSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, ssrSrvVisible.Cpu(tb + 4), reflTarget.ColorSrvCpu, heapType);
        histWrite.RenderColorOnly(cl =>
        {
            cl.SetGraphicsRootSignature(ssrRootSig); cl.SetPipelineState(ssrTemporalPso);
            cl.SetDescriptorHeaps(ssrSrvVisible.Heap);
            cl.SetGraphicsRootConstantBufferView(0, ssrCb.GPUVirtualAddress + (ulong)SsrCbOffset);
            cl.SetGraphicsRootDescriptorTable(1, ssrSrvVisible.Gpu(tb));
            cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            cl.DrawInstanced(3, 1, 0, 0);
        });
        histWrite.ColorToShaderResource();
        reflHistWriteB = !reflHistWriteB; reflHistValid = true;
        return histWrite;
    }

    // Depth-aware upsample + Fresnel-lerp the half-res reflection into the full-res scene color (Ssr.hlsl PSCombine).
    unsafe void CompositeIntoScene(Dx12FrameContext ctx, Dx12GBuffer gbuffer, Dx12OffscreenTarget target, Dx12OffscreenTarget refl)
    {
        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        Matrix4x4 proj = ctx.Proj, view = ctx.View;
        Matrix4x4.Invert(proj, out Matrix4x4 invProj);
        target.ColorToShaderResource();
        gbuffer.DepthToShaderResource();
        *(SsrConstants*)(ssrCbMapped + SsrCbOffset) = new SsrConstants
        {
            Projection = Matrix4x4.Transpose(proj), InvProjection = Matrix4x4.Transpose(invProj),
            ViewMatrix = Matrix4x4.Transpose(view), Intensity = EnvF("BALLISTIC_DX12_LUMEN_REFL_INTENSITY", 1f),
            TexelSize = new Vector2(1f / reflTarget.Width, 1f / reflTarget.Height),
        };
        ssrSrvVisible.Reset();
        int cb = ssrSrvVisible.AllocateRange(5);
        dev.Device.CopyDescriptorsSimple(1, ssrSrvVisible.Cpu(cb + 0), target.ColorSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, ssrSrvVisible.Cpu(cb + 1), gbuffer.DepthSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, ssrSrvVisible.Cpu(cb + 2), gbuffer.ColorSrvCpu(1), heapType);
        dev.Device.CopyDescriptorsSimple(1, ssrSrvVisible.Cpu(cb + 3), gbuffer.ColorSrvCpu(2), heapType);
        dev.Device.CopyDescriptorsSimple(1, ssrSrvVisible.Cpu(cb + 4), refl.ColorSrvCpu, heapType);
        reflScene.RenderColorOnly(cl =>
        {
            cl.SetGraphicsRootSignature(ssrRootSig); cl.SetPipelineState(ssrCombinePso);
            cl.SetDescriptorHeaps(ssrSrvVisible.Heap);
            cl.SetGraphicsRootConstantBufferView(0, ssrCb.GPUVirtualAddress + (ulong)SsrCbOffset);
            cl.SetGraphicsRootDescriptorTable(1, ssrSrvVisible.Gpu(cb));
            cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            cl.DrawInstanced(3, 1, 0, 0);
        });
        reflScene.ColorToShaderResource();
        target.CopyColorFrom(reflScene);
    }

    // ---- build (lazy on first armed frame) ----
    unsafe void EnsureBuilt()
    {
        if (built) return;
        built = true;
        BuildReflPipeline();
        BuildSsrPipeline();
    }

    unsafe void BuildReflPipeline()
    {
        // CBV b0 | t0 TLAS (root SRV) | t1 Cards / t2 Pages / t3 InstanceRanges (root SRVs) |
        // table{ t4 depth, t5 normal, t6 material SRV + u0 reflection UAV } | s0/s1.
        // HeapDirectlyIndexed so the LumenTrace include resolves clipmap + FinalLighting from ResourceDescriptorHeap[].
        const DescriptorRangeFlags Vol = DescriptorRangeFlags.DataVolatile;
        var cbv0   = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        var tlas   = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(0, 0), ShaderVisibility.All);   // t0 TLAS
        var cards  = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(1, 0), ShaderVisibility.All);   // t1
        var pages  = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(2, 0), ShaderVisibility.All);   // t2
        var ranges = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(3, 0), ShaderVisibility.All);   // t3
        // table: t4 depth + t5 normal + t6 material (3 SRV, offset 0) | u0 reflection UAV (offset 3).
        var gbRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 3, baseShaderRegister: 4,
            registerSpace: 0, offsetInDescriptorsFromTableStart: 0, flags: Vol);   // t4,t5,t6
        var uavRange = new DescriptorRange1(DescriptorRangeType.UnorderedAccessView, 1, baseShaderRegister: 0,
            registerSpace: 0, offsetInDescriptorsFromTableStart: 3, flags: Vol);   // u0
        var table = new RootParameter1(new RootDescriptorTable1(gbRange, uavRange), ShaderVisibility.All);
        var clamp = new StaticSamplerDescription(ShaderVisibility.All, 0, 0)
        {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp, MaxAnisotropy = 1, ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        var wrap = new StaticSamplerDescription(ShaderVisibility.All, 1, 0)
        {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Wrap, AddressV = TextureAddressMode.Wrap,
            AddressW = TextureAddressMode.Wrap, MaxAnisotropy = 1, ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        reflRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(
                RootSignatureFlags.ConstantBufferViewShaderResourceViewUnorderedAccessViewHeapDirectlyIndexed,
                new[] { cbv0, tlas, cards, pages, ranges, table }, new[] { clamp, wrap })));

        // The shader #includes "Lumen/LumenTrace.hlsl"; there is NO DXC include handler (shaders are embedded strings)
        // → source-prepend the include in place (strip the #include line, paste the source). The established pattern.
        string inc = EmbeddedShaderSource.ReadHlsl("Lumen/LumenTrace.hlsl");
        string body = EmbeddedShaderSource.ReadHlsl("Lumen/LumenReflections.hlsl");
        body = System.Text.RegularExpressions.Regex.Replace(
            body, "(?m)^\\s*#include\\s+\"Lumen/LumenTrace\\.hlsl\".*$", inc);
        reflPso = dev.Device.CreateComputePipelineState(new ComputePipelineStateDescription
        {
            RootSignature = reflRootSig,
            ComputeShader = Dx12ShaderCompiler.Compile(DxcShaderStage.Compute, body, "CSReflect", "LumenReflections.hlsl"),
        });
        reflCb = new Dx12FrameCb<ReflConstants>(dev);
    }

    unsafe void BuildSsrPipeline()
    {
        // Reuse Ssr.hlsl's combine/temporal — CBV b0 + 5-SRV table + clamp sampler (identical to Dx12ReflectionsPass).
        var cbv = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        var srvRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 5, baseShaderRegister: 0,
            registerSpace: 0, offsetInDescriptorsFromTableStart: 0, flags: DescriptorRangeFlags.DataVolatile);
        var srvTable = new RootParameter1(new RootDescriptorTable1(srvRange), ShaderVisibility.Pixel);
        var samp = new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0)
        {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp, MaxAnisotropy = 1, ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        ssrRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, new[] { cbv, srvTable }, new[] { samp })));

        string hlsl = EmbeddedShaderSource.ReadHlsl("Ssr.hlsl");
        byte[] vs = Dx12ShaderCompiler.Compile(DxcShaderStage.Vertex, hlsl, "VSMain", "Ssr.hlsl");
        ID3D12PipelineState MakePso(string entry) => dev.Device.CreateGraphicsPipelineState(
            new GraphicsPipelineStateDescription
            {
                RootSignature = ssrRootSig, VertexShader = vs,
                PixelShader = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, hlsl, entry, "Ssr.hlsl"),
                InputLayout = null, PrimitiveTopologyType = PrimitiveTopologyType.Triangle, SampleMask = uint.MaxValue,
                RasterizerState = RasterizerDescription.CullNone, BlendState = BlendDescription.Opaque,
                DepthStencilState = DepthStencilDescription.None,
                RenderTargetFormats = new[] { Dx12OffscreenTarget.HdrFormat }, DepthStencilFormat = Format.Unknown,
                SampleDescription = new SampleDescription(1, 0),
            });
        ssrCombinePso = MakePso("PSCombine");
        ssrTemporalPso = MakePso("PSTemporal");

        ssrCbStride = (Marshal.SizeOf<SsrConstants>() + 255) & ~255;
        ssrCb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)(ssrCbStride * dev.FramesInFlight)), ResourceStates.GenericRead);
        ssrCbMapped = ssrCb.Map<byte>(0);
        ssrSrvVisible = new Dx12DescriptorHeap(dev,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 10, shaderVisible: true, framesInFlight: dev.FramesInFlight);
    }

    void EnsureSized(int w, int h)
    {
        if (reflTarget != null && builtW == w && builtH == h) return;
        builtW = w; builtH = h;
        int hw = Math.Max(1, w / 2), hh = Math.Max(1, h / 2);
        reflTarget?.Dispose();
        reflTarget = new Dx12OffscreenTarget(dev, hw, hh, withDepth: false,
            colorFormat: Dx12OffscreenTarget.HdrFormat, colorReadable: true, allowUav: true);
        reflScene?.Dispose();
        reflScene = new Dx12OffscreenTarget(dev, w, h, withDepth: false,
            colorFormat: Dx12OffscreenTarget.HdrFormat, colorReadable: true, allowUav: false);
        reflHistoryA?.Dispose(); reflHistoryB?.Dispose();
        reflHistoryA = new Dx12OffscreenTarget(dev, hw, hh, withDepth: false, colorFormat: Dx12OffscreenTarget.HdrFormat, colorReadable: true);
        reflHistoryB = new Dx12OffscreenTarget(dev, hw, hh, withDepth: false, colorFormat: Dx12OffscreenTarget.HdrFormat, colorReadable: true);
        reflHistValid = false;
        reflDescStamp = -1;
    }

    public void Dispose()
    {
        reflPso?.Dispose(); reflRootSig?.Dispose(); reflCb?.Dispose();
        ssrCombinePso?.Dispose(); ssrTemporalPso?.Dispose(); ssrRootSig?.Dispose();
        ssrCb?.Dispose(); ssrSrvVisible?.Dispose();
        reflTarget?.Dispose(); reflScene?.Dispose();
        reflHistoryA?.Dispose(); reflHistoryB?.Dispose();
    }
}
