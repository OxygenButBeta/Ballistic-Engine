using System.Numerics;
using BallisticEngine.DX12;
using BallisticEngine.Rendering;   // BatchGroup<T>
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.Dxc;
using Vortice.DXGI;
using GLMatrix4 = OpenTK.Mathematics.Matrix4;
using GLVector3 = OpenTK.Mathematics.Vector3;

namespace BallisticEngine;

// The DX12 forward renderer. Minimal opaque path (first light on a real scene): iterate the scene's
// static mesh renderers, draw each submesh with its material's diffuse map under a directional N·L +
// ambient, ACES-tonemapped, into an offscreen color+depth target. NO shadows/IBL/full-PBR/post yet —
// those layer on in later milestones (Docs/Plans/dx-native-abstraction-redesign.md). This proves the
// real path end-to-end: engine mesh buffers -> input layout -> per-draw CBV + per-material SRV table ->
// depth-tested draw -> readback.
//
// Drives shading via constant buffers + descriptor tables directly (NOT the GL per-name uniform API),
// and uses NO reflection on the per-frame path (standing rule): it iterates a typed RuntimeSet and reads
// typed properties only.
public sealed class DX12HDRenderer : HDRenderer {
    readonly Dx12Device dev;
    Dx12OffscreenTarget target;       // HDR scene color (R16F) + depth — opaque/sky/fog render here
    Dx12OffscreenTarget ldr;          // LDR composite output (R8) — readback/display reads this
    int targetW = 1920, targetH = 1080;

    ID3D12RootSignature rootSig;
    ID3D12PipelineState pso;

    // --- Clustered-deferred path ---
    // Geometry pass: writes the fat G-buffer (4 MRT) with the same vertex transform + material sampling as
    // the old forward opaque, but NO lighting (GBuffer.hlsl). Reuses the per-draw DrawConstants CBV (b0) +
    // 6 material SRVs (t0..t5) — same root sig shape as the forward path minus the IBL/shadow/frame params.
    Dx12GBuffer gbuffer;
    ID3D12RootSignature gbufferRootSig;
    ID3D12PipelineState gbufferPso;

    // Deferred lighting pass: fullscreen, reads the G-buffer + depth → PBR sun + IBL + shadows → HDR target
    // (DeferredLighting.hlsl). The lighting math moved here out of the material shader.
    ID3D12RootSignature deferredRootSig;   // LightConstants CBV(b0) + FrameConstants CBV(b1) + 9-SRV table(t0..t8) + sampler
    ID3D12PipelineState deferredPso;
    ID3D12Resource deferredCb;
    unsafe byte* deferredCbMapped;
    Dx12DescriptorHeap deferredSrvVisible;  // 9 SRVs copied per frame: G0..G3, depth, irradiance, prefilter, BRDF, shadow

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct LightConstants {
        public Matrix4x4 InvViewProj;
        public Matrix4x4 View;
        public Vector3 LightDir; public float Pad0;
        public Vector3 LightColor; public float Pad1;
        public Vector3 Ambient; public float Pad2;
        public Vector3 CameraPos; public float UseIBL;
        public float PrefilterMaxMip;
        public float PunctualCount;
        public Vector2 ScreenSize;
        public Vector2 ClusterNearFar;
        public Vector2 Pad3;
    }

    // Clustered punctual lights (point/spot) shaded in the deferred pass.
    Dx12ClusteredLights clusteredLights;

    // TAA: jittered rendering + reprojected history accumulation (the AA; also smooths SSR/SSAO noise).
    // The jitter is applied to the camera projection (whole frame); reprojection uses UNJITTERED matrices.
    // Driven by the AntiAliasing VOLUME (PostFX.TaaEnabled / TaaFeedback). The jitter offset is reused by
    // the FSR upscaler later (plumbed once here).
    ID3D12RootSignature taaRootSig;     // TaaConstants CBV(b0) + 3-SRV table(current/history/depth) + sampler
    ID3D12PipelineState taaPso;
    ID3D12Resource taaCb;
    unsafe byte* taaCbMapped;
    Dx12OffscreenTarget taaHistoryA, taaHistoryB;   // ping-pong accumulated HDR history
    Dx12OffscreenTarget taaResolved;                // this frame's TAA output (→ history + copied to target)
    Dx12DescriptorHeap taaSrvVisible;   // 3 SRVs per frame
    bool taaWriteB;                     // ping-pong toggle
    bool taaHistoryValid;
    int taaFrame;                       // jitter phase counter
    Matrix4x4 taaPrevViewProj;          // previous frame's UNJITTERED view*proj
    Vector2 currentJitter;              // this frame's sub-pixel jitter (pixels) — exposed for FSR reuse
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct TaaConstants {
        public Matrix4x4 CurrInvViewProj; public Matrix4x4 PrevViewProj;
        public float Feedback; public float ValidHistory; public Vector2 TexelSize;
    }

    // SSR: half-res view-space reflection march (reads HDR color + G-buffer) → combine (depth-aware
    // upsample, lerp into the HDR color). Driven by the ScreenSpaceReflections VOLUME (PostFX.Ssr*).
    ID3D12RootSignature ssrRootSig;     // SsrConstants CBV(b0) + 5-SRV table(color/depth/normal/material/ssr) + sampler
    ID3D12PipelineState ssrMarchPso, ssrCombinePso;
    ID3D12Resource ssrCb;
    unsafe byte* ssrCbMapped;
    Dx12OffscreenTarget ssrTarget;      // half-res RGBA16F reflection (rgb + strength)
    Dx12OffscreenTarget ssrScene;       // full-res scratch: combine writes here, then copied back to `target`
    Dx12DescriptorHeap ssrSrvVisible;   // 5 SRVs per pass
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct SsrConstants {
        public Matrix4x4 Projection; public Matrix4x4 InvProjection; public Matrix4x4 ViewMatrix;
        public float Intensity; public Vector3 Pad;
        public Vector2 TexelSize; public Vector2 Pad2;
    }

    // Camera projection near/far — shared by the projection build AND the froxel log-Z grid (must match).
    const float CameraNear = 0.1f, CameraFar = 1000f;

    // Final composite (HDR scene → exposure → ACES → +bloom → sRGB → LDR).
    ID3D12RootSignature compositeRootSig;   // CompositeConstants CBV (b0) + HDR+bloom SRV table + sampler
    ID3D12PipelineState compositePso;
    ID3D12Resource compositeCb;
    unsafe byte* compositeCbMapped;
    Dx12DescriptorHeap compositeSrvVisible;  // HDR color + bloom + avg-lum, copied per frame
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct CompositeConstants {
        public float Exposure; public float BloomIntensity; public float AutoExposure; public float ExposureKey;
        public float UseAo; public Vector3 Pad2;
    }

    // Auto-exposure: a 1×1 R16F target holding the geometric-mean scene luminance (LumAverage.hlsl).
    ID3D12RootSignature lumRootSig;     // 1 HDR SRV (t0) + sampler
    ID3D12PipelineState lumPso;
    Dx12OffscreenTarget lumTarget;      // 1×1 R16F, color-readable
    Dx12DescriptorHeap lumSrvVisible;   // HDR color SRV copied per frame

    // Bloom: bright-pass + separable blur at half-res, fed into the composite (Bloom.hlsl).
    ID3D12RootSignature bloomRootSig;   // BloomConstants CBV (b0) + 1 source SRV (t0) + sampler
    ID3D12PipelineState bloomBrightPso, bloomBlurHPso, bloomBlurVPso;
    Dx12OffscreenTarget bloomA, bloomB; // half-res R16F ping-pong
    ID3D12Resource bloomCb;
    unsafe byte* bloomCbMapped;
    Dx12DescriptorHeap bloomSrvVisible; // source SRV per sub-pass (3 slots)
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct BloomConstants { public float Threshold; public Vector2 TexelSize; public float Pad; }
    bool bloomThisFrame;

    // SSAO: HBAO from depth → half-res AO target (+ separable blur), multiplied in the composite.
    ID3D12RootSignature ssaoRootSig;    // SsaoConstants CBV (b0) + 1 SRV (t0: depth, then AO for blur) + sampler
    ID3D12PipelineState ssaoPso, ssaoBlurHPso, ssaoBlurVPso;
    Dx12OffscreenTarget ssaoA, ssaoB;   // half-res R8 ping-pong
    ID3D12Resource ssaoCb;
    unsafe byte* ssaoCbMapped;
    Dx12DescriptorHeap ssaoSrvVisible;  // depth/AO source per sub-pass (3 slots)
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct SsaoConstants {
        public Matrix4x4 Projection; public Matrix4x4 InvProjection; public Matrix4x4 View;
        public float Radius; public float Intensity; public Vector2 TexelSize;
    }

    // Skybox pass (background): its own root sig (CBV b0 + cube SRV t0 + clamp sampler) + PSO (LEqual,
    // no depth write, cull none, SV_VertexID cube). Drawn after opaque in the same command list.
    ID3D12RootSignature skyRootSig;
    ID3D12PipelineState skyPso;
    ID3D12Resource skyCb;          // upload heap, one SkyboxConstants, rewritten per frame
    unsafe byte* skyCbMapped;
    Dx12DescriptorHeap skySrvVisible;   // one cube SRV copied per frame

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct SkyboxConstants {
        public Matrix4x4 ViewProjNoTranslate;
        public Matrix4x4 SkyRotation;
        public float Exposure; public Vector3 Pad;
    }

    // Procedural sky pass (atmosphere marched per-pixel; no cubemap, no SRV — pure ALU).
    ID3D12RootSignature procSkyRootSig;
    ID3D12PipelineState procSkyPso;
    ID3D12Resource procSkyCb;
    unsafe byte* procSkyCbMapped;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct ProcSkyConstants {
        public Matrix4x4 ViewProjNoTranslate;
        public Vector3 SunDirection; public float SunAngularRadius;
        public Vector3 SunRadiance; public float SunDiskIntensity;
        public Vector3 GroundAlbedo; public float AirDensity;
        public float Haze, HazeAnisotropy, OzoneDensity, MultiScatter;
        public float Exposure; public Vector3 Pad;
    }

    // Per-draw constant buffer ring: one upload heap sub-allocated in 256-byte slots, one slot per draw.
    ID3D12Resource cbRing;
    int cbSlotSize;
    int cbSlotCount;
    unsafe byte* cbMapped;

    // Shader-visible SRV heap: per draw we copy the material's diffuse SRV into the next slot and point
    // the root descriptor table at it. Reset each frame.
    Dx12DescriptorHeap srvVisible;

    // Matches StandardOpaque.hlsl's cbuffer DrawConstants byte-for-byte (16-byte-aligned rows).
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct DrawConstants {
        public Matrix4x4 Mvp;
        public Matrix4x4 Model;
        public Vector3 LightDir; public float Exposure;
        public Vector3 LightColor; public float Metallic;
        public Vector3 Ambient; public float Roughness;
        public Vector3 CameraPos; public float SpecularReflectance;
        public Vector4 BaseColorFactor;
        public Vector3 EmissiveFactor; public float HasEmissive;
        public float NormalStrength, NormalFlipY, HasMetallicMap, HasRoughnessMap;
        public float PackedOrm, Cutout, UseIBL, PrefilterMaxMip;
    }

    // The 6 material maps in HLSL register(t0..t5) order.
    const int MaterialSrvCount = 6;

    // IBL: baker (env→irradiance/prefilter/BRDF) + a per-frame 3-SRV shader-visible table (t6..t8).
    Dx12IblBaker ibl;
    Dx12DescriptorHeap iblSrvVisible;   // 3 contiguous SRVs copied per frame
    bool iblActiveThisFrame;

    // Sun cascaded shadows.
    const int CascadeCount = 4;
    const int ShadowMapSize = 2048;
    Dx12ShadowMap shadowMap;
    ID3D12RootSignature shadowRootSig;     // ShadowConstants CBV (b0)
    ID3D12PipelineState shadowPso;
    ID3D12Resource shadowCb;               // per (cascade,submesh) LightMvp slots, upload heap
    unsafe byte* shadowCbMapped;
    int shadowCbSlotSize, shadowCbSlotCount;
    readonly Matrix4x4[] cascadeMatrices = new Matrix4x4[CascadeCount];
    readonly float[] cascadeDepthRanges = new float[CascadeCount];
    bool shadowsThisFrame;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct ShadowConstants { public Matrix4x4 LightMvp; }

    // Volumetric fog (full-screen post pass, blended over scene color).
    ID3D12RootSignature fogRootSig;     // FogConstants CBV (b0) + depth+shadow SRV table (t0,t1) + sampler
    ID3D12PipelineState fogPso;
    ID3D12Resource fogCb;
    unsafe byte* fogCbMapped;
    Dx12DescriptorHeap fogSrvVisible;   // depth + shadow array, copied per frame

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct FogConstants {
        public Matrix4x4 InvViewProj;
        public Matrix4x4 Cascade0, Cascade1, Cascade2, Cascade3;
        public Vector4 CascadeBias;
        public Vector3 CameraPos; public float CascadeCountF;
        public Vector3 SunDirection; public float Density;
        public Vector3 SunColor; public float HeightFalloff;
        public Vector3 SkyAmbient; public float BaseHeight;
        public Vector3 Tint; public float Anisotropy;
        public float Scattering, AmbientScatter, SunGlow, SunGlowSharpness;
        public float StepCount, MaxDistance, ShadowMapTexel, Exposure;
    }

    // Per-frame constants (b1) shared by every opaque draw: the cascade matrices + shadow params.
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    unsafe struct FrameConstants {
        public Matrix4x4 Cascade0, Cascade1, Cascade2, Cascade3;
        public Vector4 CascadeBias;        // per-cascade depth-compare bias
        public float CascadeCountF; public float ShadowsEnabled; public float ShadowMapTexel; public float CascadeBlend;
    }
    ID3D12Resource frameCb;
    unsafe byte* frameCbMapped;

    public DX12HDRenderer(Dx12Device device) {
        dev = device;
    }

    public override RenderHandle SceneColorHandle => RenderHandle.None;
    public override RenderHandle GameColorHandle => RenderHandle.None;

    public override void ResizeSceneTarget(int width, int height) => Resize(width, height);
    public override void ResizeGameTarget(int width, int height) => Resize(width, height);

    void Resize(int width, int height) {
        if (width <= 0 || height <= 0) return;
        if (target != null && width == targetW && height == targetH) return;
        targetW = width; targetH = height;
        target?.Dispose(); ldr?.Dispose(); gbuffer?.Dispose();
        // The HDR scene target no longer owns depth — the G-buffer owns the scene depth (deferred path).
        target = new Dx12OffscreenTarget(dev, width, height, withDepth: false,
            colorFormat: Dx12OffscreenTarget.HdrFormat, colorReadable: true);
        ldr = new Dx12OffscreenTarget(dev, width, height);   // LDR composite output
        gbuffer = new Dx12GBuffer(dev, width, height);
        if (bloomRootSig != null) AllocBloomTargets();       // half-res bloom ping-pong follows size
        if (ssaoRootSig != null) AllocSsaoTargets();
        if (ssrRootSig != null) AllocSsrTarget();
        if (taaRootSig != null) AllocTaaTargets();
    }

    public override unsafe void Initialize() {
        // Clustered-deferred: geometry → G-buffer (owns scene depth) → deferred lighting → HDR `target`
        // (color only) → sky/fog/post → composite into `ldr` (R8). `target` no longer owns depth.
        target = new Dx12OffscreenTarget(dev, targetW, targetH, withDepth: false,
            colorFormat: Dx12OffscreenTarget.HdrFormat, colorReadable: true);
        ldr = new Dx12OffscreenTarget(dev, targetW, targetH);
        gbuffer = new Dx12GBuffer(dev, targetW, targetH);
        BuildRootSignature();
        BuildPipeline();
        BuildGeometryPass();

        cbSlotSize = (System.Runtime.InteropServices.Marshal.SizeOf<DrawConstants>() + 255) & ~255;
        cbSlotCount = 8192;   // submesh draws per frame ceiling (SunTemple ~hundreds)
        cbRing = dev.Device.CreateCommittedResource(
            HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)(cbSlotSize * cbSlotCount)), ResourceStates.GenericRead);
        cbMapped = cbRing.Map<byte>(0);

        // 6 SRVs per draw (the material table) — size the ring for the worst-case draw count.
        srvVisible = new Dx12DescriptorHeap(dev,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
            cbSlotCount * MaterialSrvCount, shaderVisible: true);

        BuildSkybox();
        BuildProcSky();

        ibl = new Dx12IblBaker(dev);
        // 3 IBL SRVs (irradiance/prefilter/BRDF) copied contiguously per frame into a shader-visible heap.
        iblSrvVisible = new Dx12DescriptorHeap(dev,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 3, shaderVisible: true);

        BuildShadows();

        int frameCbSize = (System.Runtime.InteropServices.Marshal.SizeOf<FrameConstants>() + 255) & ~255;
        frameCb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)frameCbSize), ResourceStates.GenericRead);
        frameCbMapped = frameCb.Map<byte>(0);

        BuildDeferredLighting();
        BuildFog();
        BuildSsr();
        BuildTaa();
        BuildComposite();
    }

    unsafe void BuildTaa() {
        var cbv = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        var srvRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 3, baseShaderRegister: 0);
        var srvTable = new RootParameter1(new RootDescriptorTable1(srvRange), ShaderVisibility.Pixel);
        var samp = new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0) {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp, MaxAnisotropy = 1,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        taaRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, new[] { cbv, srvTable }, new[] { samp })));

        string hlsl = BallisticEngine.DX12.EmbeddedShaderSource.ReadHlsl("Taa.hlsl");
        byte[] vs = Dx12ShaderCompiler.Compile(DxcShaderStage.Vertex, hlsl, "VSMain", "Taa.hlsl");
        byte[] ps = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, hlsl, "PSMain", "Taa.hlsl");
        taaPso = dev.Device.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription {
            RootSignature = taaRootSig, VertexShader = vs, PixelShader = ps, InputLayout = null,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle, SampleMask = uint.MaxValue,
            RasterizerState = RasterizerDescription.CullNone, BlendState = BlendDescription.Opaque,
            DepthStencilState = DepthStencilDescription.None,
            RenderTargetFormats = new[] { Dx12OffscreenTarget.HdrFormat }, DepthStencilFormat = Format.Unknown,
            SampleDescription = new SampleDescription(1, 0),
        });

        int cbSize = (System.Runtime.InteropServices.Marshal.SizeOf<TaaConstants>() + 255) & ~255;
        taaCb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)cbSize), ResourceStates.GenericRead);
        taaCbMapped = taaCb.Map<byte>(0);
        taaSrvVisible = new Dx12DescriptorHeap(dev,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 3, shaderVisible: true);
        AllocTaaTargets();
    }

    void AllocTaaTargets() {
        taaHistoryA?.Dispose(); taaHistoryB?.Dispose(); taaResolved?.Dispose();
        taaHistoryA = new Dx12OffscreenTarget(dev, targetW, targetH, withDepth: false, colorFormat: Dx12OffscreenTarget.HdrFormat, colorReadable: true);
        taaHistoryB = new Dx12OffscreenTarget(dev, targetW, targetH, withDepth: false, colorFormat: Dx12OffscreenTarget.HdrFormat, colorReadable: true);
        taaResolved = new Dx12OffscreenTarget(dev, targetW, targetH, withDepth: false, colorFormat: Dx12OffscreenTarget.HdrFormat, colorReadable: true);
        taaHistoryValid = false;   // history is stale after a resize
    }

    // Standard 8-phase Halton(2,3) sub-pixel jitter in pixel units (-0.5..0.5). Reused by FSR later.
    static Vector2 JitterOffset(int frameIndex) {
        int i = (frameIndex % 8) + 1;
        return new Vector2(Halton(i, 2) - 0.5f, Halton(i, 3) - 0.5f);
    }
    static float Halton(int index, int b) {
        float r = 0f, f = 1f;
        while (index > 0) { f /= b; r += f * (index % b); index /= b; }
        return r;
    }

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

        int cbSize = (System.Runtime.InteropServices.Marshal.SizeOf<SsrConstants>() + 255) & ~255;
        ssrCb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)cbSize), ResourceStates.GenericRead);
        ssrCbMapped = ssrCb.Map<byte>(0);
        ssrSrvVisible = new Dx12DescriptorHeap(dev,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 10, shaderVisible: true);
        AllocSsrTarget();
    }

    void AllocSsrTarget() {
        ssrTarget?.Dispose(); ssrScene?.Dispose();
        int w = System.Math.Max(1, targetW / 2), h = System.Math.Max(1, targetH / 2);
        ssrTarget = new Dx12OffscreenTarget(dev, w, h, withDepth: false,
            colorFormat: Dx12OffscreenTarget.HdrFormat, colorReadable: true);
        // Full-res scratch for the combine output (combine reads `target`, can't read+write it).
        ssrScene = new Dx12OffscreenTarget(dev, targetW, targetH, withDepth: false,
            colorFormat: Dx12OffscreenTarget.HdrFormat, colorReadable: true);
    }

    // Geometry pass PSO: same vertex layout + per-draw CBV(b0) + 6 material SRVs(t0..t5) as the forward
    // opaque path, but the pixel shader (GBuffer.hlsl) writes the 4-MRT fat G-buffer instead of shading.
    void BuildGeometryPass() {
        // b0 = per-draw DrawConstants (root CBV); table0 = 6 material SRVs t0..t5; s0 wrap sampler.
        var cbv = new RootParameter1(RootParameterType.ConstantBufferView,
            new RootDescriptor1(0, 0), ShaderVisibility.All);
        var matRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, MaterialSrvCount, baseShaderRegister: 0);
        var matTable = new RootParameter1(new RootDescriptorTable1(matRange), ShaderVisibility.Pixel);
        var wrap = new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0) {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Wrap,
            AddressV = TextureAddressMode.Wrap, AddressW = TextureAddressMode.Wrap, MaxAnisotropy = 16,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        gbufferRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.AllowInputAssemblerInputLayout,
                new[] { cbv, matTable }, new[] { wrap })));

        string hlsl = BallisticEngine.DX12.EmbeddedShaderSource.ReadHlsl("GBuffer.hlsl");
        byte[] vs = Dx12ShaderCompiler.Compile(DxcShaderStage.Vertex, hlsl, "VSMain", "GBuffer.hlsl");
        byte[] ps = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, hlsl, "PSMain", "GBuffer.hlsl");
        var layout = new InputLayoutDescription(
            new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new InputElementDescription("NORMAL", 0, Format.R32G32B32_Float, 0, 1),
            new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 0, 2),
            new InputElementDescription("TANGENT", 0, Format.R32G32B32A32_Float, 0, 3));
        gbufferPso = dev.Device.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription {
            RootSignature = gbufferRootSig, VertexShader = vs, PixelShader = ps, InputLayout = layout,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle, SampleMask = uint.MaxValue,
            RasterizerState = RasterizerDescription.CullClockwise,   // back-face cull, CCW-from-front (forward parity)
            BlendState = BlendDescription.Opaque, DepthStencilState = DepthStencilDescription.Default,
            RenderTargetFormats = Dx12GBuffer.ColorFormats,
            DepthStencilFormat = Dx12GBuffer.DepthFormat, SampleDescription = new SampleDescription(1, 0),
        });
    }

    // Deferred lighting PSO: fullscreen triangle, LightConstants CBV(b0) + FrameConstants CBV(b1) +
    // 9-SRV table(t0..t8: G0..G3, depth, irradiance, prefilter, BRDF, shadow) + clamp sampler.
    unsafe void BuildDeferredLighting() {
        var lightCbv = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.Pixel);
        var frameCbv = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(1, 0), ShaderVisibility.Pixel);
        // 12 SRVs: G0..G3, depth, irradiance, prefilter, BRDF, shadow (t0..t8) + cluster lights/grid/index (t9..t11).
        var srvRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 12, baseShaderRegister: 0);
        var srvTable = new RootParameter1(new RootDescriptorTable1(srvRange), ShaderVisibility.Pixel);
        var samp = new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0) {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp, MaxAnisotropy = 1,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        deferredRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None,
                new[] { lightCbv, frameCbv, srvTable }, new[] { samp })));

        string hlsl = BallisticEngine.DX12.EmbeddedShaderSource.ReadHlsl("DeferredLighting.hlsl");
        byte[] vs = Dx12ShaderCompiler.Compile(DxcShaderStage.Vertex, hlsl, "VSMain", "DeferredLighting.hlsl");
        byte[] ps = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, hlsl, "PSMain", "DeferredLighting.hlsl");
        deferredPso = dev.Device.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription {
            RootSignature = deferredRootSig, VertexShader = vs, PixelShader = ps, InputLayout = null,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle, SampleMask = uint.MaxValue,
            RasterizerState = RasterizerDescription.CullNone, BlendState = BlendDescription.Opaque,
            DepthStencilState = DepthStencilDescription.None,
            RenderTargetFormats = new[] { Dx12OffscreenTarget.HdrFormat },
            DepthStencilFormat = Format.Unknown, SampleDescription = new SampleDescription(1, 0),
        });

        int cbSize = (System.Runtime.InteropServices.Marshal.SizeOf<LightConstants>() + 255) & ~255;
        deferredCb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)cbSize), ResourceStates.GenericRead);
        deferredCbMapped = deferredCb.Map<byte>(0);
        deferredSrvVisible = new Dx12DescriptorHeap(dev,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 12, shaderVisible: true);

        clusteredLights = new Dx12ClusteredLights(dev);
    }

    unsafe void BuildComposite() {
        // CompositeConstants CBV (b0) + 4-SRV table (HDR t0, bloom t1, avg-lum t2, AO t3) + clamp sampler s0.
        var cbv = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        var srvRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 4, baseShaderRegister: 0);
        var srvTable = new RootParameter1(new RootDescriptorTable1(srvRange), ShaderVisibility.Pixel);
        var samp = new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0) {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp, MaxAnisotropy = 1,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        compositeRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, new[] { cbv, srvTable }, new[] { samp })));

        string hlsl = BallisticEngine.DX12.EmbeddedShaderSource.ReadHlsl("Composite.hlsl");
        byte[] vs = Dx12ShaderCompiler.Compile(DxcShaderStage.Vertex, hlsl, "VSMain", "Composite.hlsl");
        byte[] ps = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, hlsl, "PSMain", "Composite.hlsl");
        compositePso = dev.Device.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription {
            RootSignature = compositeRootSig, VertexShader = vs, PixelShader = ps, InputLayout = null,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle, SampleMask = uint.MaxValue,
            RasterizerState = RasterizerDescription.CullNone, BlendState = BlendDescription.Opaque,
            DepthStencilState = DepthStencilDescription.None,
            RenderTargetFormats = new[] { Dx12OffscreenTarget.ColorFormat },   // LDR output
            DepthStencilFormat = Format.Unknown, SampleDescription = new SampleDescription(1, 0),
        });

        int cbSize = (System.Runtime.InteropServices.Marshal.SizeOf<CompositeConstants>() + 255) & ~255;
        compositeCb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)cbSize), ResourceStates.GenericRead);
        compositeCbMapped = compositeCb.Map<byte>(0);
        compositeSrvVisible = new Dx12DescriptorHeap(dev,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 4, shaderVisible: true);

        BuildLumAverage();
        BuildSsao();
    }

    unsafe void BuildSsao() {
        var cbv = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        // 2-SRV table: main pass = depth(t0) + G-buffer world normal(t1); blur passes = AO(t0).
        var srvRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 2, baseShaderRegister: 0);
        var srvTable = new RootParameter1(new RootDescriptorTable1(srvRange), ShaderVisibility.Pixel);
        var samp = new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0) {
            Filter = Filter.MinMagMipPoint, AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp, MaxAnisotropy = 1,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        ssaoRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, new[] { cbv, srvTable }, new[] { samp })));

        string hlsl = BallisticEngine.DX12.EmbeddedShaderSource.ReadHlsl("Ssao.hlsl");
        byte[] vs = Dx12ShaderCompiler.Compile(DxcShaderStage.Vertex, hlsl, "VSMain", "Ssao.hlsl");
        ID3D12PipelineState MakePso(string entry) => dev.Device.CreateGraphicsPipelineState(
            new GraphicsPipelineStateDescription {
                RootSignature = ssaoRootSig, VertexShader = vs,
                PixelShader = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, hlsl, entry, "Ssao.hlsl"),
                InputLayout = null, PrimitiveTopologyType = PrimitiveTopologyType.Triangle, SampleMask = uint.MaxValue,
                RasterizerState = RasterizerDescription.CullNone, BlendState = BlendDescription.Opaque,
                DepthStencilState = DepthStencilDescription.None,
                RenderTargetFormats = new[] { Format.R8_UNorm }, DepthStencilFormat = Format.Unknown,
                SampleDescription = new SampleDescription(1, 0),
            });
        ssaoPso = MakePso("PSMain");
        ssaoBlurHPso = MakePso("PSBlurH");
        ssaoBlurVPso = MakePso("PSBlurV");

        int cbSize = (System.Runtime.InteropServices.Marshal.SizeOf<SsaoConstants>() + 255) & ~255;
        ssaoCb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)cbSize), ResourceStates.GenericRead);
        ssaoCbMapped = ssaoCb.Map<byte>(0);
        // Main pass binds a 2-SRV run (depth+normal); each blur binds a 2-SRV run (AO at t0, t1 unused).
        // 3 runs × 2 = 6 contiguous slots.
        ssaoSrvVisible = new Dx12DescriptorHeap(dev,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 6, shaderVisible: true);
        AllocSsaoTargets();
    }

    void AllocSsaoTargets() {
        ssaoA?.Dispose(); ssaoB?.Dispose();
        int w = System.Math.Max(1, targetW / 2), h = System.Math.Max(1, targetH / 2);
        ssaoA = new Dx12OffscreenTarget(dev, w, h, withDepth: false, colorFormat: Format.R8_UNorm, colorReadable: true);
        ssaoB = new Dx12OffscreenTarget(dev, w, h, withDepth: false, colorFormat: Format.R8_UNorm, colorReadable: true);
    }

    unsafe void BuildLumAverage() {
        // 1 HDR SRV (t0) + clamp sampler; outputs the 1×1 average-luminance target (auto-exposure metering).
        var srvRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 1, baseShaderRegister: 0);
        var srvTable = new RootParameter1(new RootDescriptorTable1(srvRange), ShaderVisibility.Pixel);
        var samp = new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0) {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp, MaxAnisotropy = 1,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        lumRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, new[] { srvTable }, new[] { samp })));

        string hlsl = BallisticEngine.DX12.EmbeddedShaderSource.ReadHlsl("LumAverage.hlsl");
        byte[] vs = Dx12ShaderCompiler.Compile(DxcShaderStage.Vertex, hlsl, "VSMain", "LumAverage.hlsl");
        byte[] ps = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, hlsl, "PSMain", "LumAverage.hlsl");
        lumPso = dev.Device.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription {
            RootSignature = lumRootSig, VertexShader = vs, PixelShader = ps, InputLayout = null,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle, SampleMask = uint.MaxValue,
            RasterizerState = RasterizerDescription.CullNone, BlendState = BlendDescription.Opaque,
            DepthStencilState = DepthStencilDescription.None,
            RenderTargetFormats = new[] { Format.R16_Float }, DepthStencilFormat = Format.Unknown,
            SampleDescription = new SampleDescription(1, 0),
        });

        lumTarget = new Dx12OffscreenTarget(dev, 1, 1, withDepth: false,
            colorFormat: Format.R16_Float, colorReadable: true);
        lumSrvVisible = new Dx12DescriptorHeap(dev,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 1, shaderVisible: true);

        BuildBloom();
    }

    unsafe void BuildBloom() {
        var cbv = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        var srvRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 1, baseShaderRegister: 0);
        var srvTable = new RootParameter1(new RootDescriptorTable1(srvRange), ShaderVisibility.Pixel);
        var samp = new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0) {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp, MaxAnisotropy = 1,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        bloomRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, new[] { cbv, srvTable }, new[] { samp })));

        string hlsl = BallisticEngine.DX12.EmbeddedShaderSource.ReadHlsl("Bloom.hlsl");
        byte[] vs = Dx12ShaderCompiler.Compile(DxcShaderStage.Vertex, hlsl, "VSMain", "Bloom.hlsl");
        ID3D12PipelineState MakePso(string entry) => dev.Device.CreateGraphicsPipelineState(
            new GraphicsPipelineStateDescription {
                RootSignature = bloomRootSig, VertexShader = vs,
                PixelShader = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, hlsl, entry, "Bloom.hlsl"),
                InputLayout = null, PrimitiveTopologyType = PrimitiveTopologyType.Triangle, SampleMask = uint.MaxValue,
                RasterizerState = RasterizerDescription.CullNone, BlendState = BlendDescription.Opaque,
                DepthStencilState = DepthStencilDescription.None,
                RenderTargetFormats = new[] { Dx12OffscreenTarget.HdrFormat }, DepthStencilFormat = Format.Unknown,
                SampleDescription = new SampleDescription(1, 0),
            });
        bloomBrightPso = MakePso("PSBrightPass");
        bloomBlurHPso = MakePso("PSBlurH");
        bloomBlurVPso = MakePso("PSBlurV");

        int cbSize = (System.Runtime.InteropServices.Marshal.SizeOf<BloomConstants>() + 255) & ~255;
        bloomCb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)cbSize), ResourceStates.GenericRead);
        bloomCbMapped = bloomCb.Map<byte>(0);
        bloomSrvVisible = new Dx12DescriptorHeap(dev,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 3, shaderVisible: true);
        AllocBloomTargets();
    }

    void AllocBloomTargets() {
        bloomA?.Dispose(); bloomB?.Dispose();
        int w = System.Math.Max(1, targetW / 2), h = System.Math.Max(1, targetH / 2);
        bloomA = new Dx12OffscreenTarget(dev, w, h, withDepth: false, colorFormat: Dx12OffscreenTarget.HdrFormat, colorReadable: true);
        bloomB = new Dx12OffscreenTarget(dev, w, h, withDepth: false, colorFormat: Dx12OffscreenTarget.HdrFormat, colorReadable: true);
    }

    unsafe void BuildFog() {
        // FogConstants CBV (b0) + a 2-SRV table (depth t0, shadow array t1) + clamp sampler s0.
        var cbv = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        var srvRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 2, baseShaderRegister: 0);
        var srvTable = new RootParameter1(new RootDescriptorTable1(srvRange), ShaderVisibility.Pixel);
        var samp = new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0) {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp, MaxAnisotropy = 1,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        fogRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, new[] { cbv, srvTable }, new[] { samp })));

        string hlsl = BallisticEngine.DX12.EmbeddedShaderSource.ReadHlsl("VolumetricFog.hlsl");
        byte[] vs = Dx12ShaderCompiler.Compile(DxcShaderStage.Vertex, hlsl, "VSMain", "VolumetricFog.hlsl");
        byte[] ps = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, hlsl, "PSMain", "VolumetricFog.hlsl");

        // Blend: dest = dest * srcAlpha(transmittance) + src(scatter). Classic over-fog composite.
        var blend = BlendDescription.Opaque;
        var rt0 = blend.RenderTarget[0];
        rt0.BlendEnable = true;
        rt0.SourceBlend = Blend.One;
        rt0.DestinationBlend = Blend.SourceAlpha;
        rt0.BlendOperation = BlendOperation.Add;
        rt0.SourceBlendAlpha = Blend.Zero;
        rt0.DestinationBlendAlpha = Blend.Zero;
        rt0.BlendOperationAlpha = BlendOperation.Add;
        blend.RenderTarget[0] = rt0;

        fogPso = dev.Device.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription {
            RootSignature = fogRootSig, VertexShader = vs, PixelShader = ps, InputLayout = null,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle, SampleMask = uint.MaxValue,
            RasterizerState = RasterizerDescription.CullNone, BlendState = blend,
            DepthStencilState = DepthStencilDescription.None,
            RenderTargetFormats = new[] { Dx12OffscreenTarget.HdrFormat },
            DepthStencilFormat = Format.Unknown, SampleDescription = new SampleDescription(1, 0),
        });

        int cbSize = (System.Runtime.InteropServices.Marshal.SizeOf<FogConstants>() + 255) & ~255;
        fogCb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)cbSize), ResourceStates.GenericRead);
        fogCbMapped = fogCb.Map<byte>(0);
        fogSrvVisible = new Dx12DescriptorHeap(dev,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 2, shaderVisible: true);
    }

    unsafe void BuildShadows() {
        shadowMap = new Dx12ShadowMap(dev, ShadowMapSize, CascadeCount);

        // Depth-only PSO: ShadowConstants CBV (b0), POSITION-only input, depth bias to cut acne.
        shadowRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.AllowInputAssemblerInputLayout, new[] {
                new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.Vertex) })));

        string hlsl = BallisticEngine.DX12.EmbeddedShaderSource.ReadHlsl("ShadowDepth.hlsl");
        byte[] vs = Dx12ShaderCompiler.Compile(DxcShaderStage.Vertex, hlsl, "VSMain", "ShadowDepth.hlsl");
        var layout = new InputLayoutDescription(
            new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0));
        var raster = RasterizerDescription.CullClockwise;   // cull back faces (same winding as opaque)
        raster.DepthBias = 2000;            // constant slope-scaled bias to fight shadow acne
        raster.SlopeScaledDepthBias = 2.5f;
        raster.DepthBiasClamp = 0f;
        var psoDesc = new GraphicsPipelineStateDescription {
            RootSignature = shadowRootSig, VertexShader = vs, PixelShader = default,   // depth-only, no PS
            InputLayout = layout, PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            SampleMask = uint.MaxValue, RasterizerState = raster, BlendState = BlendDescription.Opaque,
            DepthStencilState = DepthStencilDescription.Default,
            RenderTargetFormats = System.Array.Empty<Format>(),     // no color targets
            DepthStencilFormat = Dx12ShadowMap.DepthFormat,
            SampleDescription = new SampleDescription(1, 0),
        };
        shadowPso = dev.Device.CreateGraphicsPipelineState(psoDesc);

        shadowCbSlotSize = (System.Runtime.InteropServices.Marshal.SizeOf<ShadowConstants>() + 255) & ~255;
        // CascadeCount × submesh draws per frame.
        shadowCbSlotCount = CascadeCount * 4096;
        shadowCb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)((long)shadowCbSlotSize * shadowCbSlotCount)), ResourceStates.GenericRead);
        shadowCbMapped = shadowCb.Map<byte>(0);
    }

    unsafe void BuildProcSky() {
        // CBV-only root sig (the atmosphere is pure ALU — no textures).
        var cbv = new RootParameter1(RootParameterType.ConstantBufferView,
            new RootDescriptor1(0, 0), ShaderVisibility.All);
        procSkyRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, new[] { cbv })));

        string hlsl = BallisticEngine.DX12.EmbeddedShaderSource.ReadHlsl("ProceduralSky.hlsl");
        byte[] vs = Dx12ShaderCompiler.Compile(DxcShaderStage.Vertex, hlsl, "VSMain", "ProceduralSky.hlsl");
        byte[] ps = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, hlsl, "PSMain", "ProceduralSky.hlsl");
        var ds = DepthStencilDescription.Default;
        ds.DepthWriteMask = DepthWriteMask.Zero;
        ds.DepthFunc = ComparisonFunction.LessEqual;
        var psoDesc = new GraphicsPipelineStateDescription {
            RootSignature = procSkyRootSig, VertexShader = vs, PixelShader = ps, InputLayout = null,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle, SampleMask = uint.MaxValue,
            RasterizerState = RasterizerDescription.CullNone, BlendState = BlendDescription.Opaque,
            DepthStencilState = ds,
            RenderTargetFormats = new[] { Dx12OffscreenTarget.HdrFormat },
            DepthStencilFormat = Dx12OffscreenTarget.DepthFormat,
            SampleDescription = new SampleDescription(1, 0),
        };
        procSkyPso = dev.Device.CreateGraphicsPipelineState(psoDesc);

        int cbSize = (System.Runtime.InteropServices.Marshal.SizeOf<ProcSkyConstants>() + 255) & ~255;
        procSkyCb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)cbSize), ResourceStates.GenericRead);
        procSkyCbMapped = procSkyCb.Map<byte>(0);
    }

    unsafe void BuildSkybox() {
        // Root sig: CBV b0 + 1 cube SRV table (t0) + static clamp sampler.
        var cbv = new RootParameter1(RootParameterType.ConstantBufferView,
            new RootDescriptor1(0, 0), ShaderVisibility.All);
        var srvRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 1, baseShaderRegister: 0);
        var srvTable = new RootParameter1(new RootDescriptorTable1(srvRange), ShaderVisibility.Pixel);
        var sampler = new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0) {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp, MaxAnisotropy = 1,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        skyRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, new[] { cbv, srvTable }, new[] { sampler })));

        string hlsl = BallisticEngine.DX12.EmbeddedShaderSource.ReadHlsl("Skybox.hlsl");
        byte[] vs = Dx12ShaderCompiler.Compile(DxcShaderStage.Vertex, hlsl, "VSMain", "Skybox.hlsl");
        byte[] ps = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, hlsl, "PSMain", "Skybox.hlsl");
        // Depth: test LEqual, NO write — fills only far-plane (uncovered) pixels behind opaque geometry.
        var ds = DepthStencilDescription.Default;
        ds.DepthWriteMask = DepthWriteMask.Zero;
        ds.DepthFunc = ComparisonFunction.LessEqual;
        var psoDesc = new GraphicsPipelineStateDescription {
            RootSignature = skyRootSig, VertexShader = vs, PixelShader = ps,
            InputLayout = null,   // SV_VertexID cube, no vertex buffer
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle, SampleMask = uint.MaxValue,
            RasterizerState = RasterizerDescription.CullNone,
            BlendState = BlendDescription.Opaque, DepthStencilState = ds,
            RenderTargetFormats = new[] { Dx12OffscreenTarget.HdrFormat },
            DepthStencilFormat = Dx12OffscreenTarget.DepthFormat,
            SampleDescription = new SampleDescription(1, 0),
        };
        skyPso = dev.Device.CreateGraphicsPipelineState(psoDesc);

        int cbSize = (System.Runtime.InteropServices.Marshal.SizeOf<SkyboxConstants>() + 255) & ~255;
        skyCb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)cbSize), ResourceStates.GenericRead);
        skyCbMapped = skyCb.Map<byte>(0);
        skySrvVisible = new Dx12DescriptorHeap(dev,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 1, shaderVisible: true);
    }

    void BuildRootSignature() {
        // b0 = per-draw constants (root CBV);
        // table0 (param 1) = 6 material SRVs t0..t5 (per draw);
        // table1 (param 2) = 4 SRVs t6..t9: irradiance cube / prefilter cube / BRDF LUT / shadow array (frame);
        // b1 (param 3) = per-frame FrameConstants (cascade matrices + shadow params);
        // static samplers: s0 wrap (material), s1 clamp (IBL/sky).
        var cbv = new RootParameter1(RootParameterType.ConstantBufferView,
            new RootDescriptor1(0, 0), ShaderVisibility.All);
        var matRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, MaterialSrvCount, baseShaderRegister: 0);
        var matTable = new RootParameter1(new RootDescriptorTable1(matRange), ShaderVisibility.Pixel);
        var iblRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 4, baseShaderRegister: 6);
        var iblTable = new RootParameter1(new RootDescriptorTable1(iblRange), ShaderVisibility.Pixel);
        var frameCbv = new RootParameter1(RootParameterType.ConstantBufferView,
            new RootDescriptor1(1, 0), ShaderVisibility.Pixel);

        var wrap = new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0) {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Wrap,
            AddressV = TextureAddressMode.Wrap, AddressW = TextureAddressMode.Wrap, MaxAnisotropy = 16,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        var clamp = new StaticSamplerDescription(ShaderVisibility.Pixel, 1, 0) {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp, MaxAnisotropy = 1,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };

        var desc = new RootSignatureDescription1(
            RootSignatureFlags.AllowInputAssemblerInputLayout,
            new[] { cbv, matTable, iblTable, frameCbv }, new[] { wrap, clamp });
        rootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(desc));
    }

    void BuildPipeline() {
        // Fully-qualified: the GL backend also has a BallisticEngine.EmbeddedShaderSource (ReadGlsl), and
        // this file is in namespace BallisticEngine, so the unqualified name would resolve to the GL one.
        string hlsl = BallisticEngine.DX12.EmbeddedShaderSource.ReadHlsl("StandardOpaque.hlsl");
        byte[] vs = Dx12ShaderCompiler.Compile(DxcShaderStage.Vertex, hlsl, "VSMain", "StandardOpaque.hlsl");
        byte[] ps = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, hlsl, "PSMain", "StandardOpaque.hlsl");

        // Separate input slots: the engine keeps pos/normal/uv/tangent in separate GPU buffers — one
        // InputElement per slot, each at offset 0 in its own slot. (Interleaving is a later optimization.)
        var layout = new InputLayoutDescription(
            new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new InputElementDescription("NORMAL", 0, Format.R32G32B32_Float, 0, 1),
            new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 0, 2),
            new InputElementDescription("TANGENT", 0, Format.R32G32B32A32_Float, 0, 3));

        var psoDesc = new GraphicsPipelineStateDescription {
            RootSignature = rootSig,
            VertexShader = vs,
            PixelShader = ps,
            InputLayout = layout,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            SampleMask = uint.MaxValue,
            // RH mesh wound CCW-from-front; DX default front face is clockwise, so CullClockwise culls
            // back faces for CCW geometry (matches the cube test).
            RasterizerState = RasterizerDescription.CullClockwise,
            BlendState = BlendDescription.Opaque,
            DepthStencilState = DepthStencilDescription.Default,
            RenderTargetFormats = new[] { Dx12OffscreenTarget.HdrFormat },
            DepthStencilFormat = Dx12OffscreenTarget.DepthFormat,
            SampleDescription = new SampleDescription(1, 0),
        };
        pso = dev.Device.CreateGraphicsPipelineState(psoDesc);
    }

    public override unsafe RenderMetrics BeginRender(RendererArgs args) {
        IViewProjectionProvider vp = args.viewProjectionProvider;
        if (vp is null || target is null)
            return default;

        // Camera. The provider's view (LookAt) is convention-agnostic — convert 1:1. Rebuild the
        // projection DX-style (RH, z in [0,1]) since the provider's is OpenTK GL-convention (z in [-1,1]).
        Matrix4x4 view = ToNumerics(vp.GetViewMatrix());
        Matrix4x4 proj = Matrix4x4.CreatePerspectiveFieldOfView(
            45f * (MathF.PI / 180f), (float)targetW / targetH, CameraNear, CameraFar);
        // UNJITTERED view*proj — used for TAA reprojection (must be stable) + the froxel/SSR/post math.
        Matrix4x4 viewProjUnjittered = view * proj;

        // TAA jitter: offset the projection by a sub-pixel Halton amount so the whole frame (geometry +
        // SSR + shadows) is consistently jittered; the TAA pass resolves it against the unjittered history.
        // Plumbed ONCE here — FSR reuses currentJitter. Off when the volume disables TAA.
        bool taaOn = PostFX.TaaEnabled;
        currentJitter = taaOn ? JitterOffset(taaFrame) : Vector2.Zero;
        if (taaOn) {
            // NDC offset = 2 * pixelJitter / screen. DX clip y is up, so subtract for the +y pixel dir.
            proj.M31 += 2f * currentJitter.X / targetW;
            proj.M32 -= 2f * currentJitter.Y / targetH;
        }
        Matrix4x4 viewProj = view * proj;   // JITTERED — geometry/SSR/etc. render with this

        Vector3 camPos = ToNumerics(vp.Transform.WorldPosition);
        LightUniforms light = LightUniforms.Resolve();
        Vector3 lightDir = ToNumerics(light.Direction);
        Vector3 lightColor = ToNumerics(light.Color);
        Vector3 ambient = ToNumerics(vp.AmbientColor) * MathF.Max(0.05f, light.AmbientIntensity);
        // The sun radiance is HDR (lux-scaled, ~80000); a fixed pre-exposure brings it into a viewable
        // range before the ACES tonemap (the GL path auto-meters EV100; this is a constant stand-in for
        // first light). Tunable via BALLISTIC_DX12_EXPOSURE while dialing against the frozen baseline.
        // 1e-5 lands the PBR path (energy-conserving ÷π diffuse) near the GL baseline brightness; the DX12
        // image is intentionally a touch dimmer (no IBL ambient / shadows yet — those are next milestones).
        float exposure = float.TryParse(Environment.GetEnvironmentVariable("BALLISTIC_DX12_EXPOSURE"),
            System.Globalization.CultureInfo.InvariantCulture, out float e) ? e : 1.0e-5f;

        // Shadows first: render the sun cascades' depth (own upload command list) before opaque.
        RenderShadows(view, proj, light);

        // IBL: bake the env→irradiance/prefilter/BRDF from the procedural sky (re-bakes only on param
        // change). Own upload command list, before the render list. Only when a ProceduralSky is active.
        iblActiveThisFrame = false;
        if (ProceduralSky.Active is { } pSky) {
            Vector3 sunDir = lightDir.LengthSquared() < 1e-8f ? Vector3.UnitY : Vector3.Normalize(lightDir);
            float sunAngR = (DirectionalLight.Instance?.AngularDiameter ?? 0.53f) * 0.5f * (MathF.PI / 180f);
            ibl.EnsureBaked(pSky, sunDir, lightColor, sunAngR);
            iblActiveThisFrame = ibl.HasBaked;
        }

        // Per-frame constants (b1): cascade matrices + shadow params.
        var fc = new FrameConstants {
            Cascade0 = Matrix4x4.Transpose(cascadeMatrices[0]),
            Cascade1 = Matrix4x4.Transpose(cascadeMatrices[1]),
            Cascade2 = Matrix4x4.Transpose(cascadeMatrices[2]),
            Cascade3 = Matrix4x4.Transpose(cascadeMatrices[3]),
            CascadeBias = new Vector4(0.0015f, 0.0020f, 0.0030f, 0.0050f),
            CascadeCountF = CascadeCount, ShadowsEnabled = shadowsThisFrame ? 1f : 0f,
            ShadowMapTexel = 1f / ShadowMapSize, CascadeBlend = 0.1f,
        };
        *(FrameConstants*)frameCbMapped = fc;

        int draws = 0;
        long tris = 0;
        srvVisible.Reset();
        int slot = 0;

        var fallbackDiffuse = DefaultTextures.Neutral(TextureType.Diffuse) as Dx12Texture2D;

        // === GEOMETRY PASS: fill the fat G-buffer (no lighting — GBuffer.hlsl writes albedo/normal/ORM/
        // emissive + depth). Same vertex transform + material sampling as the old forward opaque. ===
        gbuffer.RenderGeometry(cl => {
            cl.SetGraphicsRootSignature(gbufferRootSig);
            cl.SetPipelineState(gbufferPso);
            cl.SetDescriptorHeaps(srvVisible.Heap);
            cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);

            foreach (IStaticMeshRenderer r in RuntimeSet<IStaticMeshRenderer>.ReadOnlyCollection) {
                if (r is null || !r.IsActive || !r.IsRenderable) continue;
                Mesh mesh = r.SharedMesh;
                if (mesh is null) continue;

                var vb = mesh.VertexBuffer as Dx12Buffer<GLVector3>;
                var ib = mesh.IndexBuffer as Dx12IndexBuffer;
                var nb = mesh.NormalBuffer as Dx12Buffer<GLVector3>;
                var ub = mesh.UvBuffer as Dx12Buffer<OpenTK.Mathematics.Vector2>;
                var tb = mesh.TangentBuffer as Dx12Buffer<OpenTK.Mathematics.Vector4>;
                if (vb?.Resource is null || ib?.Resource is null ||
                    nb?.Resource is null || ub?.Resource is null || tb?.Resource is null) continue;

                Matrix4x4 model = ToNumerics(r.Transform.WorldMatrix);
                Matrix4x4 mvp = model * viewProj;

                Span<VertexBufferView> vbViews = stackalloc VertexBufferView[4];
                vbViews[0] = new VertexBufferView(vb.GpuAddress, (uint)vb.ByteSize, (uint)vb.Stride);
                vbViews[1] = new VertexBufferView(nb.GpuAddress, (uint)nb.ByteSize, (uint)nb.Stride);
                vbViews[2] = new VertexBufferView(ub.GpuAddress, (uint)ub.ByteSize, (uint)ub.Stride);
                vbViews[3] = new VertexBufferView(tb.GpuAddress, (uint)tb.ByteSize, (uint)tb.Stride);
                cl.IASetVertexBuffers(0, vbViews);
                cl.IASetIndexBuffer(new IndexBufferView(ib.GpuAddress, (uint)ib.ByteSize, Format.R32_UInt));

                int only = r.SubMeshIndex;
                int first = only >= 0 ? only : 0;
                int last = only >= 0 ? only : mesh.SubMeshes.Length - 1;

                for (int s = first; s <= last; s++) {
                    if ((uint)s >= (uint)mesh.SubMeshes.Length) break;
                    SubMeshData sub = mesh.SubMeshes[s];
                    if (sub.IndexCount <= 0) continue;
                    Material mat = r.MaterialFor(s);
                    if (mat is null) continue;
                    if (slot >= cbSlotCount) break;

                    bool hasMetal = mat.Metallic is not null;
                    bool hasRough = mat.Roughness is not null;
                    bool emissive = mat.IsEmissive;
                    // The G-buffer geometry shader reads the material-shaping fields (factors, maps, flags);
                    // the per-light fields (LightDir/LightColor/Ambient/Exposure) are unused here (they live
                    // in the deferred pass now) but the struct is shared, so they're filled harmlessly.
                    var c = new DrawConstants {
                        Mvp = Matrix4x4.Transpose(mvp),
                        Model = Matrix4x4.Transpose(model),
                        LightDir = lightDir, Exposure = exposure,
                        LightColor = lightColor, Metallic = mat.MetallicFactor,
                        Ambient = ambient, Roughness = mat.RoughnessFactor,
                        CameraPos = camPos, SpecularReflectance = mat.SpecularReflectance,
                        BaseColorFactor = ToNumerics(mat.BaseColorFactor),
                        EmissiveFactor = ToNumerics(mat.EmissiveColor) * mat.EmissiveIntensity,
                        HasEmissive = emissive ? 1f : 0f,
                        NormalStrength = mat.NormalStrength, NormalFlipY = mat.NormalFlipY ? 1f : 0f,
                        HasMetallicMap = hasMetal ? 1f : 0f, HasRoughnessMap = hasRough ? 1f : 0f,
                        PackedOrm = mat.PackedOrm ? 1f : 0f, Cutout = mat.Cutout ? 1f : 0f,
                        UseIBL = iblActiveThisFrame ? 1f : 0f,
                        PrefilterMaxMip = ibl != null ? ibl.PrefilterMipCount - 1 : 0f,
                    };
                    *(DrawConstants*)(cbMapped + (long)slot * cbSlotSize) = c;
                    cl.SetGraphicsRootConstantBufferView(0,
                        cbRing.GPUVirtualAddress + (ulong)((long)slot * cbSlotSize));

                    // 6 material SRVs (t0..t5); null slots resolve to neutral defaults.
                    int tableStart = srvVisible.AllocateRange(MaterialSrvCount);
                    BindSrv(tableStart + 0, mat.Diffuse, TextureType.Diffuse, fallbackDiffuse);
                    BindSrv(tableStart + 1, mat.Normal, TextureType.Normal, null);
                    BindSrv(tableStart + 2, mat.Metallic, TextureType.Metallic, null);
                    BindSrv(tableStart + 3, mat.Roughness, TextureType.Roughness, null);
                    BindSrv(tableStart + 4, mat.AO, TextureType.AO, null);
                    BindSrv(tableStart + 5, mat.Emissive, TextureType.Emissive, null);
                    cl.SetGraphicsRootDescriptorTable(1, srvVisible.Gpu(tableStart));

                    cl.DrawIndexedInstanced((uint)sub.IndexCount, 1, (uint)sub.IndexStart, 0, 0);
                    draws++;
                    tris += sub.IndexCount / 3;
                    slot++;
                }
            }
        });

        // === CLUSTERED PUNCTUAL LIGHTS: gather active point/spot lights + CPU froxel-cull (before the
        // deferred pass reads the result). Lights are raw HDR (NOT pre-exposed — composite meters them,
        // same as the sun). ===
        GatherPunctualLights(view, proj);

        // === DEFERRED LIGHTING: read the G-buffer + depth → PBR sun + IBL + shadows + punctual → HDR. ===
        gbuffer.ToShaderResource();
        DrawDeferredLighting(view, viewProj, camPos, lightDir, lightColor, ambient);

        // === SKY: draw into the HDR color at the far plane, depth-testing the G-buffer depth (LEqual,
        // no write). ProceduralSky takes precedence over an asset cubemap Skybox (matches GL). ===
        gbuffer.DepthToReadOnly();
        target.RenderColorWithExternalDepth(gbuffer.DsvHandle, cl => {
            if (ProceduralSky.Active is not null)
                DrawProcSky(cl, view, proj, light);
            else
                DrawSkybox(cl, view, proj);
        });

        // --- Volumetric fog (post pass, reads depth+shadows, blends over HDR scene color) ---
        // BALLISTIC_FX_VOLUMETRIC=1 forces it on (same harness contract as the GL backend).
        bool fogOn = PostFX.VolumetricEnabled
            || Environment.GetEnvironmentVariable("BALLISTIC_FX_VOLUMETRIC") == "1";
        if (fogOn)
            DrawFog(view, viewProj, camPos, light);

        // --- SSR (volume-driven screen-space reflections, reads the G-buffer; lerps into the scene color) ---
        if (PostFX.SsrEnabled && PostFX.SsrIntensity > 0f)
            DrawSsr(view, proj);

        // --- TAA (volume-driven; the AA — resolves the jittered frame vs reprojected history) ---
        if (taaOn)
            DrawTaa(viewProjUnjittered);
        else
            taaHistoryValid = false;   // keep history fresh for when TAA turns back on

        // --- SSAO (HBAO from depth → half-res AO, multiplied in the composite) ---
        bool ssaoOn = Environment.GetEnvironmentVariable("BALLISTIC_DX12_SSAO") != "0";
        if (ssaoOn) DrawSsao(view, proj);

        // --- Final composite: HDR scene → exposure → ACES → sRGB → LDR ---
        DrawComposite(ssaoOn);

        RenderStats.Scene.DrawCalls = draws;
        RenderStats.Scene.Triangles = tris;
        return new RenderMetrics(draws, 0, (int)tris, 0, 0f);
    }

    // Gather the scene's active point/spot lights into the clustered light buffer + CPU froxel-cull. Reads
    // typed properties only (no reflection). Radiance is RAW HDR PhysicalColor (NOT pre-exposed — the DX12
    // composite auto-meters it, exactly like the sun), unlike the GL path which pre-exposes at upload.
    void GatherPunctualLights(Matrix4x4 view, Matrix4x4 proj) {
        clusteredLights.BeginGather();
        foreach (PointLight p in RuntimeSet<PointLight>.ReadOnlyCollection) {
            if (p is null || !p.IsActive) continue;
            clusteredLights.AddPoint(ToNumerics(p.transform.WorldPosition), p.Range,
                ToNumerics(p.PhysicalColor), p.SourceRadius);
        }
        foreach (SpotLight s in RuntimeSet<SpotLight>.ReadOnlyCollection) {
            if (s is null || !s.IsActive) continue;
            Vector3 dir = ToNumerics(s.transform.WorldRotation * GLVector3.UnitZ);
            float inner = Math.Clamp(s.InnerAngle, 0f, 89f) * (MathF.PI / 180f);
            float outer = Math.Clamp(MathF.Max(s.OuterAngle, s.InnerAngle), 0f, 89.9f) * (MathF.PI / 180f);
            clusteredLights.AddSpot(ToNumerics(s.transform.WorldPosition), dir, s.Range,
                ToNumerics(s.PhysicalColor), MathF.Cos(inner), MathF.Cos(outer), s.SourceRadius);
        }
        clusteredLights.Cull(view, proj, targetW, targetH, CameraNear, CameraFar);
    }

    // Fullscreen deferred lighting: read the G-buffer (G0..G3 + depth, already in SRV state) + IBL +
    // shadow cascades, shade Cook-Torrance sun + split-sum IBL + clustered punctual lights, write RAW HDR
    // into `target`. Mirrors the forward StandardOpaque shading — only the inputs come from the G-buffer.
    unsafe void DrawDeferredLighting(Matrix4x4 view, Matrix4x4 viewProj, Vector3 camPos, Vector3 lightDir, Vector3 lightColor, Vector3 ambient) {
        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        Matrix4x4.Invert(viewProj, out Matrix4x4 invVP);
        *(LightConstants*)deferredCbMapped = new LightConstants {
            InvViewProj = Matrix4x4.Transpose(invVP),
            View = Matrix4x4.Transpose(view),
            LightDir = lightDir, LightColor = lightColor, Ambient = ambient, CameraPos = camPos,
            UseIBL = iblActiveThisFrame ? 1f : 0f,
            PrefilterMaxMip = ibl != null ? ibl.PrefilterMipCount - 1 : 0f,
            PunctualCount = clusteredLights.LightCount,
            ScreenSize = new Vector2(targetW, targetH),
            ClusterNearFar = new Vector2(CameraNear, CameraFar),
        };

        // Copy the 12 SRVs: G0..G3, depth, irradiance, prefilter, BRDF, shadow (t0..t8) + cluster
        // lights/grid/index (t9..t11).
        deferredSrvVisible.Reset();
        int b = deferredSrvVisible.AllocateRange(12);
        for (int i = 0; i < Dx12GBuffer.RtCount; i++)
            dev.Device.CopyDescriptorsSimple(1, deferredSrvVisible.Cpu(b + i), gbuffer.ColorSrvCpu(i), heapType);
        dev.Device.CopyDescriptorsSimple(1, deferredSrvVisible.Cpu(b + 4), gbuffer.DepthSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, deferredSrvVisible.Cpu(b + 5), ibl.IrradianceSrv, heapType);
        dev.Device.CopyDescriptorsSimple(1, deferredSrvVisible.Cpu(b + 6), ibl.PrefilterSrv, heapType);
        dev.Device.CopyDescriptorsSimple(1, deferredSrvVisible.Cpu(b + 7), ibl.BrdfSrv, heapType);
        dev.Device.CopyDescriptorsSimple(1, deferredSrvVisible.Cpu(b + 8), shadowMap.SrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, deferredSrvVisible.Cpu(b + 9), clusteredLights.LightSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, deferredSrvVisible.Cpu(b + 10), clusteredLights.GridSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, deferredSrvVisible.Cpu(b + 11), clusteredLights.IndexSrvCpu, heapType);

        target.RenderColorOnlyCleared(cl => {
            cl.SetGraphicsRootSignature(deferredRootSig);
            cl.SetPipelineState(deferredPso);
            cl.SetDescriptorHeaps(deferredSrvVisible.Heap);
            cl.SetGraphicsRootConstantBufferView(0, deferredCb.GPUVirtualAddress);
            cl.SetGraphicsRootConstantBufferView(1, frameCb.GPUVirtualAddress);
            cl.SetGraphicsRootDescriptorTable(2, deferredSrvVisible.Gpu(b));
            cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            cl.DrawInstanced(3, 1, 0, 0);
        });
    }

    // TAA (volume-driven): resolve the jittered HDR scene against the reprojected history. Reads the
    // current HDR color (target) + history + G-buffer depth, writes the resolved color into the new history
    // buffer, then copies it back to `target` so the composite tonemaps the AA'd result. Reprojection uses
    // the UNJITTERED matrices. History ping-pongs; invalidated on resize / first frame.
    unsafe void DrawTaa(Matrix4x4 viewProjUnjittered) {
        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        Matrix4x4.Invert(viewProjUnjittered, out Matrix4x4 invVP);
        Dx12OffscreenTarget history = taaWriteB ? taaHistoryA : taaHistoryB;   // read from the OTHER buffer
        Dx12OffscreenTarget writeHist = taaWriteB ? taaHistoryB : taaHistoryA;

        *(TaaConstants*)taaCbMapped = new TaaConstants {
            CurrInvViewProj = Matrix4x4.Transpose(invVP),
            PrevViewProj = Matrix4x4.Transpose(taaPrevViewProj),
            Feedback = PostFX.TaaFeedback, ValidHistory = taaHistoryValid ? 1f : 0f,
            TexelSize = new Vector2(1f / targetW, 1f / targetH),
        };

        target.ColorToShaderResource();
        history.ColorToShaderResource();
        gbuffer.DepthToShaderResource();
        taaSrvVisible.Reset();
        int b = taaSrvVisible.AllocateRange(3);
        dev.Device.CopyDescriptorsSimple(1, taaSrvVisible.Cpu(b + 0), target.ColorSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, taaSrvVisible.Cpu(b + 1), history.ColorSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, taaSrvVisible.Cpu(b + 2), gbuffer.DepthSrvCpu, heapType);
        writeHist.RenderColorOnly(cl => {
            cl.SetGraphicsRootSignature(taaRootSig); cl.SetPipelineState(taaPso);
            cl.SetDescriptorHeaps(taaSrvVisible.Heap);
            cl.SetGraphicsRootConstantBufferView(0, taaCb.GPUVirtualAddress);
            cl.SetGraphicsRootDescriptorTable(1, taaSrvVisible.Gpu(b));
            cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            cl.DrawInstanced(3, 1, 0, 0);
        });
        writeHist.ColorToShaderResource();
        target.CopyColorFrom(writeHist);   // the resolved AA'd color becomes the scene color

        taaWriteB = !taaWriteB;
        taaHistoryValid = true;
        taaPrevViewProj = viewProjUnjittered;
        taaFrame++;
    }

    // Screen-space reflections (volume-driven): half-res view-space march reads the lit HDR color +
    // G-buffer (depth/normal/material) → ssrTarget; combine depth-aware-upsamples + lerps into the scene
    // color (via the ssrScene scratch, copied back to `target`). Runs after sky/fog so the color is complete.
    unsafe void DrawSsr(Matrix4x4 view, Matrix4x4 proj) {
        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        Matrix4x4.Invert(proj, out Matrix4x4 invProj);
        *(SsrConstants*)ssrCbMapped = new SsrConstants {
            Projection = Matrix4x4.Transpose(proj), InvProjection = Matrix4x4.Transpose(invProj),
            ViewMatrix = Matrix4x4.Transpose(view),
            Intensity = PostFX.SsrIntensity,
            TexelSize = new Vector2(1f / ssrTarget.Width, 1f / ssrTarget.Height),
        };

        // Both passes need the HDR color + G-buffer as SRVs. The G-buffer is already SRV; bring color to SRV.
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

    // HBAO from the G-buffer (scene depth for view-pos + world normal, both already SRVs from the deferred
    // pass) → blurred half-res AO in ssaoA. No depth-reconstructed normal anymore — the real surface normal
    // comes straight from the G-buffer (sharper, silhouette-correct). View transforms the world normal into
    // view space for the horizon march.
    unsafe void DrawSsao(Matrix4x4 view, Matrix4x4 proj) {
        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        Matrix4x4.Invert(proj, out Matrix4x4 invProj);
        gbuffer.DepthToShaderResource();   // no-op if fog already moved it
        *(SsaoConstants*)ssaoCbMapped = new SsaoConstants {
            Projection = Matrix4x4.Transpose(proj), InvProjection = Matrix4x4.Transpose(invProj),
            View = Matrix4x4.Transpose(view),
            Radius = 0.5f, Intensity = 1.0f, TexelSize = new Vector2(1f / ssaoA.Width, 1f / ssaoA.Height),
        };
        // Main AO pass: depth(t0) + G-buffer world normal(t1) → ssaoA. Uses slots 0,1.
        dev.Device.CopyDescriptorsSimple(1, ssaoSrvVisible.Cpu(0), gbuffer.DepthSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, ssaoSrvVisible.Cpu(1), gbuffer.ColorSrvCpu(1), heapType);
        ssaoA.RenderColorOnly(cl => {
            cl.SetGraphicsRootSignature(ssaoRootSig); cl.SetPipelineState(ssaoPso);
            cl.SetDescriptorHeaps(ssaoSrvVisible.Heap);
            cl.SetGraphicsRootConstantBufferView(0, ssaoCb.GPUVirtualAddress);
            cl.SetGraphicsRootDescriptorTable(1, ssaoSrvVisible.Gpu(0));
            cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            cl.DrawInstanced(3, 1, 0, 0);
        });
        // Blur H (ssaoA→ssaoB), Blur V (ssaoB→ssaoA). Each binds a 2-slot run (AO at t0; t1 unused but
        // copied so the descriptor is valid). Runs at slots 2 and 4.
        void Blur(ID3D12PipelineState pso, Dx12OffscreenTarget src, Dx12OffscreenTarget dst, int srvSlot) {
            src.ColorToShaderResource();
            dev.Device.CopyDescriptorsSimple(1, ssaoSrvVisible.Cpu(srvSlot), src.ColorSrvCpu, heapType);
            dev.Device.CopyDescriptorsSimple(1, ssaoSrvVisible.Cpu(srvSlot + 1), src.ColorSrvCpu, heapType);
            dst.RenderColorOnly(cl => {
                cl.SetGraphicsRootSignature(ssaoRootSig); cl.SetPipelineState(pso);
                cl.SetDescriptorHeaps(ssaoSrvVisible.Heap);
                cl.SetGraphicsRootConstantBufferView(0, ssaoCb.GPUVirtualAddress);
                cl.SetGraphicsRootDescriptorTable(1, ssaoSrvVisible.Gpu(srvSlot));
                cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                cl.DrawInstanced(3, 1, 0, 0);
            });
        }
        Blur(ssaoBlurHPso, ssaoA, ssaoB, 2);
        Blur(ssaoBlurVPso, ssaoB, ssaoA, 4);
        ssaoA.ColorToShaderResource();
    }

    // Bloom: bright-pass the HDR (target, already in SRV state) → bloomA; blur H (bloomA→bloomB);
    // blur V (bloomB→bloomA). Result lands in bloomA at half-res for the composite to add.
    unsafe void DrawBloom() {
        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        float texW = 1f / bloomA.Width, texH = 1f / bloomA.Height;

        void Pass(ID3D12PipelineState pso, Dx12OffscreenTarget src, Dx12OffscreenTarget dst, int srvSlot,
            Vector2 texel, float threshold) {
            *(BloomConstants*)bloomCbMapped = new BloomConstants { Threshold = threshold, TexelSize = texel };
            src.ColorToShaderResource();
            dev.Device.CopyDescriptorsSimple(1, bloomSrvVisible.Cpu(srvSlot), src.ColorSrvCpu, heapType);
            dst.RenderColorOnly(cl => {
                cl.SetGraphicsRootSignature(bloomRootSig);
                cl.SetPipelineState(pso);
                cl.SetDescriptorHeaps(bloomSrvVisible.Heap);
                cl.SetGraphicsRootConstantBufferView(0, bloomCb.GPUVirtualAddress);
                cl.SetGraphicsRootDescriptorTable(1, bloomSrvVisible.Gpu(srvSlot));
                cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                cl.DrawInstanced(3, 1, 0, 0);
            });
        }

        // Bright-pass reads the full-res HDR scene (already in SRV state from DrawComposite).
        Pass(bloomBrightPso, target, bloomA, 0, new Vector2(texW, texH), 1.0f);
        Pass(bloomBlurHPso, bloomA, bloomB, 1, new Vector2(texW, texH), 0f);
        Pass(bloomBlurVPso, bloomB, bloomA, 2, new Vector2(texW, texH), 0f);
        bloomA.ColorToShaderResource();   // ready for the composite to sample
    }

    // Tonemap the HDR scene target into the LDR output. Auto-exposure drives the exposure; bloom (if on)
    // is added in. The bloom pass runs first (inside this), reading the HDR scene.
    unsafe void DrawComposite(bool ssaoOn) {
        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;

        // Manual exposure override (BALLISTIC_DX12_EXPOSURE) disables auto-exposure; else auto-meter.
        bool manual = float.TryParse(Environment.GetEnvironmentVariable("BALLISTIC_DX12_EXPOSURE"),
            System.Globalization.CultureInfo.InvariantCulture, out float manualExp);

        target.ColorToShaderResource();   // HDR scene color → SRV (for both the lum pass and composite)

        if (!manual) {
            // Auto-exposure metering: reduce the HDR scene to a 1×1 geometric-mean luminance.
            dev.Device.CopyDescriptorsSimple(1, lumSrvVisible.Cpu(0), target.ColorSrvCpu, heapType);
            lumTarget.RenderColorOnly(cl => {
                cl.SetGraphicsRootSignature(lumRootSig);
                cl.SetPipelineState(lumPso);
                cl.SetDescriptorHeaps(lumSrvVisible.Heap);
                cl.SetGraphicsRootDescriptorTable(0, lumSrvVisible.Gpu(0));
                cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                cl.DrawInstanced(3, 1, 0, 0);
            });
            lumTarget.ColorToShaderResource();
        }

        // Bloom: bright-pass + blur the HDR into bloomA (half-res). On by default; BALLISTIC_DX12_BLOOM=0 off.
        bool bloomOn = Environment.GetEnvironmentVariable("BALLISTIC_DX12_BLOOM") != "0";
        if (bloomOn) DrawBloom();

        // ExposureKey: middle-grey target ~0.18 (the HDR scene is physical radiance; auto-meter rescales).
        *(CompositeConstants*)compositeCbMapped = new CompositeConstants {
            Exposure = manual ? manualExp : 1.0e-5f,
            BloomIntensity = bloomOn ? 0.6f : 0f,
            AutoExposure = manual ? 0f : 1f,
            ExposureKey = 0.18f,
            UseAo = ssaoOn ? 1f : 0f,
        };

        dev.Device.CopyDescriptorsSimple(1, compositeSrvVisible.Cpu(0), target.ColorSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, compositeSrvVisible.Cpu(1),
            bloomOn ? bloomA.ColorSrvCpu : target.ColorSrvCpu, heapType);   // bloom slot
        dev.Device.CopyDescriptorsSimple(1, compositeSrvVisible.Cpu(2),
            manual ? target.ColorSrvCpu : lumTarget.ColorSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, compositeSrvVisible.Cpu(3),
            ssaoOn ? ssaoA.ColorSrvCpu : target.ColorSrvCpu, heapType);     // AO slot

        ldr.RenderColorOnly(cl => {
            cl.SetGraphicsRootSignature(compositeRootSig);
            cl.SetPipelineState(compositePso);
            cl.SetDescriptorHeaps(compositeSrvVisible.Heap);
            cl.SetGraphicsRootConstantBufferView(0, compositeCb.GPUVirtualAddress);
            cl.SetGraphicsRootDescriptorTable(1, compositeSrvVisible.Gpu(0));
            cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            cl.DrawInstanced(3, 1, 0, 0);
        });
        if (!manual) lumTarget.ColorToRenderTarget();
        target.ColorToRenderTarget();   // restore for next frame's scene render
    }

    // Full-screen volumetric fog: march the air toward the camera (shadowed sun + sky in-scatter),
    // blend (scatter, transmittance) over the scene color. Reads scene depth + shadow cascades as SRVs.
    unsafe void DrawFog(Matrix4x4 view, Matrix4x4 viewProj, Vector3 camPos, LightUniforms light) {
        Matrix4x4.Invert(viewProj, out Matrix4x4 invVP);
        // Crude sky-ambient for fog in-scatter (engine-radiance scale; the fog Exposure constant matches
        // the opaque pre-exposure). A proper average-irradiance readback is a follow-up.
        Vector3 skyAmbient = new Vector3(2000f, 2200f, 2600f);
        var pf = PostFX;
        var fc = new FogConstants {
            InvViewProj = Matrix4x4.Transpose(invVP),
            Cascade0 = Matrix4x4.Transpose(cascadeMatrices[0]), Cascade1 = Matrix4x4.Transpose(cascadeMatrices[1]),
            Cascade2 = Matrix4x4.Transpose(cascadeMatrices[2]), Cascade3 = Matrix4x4.Transpose(cascadeMatrices[3]),
            CascadeBias = new Vector4(0.0015f, 0.0020f, 0.0030f, 0.0050f),
            CameraPos = camPos, CascadeCountF = CascadeCount,
            SunDirection = ToNumerics(light.Direction), Density = pf.VolumetricDensity,
            SunColor = ToNumerics(light.Color), HeightFalloff = pf.VolumetricHeightFalloff,
            SkyAmbient = skyAmbient, BaseHeight = pf.VolumetricBaseHeight,
            Tint = ToNumerics(pf.VolumetricTint), Anisotropy = pf.VolumetricAnisotropy,
            Scattering = pf.VolumetricScattering * pf.VolumetricIntensity,
            AmbientScatter = pf.VolumetricAmbientScatter * pf.VolumetricIntensity,
            SunGlow = pf.VolumetricSunGlow, SunGlowSharpness = pf.VolumetricSunGlowSharpness,
            StepCount = pf.VolumetricStepCount, MaxDistance = pf.VolumetricMaxDistance,
            ShadowMapTexel = 1f / ShadowMapSize, Exposure = 1.0e-5f,   // match the opaque pre-exposure
        };
        *(FogConstants*)fogCbMapped = fc;

        // depth → SRV (G-buffer owns it), shadow array already SRV from RenderShadows. Copy both into the
        // fog heap. After the sky pass the G-buffer depth is in DepthRead; bring it to PixelShaderResource.
        gbuffer.DepthToShaderResource();
        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        dev.Device.CopyDescriptorsSimple(1, fogSrvVisible.Cpu(0), gbuffer.DepthSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, fogSrvVisible.Cpu(1), shadowMap.SrvCpu, heapType);

        target.RenderColorOnly(cl => {
            cl.SetGraphicsRootSignature(fogRootSig);
            cl.SetPipelineState(fogPso);
            cl.SetDescriptorHeaps(fogSrvVisible.Heap);
            cl.SetGraphicsRootConstantBufferView(0, fogCb.GPUVirtualAddress);
            cl.SetGraphicsRootDescriptorTable(1, fogSrvVisible.Gpu(0));
            cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            cl.DrawInstanced(3, 1, 0, 0);
        });
    }

    // Draw the environment cubemap as the far-plane background (LEqual, no depth write) where opaque
    // geometry didn't cover. No-op if the scene has no Skybox or its cubemap isn't a DX12 cube yet.
    unsafe void DrawSkybox(ID3D12GraphicsCommandList4 cl, Matrix4x4 view, Matrix4x4 proj) {
        if (Skybox.Active?.Cubemap is not Dx12Texture3D cube || cube.Resource is null)
            return;

        // View with translation stripped (the sky cube is centred on the camera).
        Matrix4x4 viewNoT = view; viewNoT.M41 = 0; viewNoT.M42 = 0; viewNoT.M43 = 0;
        OpenTK.Mathematics.Vector3 euler = Skybox.Active.RotationEuler;
        Matrix4x4 rot = Matrix4x4.CreateRotationX(euler.X * (MathF.PI / 180f))
                      * Matrix4x4.CreateRotationY(euler.Y * (MathF.PI / 180f))
                      * Matrix4x4.CreateRotationZ(euler.Z * (MathF.PI / 180f));
        // The skybox texels are HDR scaled by sky.Exposure; fold in the same pre-exposure the opaque pass
        // uses so the sky brightness tracks the scene. (Skybox.Exposure defaults ~5000 for .hdr cubes.)
        float skyExposure = Skybox.Active.Exposure * 1.0e-5f;

        var sc = new SkyboxConstants {
            ViewProjNoTranslate = Matrix4x4.Transpose(viewNoT * proj),
            SkyRotation = Matrix4x4.Transpose(rot),
            Exposure = skyExposure,
        };
        *(SkyboxConstants*)skyCbMapped = sc;

        dev.Device.CopyDescriptorsSimple(1, skySrvVisible.Cpu(0), cube.SrvCpu,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);

        cl.SetGraphicsRootSignature(skyRootSig);
        cl.SetPipelineState(skyPso);
        cl.SetDescriptorHeaps(skySrvVisible.Heap);
        cl.SetGraphicsRootConstantBufferView(0, skyCb.GPUVirtualAddress);
        cl.SetGraphicsRootDescriptorTable(1, skySrvVisible.Gpu(0));
        cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        cl.DrawInstanced(36, 1, 0, 0);
    }

    // Render the sun cascades' depth (one depth-array layer per cascade) before the opaque pass. Uses the
    // dedicated upload command list (separate from the render list), then leaves the array as an SRV the
    // opaque shader samples. Re-renders every frame (cascade caching is a later optimization).
    unsafe void RenderShadows(Matrix4x4 camView, Matrix4x4 camProj, LightUniforms light) {
        shadowsThisFrame = false;
        if (DirectionalLight.Instance is null) return;   // no sun → no shadows

        Vector3 sunTravel = -ToNumerics(light.Direction);   // light.Direction is TOWARD the light
        if (sunTravel.LengthSquared() < 1e-8f) return;
        float shadowDistance = DirectionalLight.Instance.ShadowDistance;
        Dx12ShadowMath.ComputeCascades(camView, camProj, sunTravel, shadowDistance, ShadowMapSize,
            cascadeMatrices, cascadeDepthRanges);

        // Fill per (cascade, submesh) LightMvp constants, mirroring the opaque iteration.
        int slot = 0;
        var fills = new System.Collections.Generic.List<(int cascade, Dx12Buffer<GLVector3> vb, Dx12IndexBuffer ib, int start, int count, int cbSlot)>();
        for (int c = 0; c < CascadeCount; c++) {
            foreach (IStaticMeshRenderer r in RuntimeSet<IStaticMeshRenderer>.ReadOnlyCollection) {
                if (r is null || !r.IsActive || !r.IsRenderable) continue;
                Mesh mesh = r.SharedMesh; if (mesh is null) continue;
                if (mesh.VertexBuffer is not Dx12Buffer<GLVector3> vb || vb.Resource is null) continue;
                if (mesh.IndexBuffer is not Dx12IndexBuffer ib || ib.Resource is null) continue;
                Matrix4x4 model = ToNumerics(r.Transform.WorldMatrix);
                Matrix4x4 lightMvp = model * cascadeMatrices[c];
                int only = r.SubMeshIndex;
                int first = only >= 0 ? only : 0;
                int last = only >= 0 ? only : mesh.SubMeshes.Length - 1;
                for (int s = first; s <= last; s++) {
                    if ((uint)s >= (uint)mesh.SubMeshes.Length) break;
                    SubMeshData sub = mesh.SubMeshes[s];
                    if (sub.IndexCount <= 0) continue;
                    if (slot >= shadowCbSlotCount) break;
                    *(ShadowConstants*)(shadowCbMapped + (long)slot * shadowCbSlotSize) =
                        new ShadowConstants { LightMvp = Matrix4x4.Transpose(lightMvp) };
                    fills.Add((c, vb, ib, sub.IndexStart, sub.IndexCount, slot));
                    slot++;
                }
            }
        }
        if (fills.Count == 0) return;

        dev.ExecuteUpload(cl => {
            shadowMap.ToDepthWrite(cl);
            cl.SetGraphicsRootSignature(shadowRootSig);
            cl.SetPipelineState(shadowPso);
            cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            int cur = -1;
            for (int c = 0; c < CascadeCount; c++) {
                shadowMap.RenderCascade(cl, c, cc => {
                    foreach (var f in fills) {
                        if (f.cascade != c) continue;
                        cc.SetGraphicsRootConstantBufferView(0,
                            shadowCb.GPUVirtualAddress + (ulong)((long)f.cbSlot * shadowCbSlotSize));
                        cc.IASetVertexBuffers(0, new VertexBufferView(f.vb.GpuAddress, (uint)f.vb.ByteSize, (uint)f.vb.Stride));
                        cc.IASetIndexBuffer(new IndexBufferView(f.ib.GpuAddress, (uint)f.ib.ByteSize, Format.R32_UInt));
                        cc.DrawIndexedInstanced((uint)f.count, 1, (uint)f.start, 0, 0);
                    }
                });
            }
            shadowMap.ToShaderResource(cl);
        });
        shadowsThisFrame = true;
    }

    // Draw the procedural atmosphere as the far-plane background (pure-ALU march by view direction).
    unsafe void DrawProcSky(ID3D12GraphicsCommandList4 cl, Matrix4x4 view, Matrix4x4 proj, LightUniforms light) {
        ProceduralSky sky = ProceduralSky.Active;
        if (sky is null) return;

        Matrix4x4 viewNoT = view; viewNoT.M41 = 0; viewNoT.M42 = 0; viewNoT.M43 = 0;
        // Sun: DirectionalLight drives it (LightUniforms.Direction is TOWARD the light = toward the sun).
        Vector3 sunDir = ToNumerics(light.Direction);
        if (sunDir.LengthSquared() < 1e-8f) sunDir = Vector3.UnitY;
        sunDir = Vector3.Normalize(sunDir);
        float sunAngularRadius = (DirectionalLight.Instance?.AngularDiameter ?? 0.53f) * 0.5f * (MathF.PI / 180f);

        var sc = new ProcSkyConstants {
            ViewProjNoTranslate = Matrix4x4.Transpose(viewNoT * proj),
            SunDirection = sunDir, SunAngularRadius = MathF.Max(sunAngularRadius, 1e-4f),
            SunRadiance = ToNumerics(light.Color), SunDiskIntensity = MathF.Max(sky.SunDiskIntensity, 0f),
            GroundAlbedo = ToNumerics(sky.GroundColor), AirDensity = MathF.Max(sky.AirDensity, 0f),
            Haze = MathF.Max(sky.Haze, 0f), HazeAnisotropy = Math.Clamp(sky.HazeAnisotropy, 0f, 0.99f),
            OzoneDensity = MathF.Max(sky.OzoneDensity, 0f), MultiScatter = MathF.Max(sky.MultipleScattering, 1f),
            Exposure = MathF.Max(sky.Exposure, 0f),
        };
        *(ProcSkyConstants*)procSkyCbMapped = sc;

        cl.SetGraphicsRootSignature(procSkyRootSig);
        cl.SetPipelineState(procSkyPso);
        cl.SetGraphicsRootConstantBufferView(0, procSkyCb.GPUVirtualAddress);
        cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        cl.DrawInstanced(36, 1, 0, 0);
    }

    // Copy one material texture's persistent SRV into the shader-visible table at `visibleSlot`. A null
    // texture resolves to that slot's neutral default (DefaultTextures.Neutral) so the descriptor is
    // always valid — matching the GL Material.Activate fallback (metallic 0, roughness 1, AO 1, flat +Z
    // normal, dark emissive). `explicitFallback` lets diffuse use a white fallback.
    void BindSrv(int visibleSlot, Texture2D tex, TextureType type, Dx12Texture2D explicitFallback) {
        var dx = (tex as Dx12Texture2D)
                 ?? explicitFallback
                 ?? (DefaultTextures.Neutral(type) as Dx12Texture2D);
        dev.Device.CopyDescriptorsSimple(1, srvVisible.Cpu(visibleSlot), dx.SrvCpu,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
    }

    public override void PostRenderCleanUp() {
        foreach (IStaticMeshRenderer r in RuntimeSet<IStaticMeshRenderer>.ReadOnlyCollection)
            if (r != null) r.RenderedThisFrame = false;
    }

    // Readback comes from the LDR composite (R8) — the HDR scene target isn't a valid BMP source.
    public void SaveFrame(string path) => ldr?.SaveBmp(path);
    public int Width => targetW;
    public int Height => targetH;

    // Internal pipeline steps — no engine/editor caller (BeginRender draws opaques itself).
    public override void RenderOpaque(IReadOnlyCollection<IStaticMeshRenderer> renderTargets,
        RendererArgs args, bool isShadowPass) { }
    public override void RenderSkybox(IReadOnlyCollection<ISkyboxDrawable> renderTargets, RendererArgs args) { }
    public override void RenderInstancing(BatchGroup<IStaticMeshRenderer> batchGroup, RendererArgs args) { }
    public override void RenderInstancing(Mesh mesh, Material material, GLMatrix4[] transforms, RendererArgs args) { }

    static Matrix4x4 ToNumerics(GLMatrix4 m) => new(
        m.M11, m.M12, m.M13, m.M14,
        m.M21, m.M22, m.M23, m.M24,
        m.M31, m.M32, m.M33, m.M34,
        m.M41, m.M42, m.M43, m.M44);
    static Vector3 ToNumerics(GLVector3 v) => new(v.X, v.Y, v.Z);
    static Vector4 ToNumerics(OpenTK.Mathematics.Vector4 v) => new(v.X, v.Y, v.Z, v.W);
}
