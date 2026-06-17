using System;
using System.Numerics;
using System.Runtime.InteropServices;
using BallisticEngine;         // RuntimeSet, IStaticMeshRenderer, ReflectionMode, PostProcessSettings
using Vortice.Direct3D;        // PrimitiveTopology
using Vortice.Direct3D12;
using Vortice.Dxc;             // DxcShaderStage
using Vortice.DXGI;            // Format, SampleDescription

namespace BallisticEngine.DX12;

// Reflections — the SINGLE mode-branching reflections pass (plan decision F / trap 4: per-mode passes would
// break the EnsureRtReflections-fallback, so reflections is ONE pass that branches internally). It folds the
// inline reflections block (DX12HDRenderer ~1649) + extracts the SSR resources chunk 5 DEFERRED to here:
//   - SSR (half-res view-space march → depth-aware Fresnel combine into the scene color)
//   - RT reflections (DxrReflections trace per pixel → REUSES the SSR combine) with the world-radiance hit
//     shading (sun + punctual + the DDGI world-cache field as ambient, read from ctx.Dxr.Ddgi)
//   - the rtReflWanted && EnsureRtReflections() ? DrawRtReflections : DrawSsr branch + the !sceneAS.Valid →
//     DrawSsr fallback (verbatim) — DrawRtReflections SHARES every SSR resource (ssrTarget/ssrScene/ssrCb/
//     ssrRootSig/ssrCombinePso/ssrSrvVisible) AND falls back to DrawSsr, which is exactly WHY chunk 5 deferred
//     SSR to this unified pass.
//
// Event = Reflections (600). Enabled = !Minimal && PostFX.SsrEnabled && SsrIntensity>0 (the verbatim outer-if;
// doors.Minimal forces SSR off, re-enabled at the SSR stage via the Ssr volume / a forced PostFX). The RT-vs-
// SSR branch (including the BALLISTIC_DX12_RT_REFLECTIONS env / PostFX.ReflectionMode read) lives in Record.
// The shared DXR substrate (sceneAS / device5 / dxr-availability / rtGeometry / ddgi) lives in ctx.Dxr,
// shared with RT shadows (inline) + the GI pass. Wrap ORCHESTRATION only — the DXR closest-hit is frozen.
public sealed class Dx12ReflectionsPass : IRenderPass, IDisposable {
    public Dx12RenderPassEvent Event => Dx12RenderPassEvent.Reflections;
    public string Name => "Reflections";

    // The verbatim outer-if from the inline reflections block (DX12HDRenderer ~1649). When false the pass is
    // skipped entirely (no SSR, no RT reflections) — exactly as the inline `if(...)` did.
    public bool Enabled(Dx12FrameContext ctx) =>
        !ctx.Doors.Minimal && ctx.PostFX.SsrEnabled && ctx.PostFX.SsrIntensity > 0f;

    // PHASE-2 V1: reads the G-buffer (depth + normal/roughness for the SSR march) and read-modify-writes the HDR
    // scene color (marches reflections from `target`, then CopyColorFrom(ssrScene) back into `target`). RT
    // reflections additionally use the DXR AS (inline-core in V1) — declaring G-buffer + SceneColor suffices for
    // the V1 order/cull (RT reflections excluded from the golden gate).
    public void Declare(Dx12PassBuilder b) {
        b.Read(b.Resource("GBuffer"));
        b.ReadWrite(b.Resource("SceneColor"));
    }

    readonly Dx12Device dev;

    // === SSR: half-res view-space reflection march → combine (depth-aware upsample, lerp into HDR color). ===
    ID3D12RootSignature ssrRootSig;     // SsrConstants CBV(b0) + 5-SRV table(color/depth/normal/material/ssr) + sampler
    ID3D12PipelineState ssrMarchPso, ssrCombinePso;
    ID3D12Resource ssrCb;
    unsafe byte* ssrCbMapped;
    Dx12OffscreenTarget ssrTarget;      // half-res RGBA16F reflection (rgb + strength); also RT reflections' UAV output
    Dx12OffscreenTarget ssrScene;       // full-res scratch: combine writes here, then copied back to `target`
    Dx12DescriptorHeap ssrSrvVisible;   // 5 SRVs per pass (10-slot ring)
    [StructLayout(LayoutKind.Sequential)]
    struct SsrConstants {
        public Matrix4x4 Projection; public Matrix4x4 InvProjection; public Matrix4x4 ViewMatrix;
        public float Intensity; public Vector3 Pad;
        public Vector2 TexelSize; public Vector2 Pad2;
    }

    // === DXR ray-traced reflections (Reflection volume SSR-vs-RT dropdown: PostFX.ReflectionMode) ===
    ID3D12RootSignature rtReflRootSig;          // HeapDirectlyIndexed; CBV b0/b1/b2 + table{t0-t6,u0} + root SRV t7/t8/t9/t10 + s0/s1
    ID3D12StateObject rtReflPso;
    ID3D12Resource rtReflSbt, rtReflCb, rtReflSunCb, rtReflGridCb;
    unsafe byte* rtReflCbMapped, rtReflSunCbMapped, rtReflGridCbMapped;
    bool rtReflBuilt;
    const int RtSbtSlot = 64;                   // shader-table record alignment
    // Phase-8 reflection table reserves its OWN 8-slot tail of the bindless heap, BELOW the ScreenProbe tail
    // (16368) so the four reservations (RtRefl < ScreenProbe < DDGI < RtGi) never collide. Slots 16352..16359:
    // t0 TLAS, t1 depth, t2 normal, t3 material, t4 irr cube, t5 prefilter cube, t6 DDGI irr atlas, u0 ssrTarget.
    const int RtReflTableBase = 16384 - 32;
    [StructLayout(LayoutKind.Sequential)]
    struct RtReflConstants {
        public Matrix4x4 InvViewProj; public Vector3 CameraPos; public float Intensity;
        public float PrefilterMaxMip; public float NormalBias; public float UseDdgi; public float Pad0;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct RtGiSun { public Vector3 SunDir; public float NormalBias; public Vector3 SunColor; public float LightCount; }

    // BuildSsr moved VERBATIM into the ctor (re-rooted onto dev). The SSR PSOs/CB/heap are built here; the SSR
    // targets allocate in Resize, called at the end of the ctor (the inline BuildSsr called AllocSsrTarget(); the
    // SSAO/TAA/Composite passes follow the same ctor(dev,w,h) → Resize pattern) so the first frame already has
    // valid targets. The RT-reflection pipeline (rtRefl*) stays LAZY (EnsureRtReflections on first RT use)
    // exactly as inline — DXR may be unavailable / RT reflections may never be requested.
    public unsafe Dx12ReflectionsPass(Dx12Device device, int width, int height) {
        dev = device;
        BuildSsr();
        Resize(width, height);
    }

    // ===== ENABLED-PASS RECORD: the inline reflections block (DX12HDRenderer ~1649) moved VERBATIM. =====
    // rtReflWanted && EnsureRtReflections() ? DrawRtReflections : DrawSsr. The inline TimePass("Reflections:RT")
    // tag is dropped — the GRAPH already times the pass under Name ("Reflections").
    public unsafe void Record(Dx12FrameContext ctx) {
        string rtrEnv = Environment.GetEnvironmentVariable("BALLISTIC_DX12_RT_REFLECTIONS");
        bool rtReflWanted = rtrEnv == "1" || (rtrEnv != "0" && ctx.PostFX.ReflectionMode == ReflectionMode.RayTraced);
        if (rtReflWanted && EnsureRtReflections(ctx))
            DrawRtReflections(ctx);
        else
            DrawSsr(ctx);
    }

    // ============================== SSR ==============================

    // Screen-space reflections (volume-driven): half-res view-space march reads the lit HDR color + G-buffer →
    // ssrTarget; combine depth-aware-upsamples + lerps into the scene color (via ssrScene, copied back).
    unsafe void DrawSsr(Dx12FrameContext ctx) {
        var dev = ctx.Dev; var target = ctx.SceneColor; var gbuffer = ctx.GBuffer;
        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        Matrix4x4 view = ctx.View, proj = ctx.Proj;
        Matrix4x4.Invert(proj, out Matrix4x4 invProj);
        *(SsrConstants*)ssrCbMapped = new SsrConstants {
            Projection = Matrix4x4.Transpose(proj), InvProjection = Matrix4x4.Transpose(invProj),
            ViewMatrix = Matrix4x4.Transpose(view),
            Intensity = ctx.PostFX.SsrIntensity,
            TexelSize = new Vector2(1f / ssrTarget.Width, 1f / ssrTarget.Height),
        };

        // Both passes need the HDR color + G-buffer as SRVs. The G-buffer is already SRV; bring color to SRV.
        // R2 / Decision 4: reflections is a consumer of color + G-buffer-as-SRV — head transitions live here.
        target.ColorToShaderResource();
        gbuffer.DepthToShaderResource();

        // March (half-res) → ssrTarget. SRV slots: color t0, depth t1, normal t2, material t3, (ssr t4 unused).
        ssrSrvVisible.Reset();
        int mb = ssrSrvVisible.AllocateRange(5);
        dev.Device.CopyDescriptorsSimple(1, ssrSrvVisible.Cpu(mb + 0), target.ColorSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, ssrSrvVisible.Cpu(mb + 1), gbuffer.DepthSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, ssrSrvVisible.Cpu(mb + 2), gbuffer.ColorSrvCpu(1), heapType);
        dev.Device.CopyDescriptorsSimple(1, ssrSrvVisible.Cpu(mb + 3), gbuffer.ColorSrvCpu(2), heapType);
        dev.Device.CopyDescriptorsSimple(1, ssrSrvVisible.Cpu(mb + 4), ssrTarget.ColorSrvCpu, heapType);
        ssrTarget.RenderColorOnly(cl => {
            cl.SetGraphicsRootSignature(ssrRootSig); cl.SetPipelineState(ssrMarchPso);
            cl.SetDescriptorHeaps(ssrSrvVisible.Heap);
            cl.SetGraphicsRootConstantBufferView(0, ssrCb.GPUVirtualAddress);
            cl.SetGraphicsRootDescriptorTable(1, ssrSrvVisible.Gpu(mb));
            cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            cl.DrawInstanced(3, 1, 0, 0);
        });

        // Combine (full-res) → ssrScene, reading scene color (t0), depth (t1), ssrTarget (t4).
        ssrTarget.ColorToShaderResource();
        int cb = ssrSrvVisible.AllocateRange(5);
        dev.Device.CopyDescriptorsSimple(1, ssrSrvVisible.Cpu(cb + 0), target.ColorSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, ssrSrvVisible.Cpu(cb + 1), gbuffer.DepthSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, ssrSrvVisible.Cpu(cb + 2), gbuffer.ColorSrvCpu(1), heapType);
        dev.Device.CopyDescriptorsSimple(1, ssrSrvVisible.Cpu(cb + 3), gbuffer.ColorSrvCpu(2), heapType);
        dev.Device.CopyDescriptorsSimple(1, ssrSrvVisible.Cpu(cb + 4), ssrTarget.ColorSrvCpu, heapType);
        ssrScene.RenderColorOnly(cl => {
            cl.SetGraphicsRootSignature(ssrRootSig); cl.SetPipelineState(ssrCombinePso);
            cl.SetDescriptorHeaps(ssrSrvVisible.Heap);
            cl.SetGraphicsRootConstantBufferView(0, ssrCb.GPUVirtualAddress);
            cl.SetGraphicsRootDescriptorTable(1, ssrSrvVisible.Gpu(cb));
            cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            cl.DrawInstanced(3, 1, 0, 0);
        });
        ssrScene.ColorToShaderResource();
        target.CopyColorFrom(ssrScene);   // the reflected scene becomes the new scene color
    }

    // ============================== RT reflections ==============================

    // Lazily build the DXR reflection pipeline. Reuses the shared device5 + sceneAS + rtGeometry (ctx.Dxr).
    // Returns false (→ SSR fallback) when DXR is unavailable.
    unsafe bool EnsureRtReflections(Dx12FrameContext ctx) {
        if (!ctx.Dxr.CheckAvailable("RTReflections")) return false;
        if (rtReflBuilt) return true;
        rtReflBuilt = true;

        var dev = ctx.Dev;
        var device5 = ctx.Dxr.Device5;

        // PHASE 8 root sig (mirrors rtGiRootSig — the closest-hit decodes the hit bindlessly via
        // ResourceDescriptorHeap[], so HeapDirectlyIndexed + the table descriptors live in the bindless tail):
        //   CBV b0 ReflConstants | CBV b1 RtGiSun | CBV b2 DdgiGrid | table{SRV t0-t6, UAV u0} |
        //   root SRV t7 GpuMaterials | t8 RtInstance[] | t9 Lights | t10 ProbeState + static clamp s0 + wrap s1.
        var cbv0 = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        var cbv1 = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(1, 0), ShaderVisibility.All);
        var cbv2 = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(2, 0), ShaderVisibility.All);
        var srvRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 7, baseShaderRegister: 0);  // t0-t6
        var uavRange = new DescriptorRange1(DescriptorRangeType.UnorderedAccessView, 1, baseShaderRegister: 0);
        var table = new RootParameter1(new RootDescriptorTable1(srvRange, uavRange), ShaderVisibility.All);
        var matSrv = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(7, 0), ShaderVisibility.All);
        var instSrv = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(8, 0), ShaderVisibility.All);
        var lightSrv = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(9, 0), ShaderVisibility.All);
        var probeSrv = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(10, 0), ShaderVisibility.All);
        var clampSamp = new StaticSamplerDescription(ShaderVisibility.All, 0, 0) {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp, MaxAnisotropy = 1,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        var wrapSamp = new StaticSamplerDescription(ShaderVisibility.All, 1, 0) {   // albedo texture sampling at hits
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Wrap,
            AddressV = TextureAddressMode.Wrap, AddressW = TextureAddressMode.Wrap, MaxAnisotropy = 1,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        rtReflRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(
                RootSignatureFlags.ConstantBufferViewShaderResourceViewUnorderedAccessViewHeapDirectlyIndexed,
                new[] { cbv0, cbv1, cbv2, table, matSrv, instSrv, lightSrv, probeSrv }, new[] { clampSamp, wrapSamp })));

        string hlsl = BallisticEngine.DX12.EmbeddedShaderSource.ReadHlsl("DxrReflections.hlsl");
        byte[] dxil = Dx12ShaderCompiler.Compile(DxcShaderStage.Library, hlsl, "", "DxrReflections.hlsl");
        var subs = new[] {
            new StateSubObject(new DxilLibraryDescription(dxil,
                new ExportDescription("RayGen"), new ExportDescription("Miss"), new ExportDescription("ClosestHit"))),
            new StateSubObject(new HitGroupDescription("HitGroup", HitGroupType.Triangles, "", "ClosestHit", "")),
            new StateSubObject(new RaytracingShaderConfig(16, 8)),   // payload = float3 color + float roughness
            new StateSubObject(new RaytracingPipelineConfig(1)),
            new StateSubObject(new GlobalRootSignature(rtReflRootSig)),
        };
        rtReflPso = device5.CreateStateObject(new StateObjectDescription(StateObjectType.RaytracingPipeline, subs));

        using ID3D12StateObjectProperties props = rtReflPso.QueryInterface<ID3D12StateObjectProperties>();
        uint idSize = Vortice.Direct3D12.D3D12.ShaderIdentifierSizeInBytes;
        rtReflSbt = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer(RtSbtSlot * 3), ResourceStates.GenericRead);
        byte* sp = rtReflSbt.Map<byte>(0);
        System.Runtime.CompilerServices.Unsafe.CopyBlock(sp + 0 * RtSbtSlot, (void*)props.GetShaderIdentifier("RayGen"), idSize);
        System.Runtime.CompilerServices.Unsafe.CopyBlock(sp + 1 * RtSbtSlot, (void*)props.GetShaderIdentifier("Miss"), idSize);
        System.Runtime.CompilerServices.Unsafe.CopyBlock(sp + 2 * RtSbtSlot, (void*)props.GetShaderIdentifier("HitGroup"), idSize);
        rtReflSbt.Unmap(0);

        int cbSize = (Marshal.SizeOf<RtReflConstants>() + 255) & ~255;
        rtReflCb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)cbSize), ResourceStates.GenericRead);
        rtReflCbMapped = rtReflCb.Map<byte>(0);
        int sunSize = (Marshal.SizeOf<RtGiSun>() + 255) & ~255;
        rtReflSunCb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)sunSize), ResourceStates.GenericRead);
        rtReflSunCbMapped = rtReflSunCb.Map<byte>(0);
        int gridSize = (Marshal.SizeOf<Dx12Ddgi.DdgiConstants>() + 255) & ~255;
        rtReflGridCb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)gridSize), ResourceStates.GenericRead);
        rtReflGridCbMapped = rtReflGridCb.Map<byte>(0);
        _ = ctx.Dxr.RtGeometry;   // was `rtGeometry ??= new Dx12RtGeometry(dev)` — reuse if GI already built it.
        return true;
    }

    // RT reflections: trace a reflection ray per pixel → ssrTarget (reflected color + strength), then reuse the
    // SSR combine. PHASE 8: the hit is shaded with REAL world radiance (sun + punctual + the DDGI world-cache
    // field as ambient), so this needs the bindless geo/material table + the DDGI atlas/grid/ProbeState — bound
    // EXACTLY like DrawRtGi (the renderer is fully synchronous, so the DDGI atlas the GI pass wrote this frame
    // is fully drained before the reflection pass reads it).
    unsafe void DrawRtReflections(Dx12FrameContext ctx) {
        var dev = ctx.Dev; var target = ctx.SceneColor; var gbuffer = ctx.GBuffer; var ibl = ctx.Ibl;
        var gpuDriven = ctx.GpuDriven; var clusteredLights = ctx.ClusteredLights;
        var sceneAS = ctx.Dxr.SceneAS; var rtGeometry = ctx.Dxr.RtGeometry; var ddgi = ctx.Dxr.Ddgi;
        Matrix4x4 view = ctx.View, viewProj = ctx.ViewProj, proj = ctx.Proj;
        Vector3 camPos = ctx.CamPos, lightDir = ctx.LightDir, lightColor = ctx.LightColor;

        sceneAS.Ensure(RuntimeSet<IStaticMeshRenderer>.ReadOnlyCollection);
        if (!sceneAS.Valid) { DrawSsr(ctx); return; }   // no geometry → fall back to SSR

        // The world-radiance hit shading reads the bindless material table + per-instance geometry SRVs (same as
        // RT-GI) — ensure they're fresh (stamp-cached no-ops if the geometry pass already built them).
        gpuDriven.EnsureMaterialTable(ctx.WholeMeshRenderers);
        rtGeometry.Ensure(RuntimeSet<IStaticMeshRenderer>.ReadOnlyCollection, gpuDriven);

        // Sample the DDGI world cache at hits when it's allocated this frame (DDGI on). Without it, the hit
        // ambient falls back to the flat IBL irradiance cube (UseDdgi=0) — a graceful no-DDGI path.
        bool useDdgi = DdgiEnabled(ctx) && ddgi != null && ddgi.Allocated;

        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        Matrix4x4.Invert(viewProj, out Matrix4x4 invVP);
        *(RtReflConstants*)rtReflCbMapped = new RtReflConstants {
            InvViewProj = Matrix4x4.Transpose(invVP), CameraPos = camPos, Intensity = ctx.PostFX.SsrIntensity,
            PrefilterMaxMip = ibl != null ? ibl.PrefilterMipCount - 1 : 0f, NormalBias = 0.05f,
            UseDdgi = useDdgi ? 1f : 0f,
            // Pad0 = emissiveEnable — reflected emissive surfaces (neon in a mirror) light up when >0.5.
            Pad0 = GiEmissiveEnabled(ctx) ? 1f : 0f,
        };
        Vector3 sunDir = lightDir.LengthSquared() < 1e-8f ? Vector3.UnitY : Vector3.Normalize(lightDir);
        *(RtGiSun*)rtReflSunCbMapped = new RtGiSun {
            SunDir = sunDir, NormalBias = 0.03f, SunColor = lightColor, LightCount = clusteredLights.LightCount,
        };
        // The DDGI grid description for SampleDdgiField at the hit (origin/spacing/dims + irrTexels/normalBias).
        *(Dx12Ddgi.DdgiConstants*)rtReflGridCbMapped = useDdgi ? ddgi.GridConstants() : default;

        // The G-buffer is in the combined shader-read state; color (target) bring to SRV for the combine.
        target.ColorToShaderResource();
        // The DXR raygen samples depth (t1) from the NON-PIXEL stage — promote it (fog/SSGI leave depth in
        // PixelShaderResource only). The combine's back-half re-transitions depth (DepthToShaderResource below).
        gbuffer.DepthToNonPixelShaderResource();

        // The table descriptors live in the bindless heap's reserved tail (so the one bound CBV/SRV/UAV heap
        // serves BOTH the table AND the closest-hit's ResourceDescriptorHeap[] bindless geo/material reads).
        Dx12DescriptorHeap bindless = Dx12Backend.BindlessHeap;
        sceneAS.CreateTlasSrv(bindless.Cpu(RtReflTableBase + 0));                                            // t0 TLAS
        dev.Device.CopyDescriptorsSimple(1, bindless.Cpu(RtReflTableBase + 1), gbuffer.DepthSrvCpu, heapType);     // t1 depth
        dev.Device.CopyDescriptorsSimple(1, bindless.Cpu(RtReflTableBase + 2), gbuffer.ColorSrvCpu(1), heapType);  // t2 world normal
        dev.Device.CopyDescriptorsSimple(1, bindless.Cpu(RtReflTableBase + 3), gbuffer.ColorSrvCpu(2), heapType);  // t3 material
        dev.Device.CopyDescriptorsSimple(1, bindless.Cpu(RtReflTableBase + 4), ibl.IrradianceSrv, heapType);       // t4 irr cube
        dev.Device.CopyDescriptorsSimple(1, bindless.Cpu(RtReflTableBase + 5), ibl.PrefilterSrv, heapType);        // t5 prefilter cube
        // t6 DDGI irradiance atlas (the hit's ambient field). When DDGI is off bind a Texture2D SRV (the
        // G-buffer depth) as an inert stand-in so the descriptor TYPE matches the shader's Texture2D<float4> slot.
        if (useDdgi)
            dev.Device.CreateShaderResourceView(ddgi.IrradianceTex, new ShaderResourceViewDescription {
                Format = Format.R16G16B16A16_Float, ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
                Shader4ComponentMapping = ShaderComponentMapping.Default,
                Texture2D = new Texture2DShaderResourceView { MipLevels = 1 },
            }, bindless.Cpu(RtReflTableBase + 6));
        else
            dev.Device.CopyDescriptorsSimple(1, bindless.Cpu(RtReflTableBase + 6), gbuffer.DepthSrvCpu, heapType);
        dev.Device.CreateUnorderedAccessView(ssrTarget.RenderTarget, null, new UnorderedAccessViewDescription {
            Format = Dx12OffscreenTarget.HdrFormat, ViewDimension = UnorderedAccessViewDimension.Texture2D,
        }, bindless.Cpu(RtReflTableBase + 7));                                                               // u0 ssrTarget

        ssrTarget.ColorToUnorderedAccess();
        // DDGI atlas (UnorderedAccess between passes) → NonPixelSRV for the closest-hit's field read; restore
        // after. ProbeState (t10 root SRV) is read in its UAV state, same as the screen-probe trace does.
        if (useDdgi)
            dev.ExecuteSync(cl => cl.ResourceBarrierTransition(ddgi.IrradianceTex,
                ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource));

        uint idSize = Vortice.Direct3D12.D3D12.ShaderIdentifierSizeInBytes;
        dev.ExecuteSync(cl => {
            cl.SetDescriptorHeaps(bindless.Heap);   // bindless heap = the bound CBV/SRV/UAV heap (table + ResourceDescriptorHeap[])
            cl.SetComputeRootSignature(rtReflRootSig);
            cl.SetPipelineState1(rtReflPso);
            cl.SetComputeRootConstantBufferView(0, rtReflCb.GPUVirtualAddress);
            cl.SetComputeRootConstantBufferView(1, rtReflSunCb.GPUVirtualAddress);
            cl.SetComputeRootConstantBufferView(2, rtReflGridCb.GPUVirtualAddress);
            cl.SetComputeRootDescriptorTable(3, bindless.Gpu(RtReflTableBase));
            cl.SetComputeRootShaderResourceView(4, gpuDriven.MaterialsGpuAddress);       // t7 GpuMaterials
            cl.SetComputeRootShaderResourceView(5, rtGeometry.InstancesGpuAddress);      // t8 RtInstance[]
            cl.SetComputeRootShaderResourceView(6, clusteredLights.LightBufGpuAddress);  // t9 punctual lights
            cl.SetComputeRootShaderResourceView(7, useDdgi ? ddgi.ProbeStateGpuAddress   // t10 ProbeState (DDGI on)
                                                           : clusteredLights.LightBufGpuAddress);  // inert when off (never read)
            cl.DispatchRays(new DispatchRaysDescription {
                Width = (uint)ssrTarget.Width, Height = (uint)ssrTarget.Height, Depth = 1,
                RayGenerationShaderRecord = new GpuVirtualAddressRange { StartAddress = rtReflSbt.GPUVirtualAddress, SizeInBytes = idSize },
                MissShaderTable = new GpuVirtualAddressRangeAndStride { StartAddress = rtReflSbt.GPUVirtualAddress + RtSbtSlot, SizeInBytes = idSize, StrideInBytes = idSize },
                HitGroupTable = new GpuVirtualAddressRangeAndStride { StartAddress = rtReflSbt.GPUVirtualAddress + 2 * RtSbtSlot, SizeInBytes = idSize, StrideInBytes = idSize },
            });
        });
        if (useDdgi)
            dev.ExecuteSync(cl => cl.ResourceBarrierTransition(ddgi.IrradianceTex,
                ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess));
        ssrTarget.ColorToShaderResource();

        // Reuse the SSR combine (depth-aware upsample + Fresnel lerp into the scene color).
        Matrix4x4.Invert(proj, out Matrix4x4 invProj);
        *(SsrConstants*)ssrCbMapped = new SsrConstants {
            Projection = Matrix4x4.Transpose(proj), InvProjection = Matrix4x4.Transpose(invProj),
            ViewMatrix = Matrix4x4.Transpose(view), Intensity = ctx.PostFX.SsrIntensity,
            TexelSize = new Vector2(1f / ssrTarget.Width, 1f / ssrTarget.Height),
        };
        gbuffer.DepthToShaderResource();
        ssrSrvVisible.Reset();
        int cb = ssrSrvVisible.AllocateRange(5);
        dev.Device.CopyDescriptorsSimple(1, ssrSrvVisible.Cpu(cb + 0), target.ColorSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, ssrSrvVisible.Cpu(cb + 1), gbuffer.DepthSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, ssrSrvVisible.Cpu(cb + 2), gbuffer.ColorSrvCpu(1), heapType);
        dev.Device.CopyDescriptorsSimple(1, ssrSrvVisible.Cpu(cb + 3), gbuffer.ColorSrvCpu(2), heapType);
        dev.Device.CopyDescriptorsSimple(1, ssrSrvVisible.Cpu(cb + 4), ssrTarget.ColorSrvCpu, heapType);
        ssrScene.RenderColorOnly(cl => {
            cl.SetGraphicsRootSignature(ssrRootSig); cl.SetPipelineState(ssrCombinePso);
            cl.SetDescriptorHeaps(ssrSrvVisible.Heap);
            cl.SetGraphicsRootConstantBufferView(0, ssrCb.GPUVirtualAddress);
            cl.SetGraphicsRootDescriptorTable(1, ssrSrvVisible.Gpu(cb));
            cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            cl.DrawInstanced(3, 1, 0, 0);
        });
        ssrScene.ColorToShaderResource();
        target.CopyColorFrom(ssrScene);
    }

    // ============================== build + helpers ==============================

    // BuildSsr moved VERBATIM. The march + combine PSOs share one rootsig + CB; the half-res reflection target +
    // full-res combine scratch allocate in Resize.
    unsafe void BuildSsr() {
        var cbv = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        var srvRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 5, baseShaderRegister: 0);
        var srvTable = new RootParameter1(new RootDescriptorTable1(srvRange), ShaderVisibility.Pixel);
        var samp = new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0) {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp, MaxAnisotropy = 1,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        ssrRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, new[] { cbv, srvTable }, new[] { samp })));

        string hlsl = BallisticEngine.DX12.EmbeddedShaderSource.ReadHlsl("Ssr.hlsl");
        byte[] vs = Dx12ShaderCompiler.Compile(DxcShaderStage.Vertex, hlsl, "VSMain", "Ssr.hlsl");
        ID3D12PipelineState MakePso(string entry, Format rtFmt) => dev.Device.CreateGraphicsPipelineState(
            new GraphicsPipelineStateDescription {
                RootSignature = ssrRootSig, VertexShader = vs,
                PixelShader = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, hlsl, entry, "Ssr.hlsl"),
                InputLayout = null, PrimitiveTopologyType = PrimitiveTopologyType.Triangle, SampleMask = uint.MaxValue,
                RasterizerState = RasterizerDescription.CullNone, BlendState = BlendDescription.Opaque,
                DepthStencilState = DepthStencilDescription.None,
                RenderTargetFormats = new[] { rtFmt }, DepthStencilFormat = Format.Unknown,
                SampleDescription = new SampleDescription(1, 0),
            });
        ssrMarchPso = MakePso("PSMarch", Dx12OffscreenTarget.HdrFormat);
        ssrCombinePso = MakePso("PSCombine", Dx12OffscreenTarget.HdrFormat);

        int cbSize = (Marshal.SizeOf<SsrConstants>() + 255) & ~255;
        ssrCb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)cbSize), ResourceStates.GenericRead);
        ssrCbMapped = ssrCb.Map<byte>(0);
        ssrSrvVisible = new Dx12DescriptorHeap(dev,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 10, shaderVisible: true, framesInFlight: dev.FramesInFlight);
    }

    // AllocSsrTarget moved VERBATIM into Resize (graph.Resize fans this out in registration order, R5).
    public void Resize(int w, int h) {
        ssrTarget?.Dispose(); ssrScene?.Dispose();
        int hw = Math.Max(1, w / 2), hh = Math.Max(1, h / 2);
        // allowUav so RT reflections can write it via a UAV (SSR still writes it via the RTV).
        ssrTarget = new Dx12OffscreenTarget(dev, hw, hh, withDepth: false,
            colorFormat: Dx12OffscreenTarget.HdrFormat, colorReadable: true, allowUav: true);
        // Full-res scratch for the combine output (combine reads `target`, can't read+write it).
        ssrScene = new Dx12OffscreenTarget(dev, w, h, withDepth: false,
            colorFormat: Dx12OffscreenTarget.HdrFormat, colorReadable: true);
    }

    // --- helpers replicated from the orchestrator (read env / ctx.PostFX; identical semantics) ---
    string ddgiEnvCached; bool ddgiEnvRead;
    bool DdgiEnabled(Dx12FrameContext ctx) {
        if (!ddgiEnvRead) { ddgiEnvCached = Environment.GetEnvironmentVariable("BALLISTIC_DX12_DDGI"); ddgiEnvRead = true; }
        return ddgiEnvCached is null ? ctx.PostFX.Ddgi : ddgiEnvCached == "1";
    }

    string giEmissiveEnvCached; bool giEmissiveEnvRead;
    bool GiEmissiveEnabled(Dx12FrameContext ctx) {
        if (!giEmissiveEnvRead) {
            giEmissiveEnvCached = Environment.GetEnvironmentVariable("BALLISTIC_DX12_GI_EMISSIVE");
            giEmissiveEnvRead = true;
        }
        return giEmissiveEnvCached is null ? ctx.PostFX.GiEmissive : giEmissiveEnvCached != "0";
    }

    public void Dispose() {
        ssrMarchPso?.Dispose(); ssrCombinePso?.Dispose();
        ssrRootSig?.Dispose(); ssrCb?.Dispose(); ssrSrvVisible?.Dispose();
        ssrTarget?.Dispose(); ssrScene?.Dispose();
        rtReflPso?.Dispose(); rtReflRootSig?.Dispose();
        rtReflSbt?.Dispose(); rtReflCb?.Dispose(); rtReflSunCb?.Dispose(); rtReflGridCb?.Dispose();
    }
}
