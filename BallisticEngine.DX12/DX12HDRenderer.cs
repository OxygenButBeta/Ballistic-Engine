using System.Numerics;
using BallisticEngine.DX12;
using BallisticEngine.Rendering;   // BatchGroup<T>
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.Dxc;
using Vortice.DXGI;
using GLMatrix4 = System.Numerics.Matrix4x4;   // engine math is System.Numerics now; ToNumerics(...) is an identity copy
using GLVector3 = System.Numerics.Vector3;

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
    // targetW/targetH = the INTERNAL (render) resolution: the scene + all post passes render here. When FSR
    // is off this equals the output resolution. When FSR is on it's the (smaller) FSR render resolution and
    // the upscaler reconstructs outputW/outputH. ldr is always at output resolution.
    int targetW = 1920, targetH = 1080;
    int outputW = 1920, outputH = 1080;

    // FSR temporal upscaling: render at targetW/H (internal) -> fsrOutput (output res). Replaces TAA when
    // active. fsrUnavailable latches if the native DLLs fail to load (clean checkout) so we stop retrying.
    Dx12FsrUpscaler fsr;
    Dx12OffscreenTarget fsrOutput;    // HDR (R16F), output res, UAV-writable — FSR's reconstructed color
    bool fsrActive;
    bool fsrUnavailable;
    UpscaleMode currentUpscaleMode = UpscaleMode.Off;
    const float FovYRadians = 45f * (MathF.PI / 180f);   // matches the projection's vertical FOV

    ID3D12RootSignature rootSig;
    ID3D12PipelineState pso;

    // --- Clustered-deferred path ---
    // Geometry pass: writes the fat G-buffer (4 MRT) with the same vertex transform + material sampling as
    // the old forward opaque, but NO lighting (GBuffer.hlsl). Reuses the per-draw DrawConstants CBV (b0) +
    // 6 material SRVs (t0..t5) — same root sig shape as the forward path minus the IBL/shadow/frame params.
    Dx12GBuffer gbuffer;
    ID3D12RootSignature gbufferRootSig;
    ID3D12PipelineState gbufferPso;

    // Motion vectors: a per-pass CBV (b1) shared by BOTH geometry passes (CPU GBuffer.hlsl + GPU-driven
    // GBufferBindless.hlsl) holding the UNJITTERED current + previous frame view*proj. The geometry PS
    // reprojects each surface's world position through both to write a jitter-free screen-space motion
    // vector (prevUV - currUV) into the G-buffer's RG16F motion target — consumed by TAA and the FSR
    // upscaler. Camera reprojection (correct for static geometry, which is all of the heavy test content);
    // per-object motion for animated/physics renderers is a follow-up (would bake a prev model per draw).
    ID3D12Resource motionCb;
    unsafe byte* motionCbMapped;
    Matrix4x4 motionPrevViewProj;   // previous frame's UNJITTERED view*proj
    bool motionPrevValid;
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct MotionConstants { public Matrix4x4 ViewProjCur; public Matrix4x4 ViewProjPrev; }

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
        public float UseRtShadows; public float Pad3;
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
    Vector2 currentJitter;              // this frame's sub-pixel jitter (pixels) — exposed for FSR reuse
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct TaaConstants {
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

    // SSGI: SSILVB horizon-bitmask one-bounce gather (half-res) → composite into the lit HDR scene. Ported
    // from the GL SSGI_Frag/Combine. Temporal accumulation (via the motion buffer) + OIDN denoise wrap it
    // (step C). Driven by the ScreenSpaceGlobalIllumination VOLUME (PostFX.Ssgi*).
    ID3D12RootSignature ssgiRootSig;        // SsgiConstants CBV(b0) + 3-SRV table + clamp sampler
    ID3D12PipelineState ssgiGatherPso, ssgiTemporalPso, ssgiCombinePso;
    ID3D12Resource ssgiCb;
    unsafe byte* ssgiCbMapped;
    Dx12OffscreenTarget ssgiTarget;         // half-res RGBA16F raw GI (rgb + edge-fade)
    Dx12OffscreenTarget ssgiHistoryA, ssgiHistoryB;  // half-res ping-pong accumulated GI (rgb + history len)
    Dx12OffscreenTarget ssgiDenoised;       // half-res OIDN output (the GL a-trous replacement)
    Dx12OffscreenTarget ssgiScene;          // full-res scratch: combine writes here, copied back to `target`
    Dx12DescriptorHeap ssgiSrvVisible;      // 3 SRVs per pass
    int ssgiFrame;                          // slice-set rotation counter
    bool ssgiHistWriteB;                    // temporal ping-pong toggle
    bool ssgiHistValid;                     // false on first frame / resize
    // OIDN spatial denoise (replaces the GL a-trous). Two paths: a ZERO-COPY GPU path (OIDN's HIP device
    // imports a D3D12 shared buffer; per frame = 2 GPU texture<->buffer copies + an in-place GPU denoise, no
    // CPU readback) when SharedCapable, else the CPU readback round-trip (D3D12 -> host floats -> OIDN ->
    // upload). Created lazily on first use; the shared path falls back to readback if the HIP import fails.
    Dx12OidnDenoiser ssgiOidn;
    bool ssgiOidnTried;
    float[] ssgiCpuColor, ssgiCpuOut;       // host float3 buffers sized to the half-res GI (readback path)
    Dx12OidnGpuPath ssgiOidnGpu;            // zero-copy GPU pack/unpack denoise (shared float buffer)
    bool ssgiSharedFailed;                   // shared path failed once → stick to readback forever
    bool ssgiOidnForceReadback;              // BALLISTIC_DX12_OIDN_READBACK=1 → force the CPU path (A/B door)
    bool ssgiOidnTiming;                     // BALLISTIC_DX12_OIDN_TIMING=1 → log avg denoise ms (perf A/B)
    bool ssgiOidnEnvRead;
    readonly System.Diagnostics.Stopwatch ssgiOidnSw = new();
    double ssgiOidnAccumMs; int ssgiOidnAccumFrames;
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct SsgiConstants {
        public Matrix4x4 Projection; public Matrix4x4 InvProjection; public Matrix4x4 ViewMatrix;
        public Vector4 Params0;   // RayLength, Falloff, Thickness, MultiBounce
        public Vector4 Params1;   // BounceBoost, RayCount, FrameIndex, _
        public Vector4 Params2;   // TexelSize.xy, preExposure, 1/preExposure
        public Vector4 Combine0;  // Intensity, Look, Saturation, OcclusionPower
        public Vector4 Tint;      // Tint.xyz, _
        public Vector4 Params3;   // HasHistory, MaxHistory, _, _
    }

    // --- DXR ray-traced sun shadows (volume-driven: Shadows.rayTracedShadows / PostFX.RayTracedShadows) ---
    // A scene BLAS/TLAS (Dx12SceneAS) + an RT pass (DxrShadows.hlsl) that traces one shadow ray per pixel
    // toward the sun → a full-res R8 mask the deferred lighting multiplies into the sun term (UseRtShadows).
    // Built lazily on first use; falls back to the cascaded CSM when DXR is unavailable. Hard shadows are
    // deterministic (no denoise); soft penumbra + OIDN is a follow-up.
    Dx12SceneAS sceneAS;
    ID3D12Device5 device5;
    bool dxrChecked, dxrAvailable;
    bool noRtWarned;                            // P7.0: log the no-RT downgrade once, not every frame
    // P7.0: one-time log when RayTraced GI/reflections/shadows are auto-downgraded for lack of HW ray tracing.
    void WarnNoRtOnce() {
        if (noRtWarned) return;
        noRtWarned = true;
        Console.WriteLine("[DX12] No hardware ray tracing — RayTraced GI/reflections/shadows downgraded to screen-space fallbacks (SSGI/SSR/cascades).");
    }
    ID3D12RootSignature rtShadowRootSig;        // CBV(b0) + table{SRV t0 TLAS, t1 depth, t2 normal; UAV u0 mask}
    ID3D12StateObject rtShadowPso;
    ID3D12Resource rtShadowSbt, rtShadowCb;
    unsafe byte* rtShadowCbMapped;
    Dx12OffscreenTarget rtShadowMask;           // full-res R8 (1 lit / 0 shadowed), UAV + SRV
    Dx12DescriptorHeap rtShadowHeap;            // 4 descriptors (rebuilt per frame)
    bool rtShadowBuilt;
    bool rtShadowsThisFrame;
    const int RtSbtSlot = 64;                   // shader-table record alignment
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct RtShadowConstants { public Matrix4x4 InvViewProj; public Vector3 SunDir; public float NormalBias; }

    // --- DXR ray-traced reflections (Reflection volume SSR-vs-RT dropdown: PostFX.ReflectionMode) ---
    // Reuses Dx12SceneAS + the SSR reflection target (ssrTarget) + the SSR combine (ssrCombinePso): the RT
    // pass writes (reflected color, strength) into ssrTarget, then the existing depth-aware Fresnel combine
    // mixes it into the scene. DxrReflections.hlsl shades misses as the sky/IBL cube and hits as ambient
    // grey (full per-instance material shade = follow-up). Mirror rays are deterministic → no denoise yet.
    ID3D12RootSignature rtReflRootSig;          // CBV(b0) + table{SRV t0 TLAS,t1 depth,t2 normal,t3 mat,t4 irr,t5 pref; UAV u0} + s0
    ID3D12StateObject rtReflPso;
    ID3D12Resource rtReflSbt, rtReflCb;
    unsafe byte* rtReflCbMapped;
    Dx12DescriptorHeap rtReflHeap;              // 7 descriptors (rebuilt per frame)
    bool rtReflBuilt;
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct RtReflConstants {
        public Matrix4x4 InvViewProj; public Vector3 CameraPos; public float Intensity;
        public float PrefilterMaxMip; public float NormalBias; public Vector2 Pad;
    }

    // --- DXR ray-traced GI (GI volume Off/SSGI/RT-GI enum: PostFX.GiMode) ---
    // DxrGi.hlsl cosine-samples one hemisphere ray per pixel → the raw one-bounce GI in ssgiTarget; then the
    // SHARED SSGI pipeline (motion temporal + OIDN + combine) cleans + adds it. RT GI just replaces the
    // SSILVB gather. Reuses device5 + sceneAS.
    ID3D12RootSignature rtGiRootSig;            // CBV b0/b1 + table{t0-t4,u0} + SRV t5 materials + t6 instances + bindless
    ID3D12StateObject rtGiPso;
    ID3D12Resource rtGiSbt, rtGiCb, rtGiSunCb;
    unsafe byte* rtGiCbMapped, rtGiSunCbMapped;
    Dx12DescriptorHeap rtGiHeap;                // 6 descriptors (rebuilt per frame)
    Dx12RtGeometry rtGeometry;                  // P1: per-instance index/normal/uv/tri-material SRVs (bindless)
    Dx12Ddgi ddgi;                              // P2: DDGI world-probe radiance cache (BALLISTIC_DX12_DDGI=1)
    bool ddgiLogged;
    bool ddgiDebugDumped;                        // BALLISTIC_DX12_DDGI_DEBUG=1: one-shot atlas readback stats
    bool? ddgiOn;
    bool DdgiEnabled => ddgiOn ??= Environment.GetEnvironmentVariable("BALLISTIC_DX12_DDGI") == "1";

    // Phase 4: screen-space radiance probes (final gather). When DDGI is on, the screen-probe gather
    // (Place→Trace→Blend→Integrate, bilateral-upsampled, miss → DDGI world cache) is the DEFAULT near/mid-field
    // GI source — DDGI is the far-field cache. This is the published Lumen screen-trace → world-cache hierarchy.
    // Phase 4 is hardened (bilateral + deterministic + budgeted + measured LESS noisy than the per-pixel DDGI
    // gather), so it is now PRIMARY (2026-06-16 flip). THREE-STATE door: unset / "1" → screen probes (default);
    // ONLY "0" → the per-pixel DDGI gather fallback. SCREENPROBE=0 reproduces the exact pre-flip DDGI-gather
    // image (the byte-identical regression oracle). DDGI-on only — with DDGI off this path is untouched (the
    // screen-probe trace needs the DDGI field for its far-field ray-miss handoff). P4.0: uniform rays + naive
    // upsample; P4.1: bilateral integrate + E→L energy fix; P4.3: determinism + budget.
    Dx12ScreenProbe screenProbe;
    bool screenProbeLogged;
    bool? screenProbeOn;
    bool ScreenProbeEnabled => screenProbeOn ??= Environment.GetEnvironmentVariable("BALLISTIC_DX12_SCREENPROBE") != "0";
    bool rtGiBuilt;
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct RtGiConstants { public Matrix4x4 InvViewProj; public Matrix4x4 ViewProj; public Vector4 Params; }  // preExp, rayLength, _, frameIdx
    // P1 world-radiance hit shading: the sun + normal bias for the hit's direct-light term + shadow ray,
    // plus the punctual-light count (the hit shader loops all gathered point/spot lights — scenes lit only
    // by a point light, like the Bistro interior, get no bounce from sun/IBL alone).
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct RtGiSun { public Vector3 SunDir; public float NormalBias; public Vector3 SunColor; public float LightCount; }

    float SsgiPreExposure() => float.TryParse(Environment.GetEnvironmentVariable("BALLISTIC_DX12_EXPOSURE"),
        System.Globalization.CultureInfo.InvariantCulture, out float e) ? e : 1.0e-5f;

    // BALLISTIC_DETERMINISTIC=1 — byte-deterministic, frame-INDEPENDENT captures (the documented "frame 60 ==
    // frame 240" contract; `bal render`/`bal gbuffer` set it). On DX12 this had NO consumer (it was a GL-only
    // implementation), so TAA's per-frame Halton jitter + the SSGI/DDGI temporal EMA left captures frame-count-
    // dependent — defeating P2.5's DDGI warm-up freeze downstream. Wired here (P2.5): kill TAA jitter (force
    // taaOn off → no sub-pixel G-buffer perturbation, no jitter-history EMA) + make the SSGI temporal pass a
    // pass-through (HasHistory=0 → the resolve returns the current GI directly, no frame-count-dependent EMA).
    // Exposure is already pinned by BALLISTIC_DX12_EXPOSURE. Result: every captured frame is identical regardless
    // of SCREENSHOT_FRAME, so DDGI (and any GI) captures are truly diffable across builds.
    bool? deterministicOn;
    bool DeterministicCapture => deterministicOn ??= Environment.GetEnvironmentVariable("BALLISTIC_DETERMINISTIC") == "1";

    // GI-ISOLATE debug view (P0 measurement harness): when on, the SSGI/RT-GI combine outputs ONLY the
    // indirect bounce it adds (not scene+bounce) so the indirect contribution is directly visible + diffable
    // in enclosed interiors — the antidote the GI plan is built around (judge GI by the isolated bounce, never
    // the composite mean). Env door BALLISTIC_DX12_GI_ISOLATE=1 (headless A/B); the editor can drive it later
    // via PostFX. Falls back to the volume's SsgiDebugView so the existing inspector toggle works headless too.
    bool GiIsolateOn() =>
        Environment.GetEnvironmentVariable("BALLISTIC_DX12_GI_ISOLATE") == "1" || PostFX.SsgiDebugView;

    // Transparent forward pass: after deferred + sky, draw Material.Transparent submeshes back-to-front,
    // alpha-blended, depth-testing the G-buffer depth (LEqual, no write). Full forward PBR (sun + IBL +
    // shadows + clustered punctual) sampling material maps directly (TransparentForward.hlsl).
    ID3D12RootSignature transparentRootSig;  // b0 TransparentConstants + b1 FrameConstants + 6-SRV material table + 7-SRV lighting table + 2 samplers
    ID3D12PipelineState transparentPso;
    ID3D12Resource transparentCb;            // per-draw TransparentConstants ring
    unsafe byte* transparentCbMapped;
    int transparentCbSlotSize, transparentCbSlotCount;
    Dx12DescriptorHeap transparentSrvVisible; // per frame: 7 lighting SRVs + 6 material SRVs per draw
    readonly List<(IStaticMeshRenderer r, int submesh, float dist)> transparentItems = new();

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct TransparentConstants {
        public Matrix4x4 Mvp;
        public Matrix4x4 Model;
        public Matrix4x4 View;
        public Vector3 LightDir; public float Exposure;
        public Vector3 LightColor; public float Metallic;
        public Vector3 Ambient; public float Roughness;
        public Vector3 CameraPos; public float SpecularReflectance;
        public Vector4 BaseColorFactor;
        public Vector3 EmissiveFactor; public float HasEmissive;
        public float NormalStrength, NormalFlipY, HasMetallicMap, HasRoughnessMap;
        public float PackedOrm, Cutout, UseIBL, PrefilterMaxMip;
        public float Opacity, PunctualCount; public Vector2 ScreenSize;
        public Vector2 ClusterNearFar; public Vector2 Pad;
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

    // MUST match ProceduralSky.hlsl's cbuffer AND Dx12IblBaker.ProcSkyConstants byte-for-byte.
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct ProcSkyConstants {
        public Matrix4x4 ViewProjNoTranslate;
        public Vector3 SunDirection; public float SunAngularRadius;
        public Vector3 SunRadiance; public float SunDiskIntensity;
        public Vector3 GroundAlbedo; public float AirDensity;
        public float Haze, HazeAnisotropy, OzoneDensity, MultiScatter;
        public float Exposure, BakeFace; public Vector2 Pad0;
        // Volumetric clouds + cirrus + stars (GL Sky_Procedural.glsl parity).
        public float CloudsEnabled, CloudCoverage, CloudDensity, CloudAltitude;
        public float CloudThickness, CloudScale, CloudDetail, CloudAmbient;
        public Vector3 CloudWindOffset; public float CloudWindAngle;
        public float CirrusCoverage, StarIntensity; public Vector2 Pad1;
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

    // Editor viewport display: the final LDR composite (`ldr`) is mirrored into the shared shader-visible
    // UI heap (Dx12Backend.UiHeap) so the editor's ImGui pass can sample it via ImGui.Image. SceneColorHandle/
    // GameColorHandle return the cached GPU descriptor ptr (a trivial field read — no per-frame descriptor
    // churn, which is banned in the hot path). Single `ldr` today, so Scene and Game alias the same texture
    // (the editor renders one ActiveTarget per frame). RenderHandle.None until the first allocation.
    int ldrUiSlot = -1;
    nint ldrUiHandle;
    public override RenderHandle SceneColorHandle => new(ldrUiHandle);
    public override RenderHandle GameColorHandle => new(ldrUiHandle);

    // The final composited LDR color resource (R8G8B8A8_UNORM). The windowed DX12 player host blits this
    // straight into the swapchain backbuffer (PresentToScreen path); the editor samples it via the UI heap.
    public ID3D12Resource DisplayResource => ldr?.RenderTarget;
    public int DisplayWidth => outputW;
    public int DisplayHeight => outputH;

    // Spatial-query substrate (the AI agent's "eyes"): a GpuSceneQuery over the scene TLAS. Created on
    // demand (not per-frame) — the headless `bal query` path + the editor live-query surface use it. Owns
    // its OWN Dx12SceneAS so queries work with RT render effects off. See GpuSceneQuery / the proposal doc.
    public GpuSceneQuery CreateSceneQuery() => new GpuSceneQuery(dev);
    // DX12 textures are top-down → the editor must NOT flip V (unlike GL's bottom-up textures).
    public override bool DisplayTextureTopDown => true;

    // Mirror the LDR composite's SRV into the shared UI heap at a STABLE slot (re-pointed on resize so the
    // ImGui handle stays constant). Headless registers too — harmless; the handle is just never sampled
    // without an editor present. Requires `ldr` to have been created colorReadable (so it owns an SRV).
    void RegisterLdrUi() {
        if (Dx12Backend.UiHeap == null) return;
        if (ldrUiSlot < 0) ldrUiSlot = Dx12Backend.UiHeap.Allocate();
        Dx12Backend.RegisterUiAt(ldrUiSlot, ldr.ColorSrvCpu);
        ldrUiHandle = (nint)Dx12Backend.UiHeap.Gpu(ldrUiSlot).Ptr;
    }

    public override void ResizeSceneTarget(int width, int height) => Resize(width, height);
    public override void ResizeGameTarget(int width, int height) => Resize(width, height);

    void Resize(int width, int height) {
        if (width <= 0 || height <= 0) return;
        if (target != null && width == outputW && height == outputH) return;
        outputW = width; outputH = height;
        // Reset to native (internal == output); BeginRender's EnsureUpscaleTargets re-derives the internal
        // render resolution from the volume's UpscaleMode and reallocates if FSR wants a smaller render res.
        fsrActive = false;
        currentUpscaleMode = UpscaleMode.Off;
        fsr?.Dispose(); fsr = null;
        AllocateResolutionTargets(width, height);
    }

    // (Re)allocate every resolution-dependent target. internalW/H = the render resolution (scene + all post
    // passes); ldr + fsrOutput are at the output resolution. Called on resize and on an FSR mode change.
    void AllocateResolutionTargets(int internalW, int internalH) {
        // GPU MUST be idle before freeing the old targets: a resize (e.g. dragging the editor from the 4K
        // to the 1080p monitor) reallocates these while the previous frame's commands may still read them.
        // Disposing under an active GPU read is a use-after-free → TDR → DXGI_ERROR_DEVICE_REMOVED. Flush
        // also drains in-flight worker uploads (see Dx12Device.Flush). Realloc is rare (resize / FSR mode).
        dev.Flush();
        targetW = internalW; targetH = internalH;
        target?.Dispose(); ldr?.Dispose(); gbuffer?.Dispose();
        // The HDR scene target no longer owns depth — the G-buffer owns the scene depth (deferred path).
        target = new Dx12OffscreenTarget(dev, internalW, internalH, withDepth: false,
            colorFormat: Dx12OffscreenTarget.HdrFormat, colorReadable: true);
        ldr = new Dx12OffscreenTarget(dev, outputW, outputH, colorReadable: true);   // LDR composite output (display res)
        RegisterLdrUi();
        // Editor display: the editor's ImGui pass samples ldr (SceneColorHandle) EVERY frame, including
        // before the scene first composites (long async import). Leave it sample-ready (PixelShaderResource)
        // so it's never sampled as an SRV while in RenderTarget state — that undefined access hangs the GPU
        // over many frames (DXGI_ERROR_DEVICE_HUNG). Composite transitions PSR->RT->PSR per frame thereafter.
        // Unconditional (not gated on PresentToScreen): the editor sets PresentToScreen=false AFTER Initialize,
        // and it's harmless headless (SaveBmp transitions from any state).
        ldr.ColorToShaderResource();
        gbuffer = new Dx12GBuffer(dev, internalW, internalH);
        motionPrevValid = false;                                // prev view*proj is stale after a realloc
        if (bloomRootSig != null) AllocBloomTargets();          // half-res bloom ping-pong follows size
        if (ssaoRootSig != null) AllocSsaoTargets();
        if (ssrRootSig != null) AllocSsrTarget();
        if (ssgiRootSig != null) AllocSsgiTargets();
        if (taaRootSig != null) AllocTaaTargets();
        if (rtShadowMask != null) AllocRtShadowMask();
        AllocFsrOutput();
    }

    void AllocFsrOutput() {
        fsrOutput?.Dispose();
        // Output-resolution HDR target FSR writes via UAV and the composite reads via SRV.
        fsrOutput = new Dx12OffscreenTarget(dev, outputW, outputH, withDepth: false,
            colorFormat: Dx12OffscreenTarget.HdrFormat, colorReadable: true, allowUav: true);
    }

    // Map the volume UpscaleMode to an FSR quality id.
    static uint FsrQuality(UpscaleMode m) => m switch {
        UpscaleMode.NativeAA => FfxApi.QualityNativeAA,
        UpscaleMode.Quality => FfxApi.QualityQuality,
        UpscaleMode.Balanced => FfxApi.QualityBalanced,
        UpscaleMode.Performance => FfxApi.QualityPerformance,
        UpscaleMode.UltraPerformance => FfxApi.QualityUltraPerformance,
        _ => FfxApi.QualityQuality,
    };

    // Ensure the internal render resolution + FSR context match the requested upscale mode. Reallocates the
    // internal-res targets (and recreates the FSR context) only when the mode actually changes. If the FSR
    // DLLs can't load it latches off and renders native (graceful degrade on a clean checkout).
    void EnsureUpscaleTargets(UpscaleMode mode) {
        bool wantFsr = mode != UpscaleMode.Off && !fsrUnavailable;
        int wantIW = outputW, wantIH = outputH;
        if (wantFsr) {
            try { (wantIW, wantIH) = Dx12FsrUpscaler.RenderResolutionFor(outputW, outputH, FsrQuality(mode)); }
            catch (Exception e) {
                Console.WriteLine($"[FSR] unavailable, rendering native: {e.Message}");
                fsrUnavailable = true; wantFsr = false; wantIW = outputW; wantIH = outputH;
            }
        }
        if (target != null && wantIW == targetW && wantIH == targetH && fsrActive == wantFsr) {
            currentUpscaleMode = mode;
            return;   // nothing to reallocate
        }
        AllocateResolutionTargets(wantIW, wantIH);
        if (wantFsr) {
            try {
                fsr?.Dispose();
                fsr = new Dx12FsrUpscaler(dev, wantIW, wantIH, outputW, outputH);
            } catch (Exception e) {
                Console.WriteLine($"[FSR] context create failed, rendering native: {e.Message}");
                fsrUnavailable = true; wantFsr = false;
            }
        }
        fsrActive = wantFsr;
        currentUpscaleMode = mode;
    }

    public override unsafe void Initialize() {
        // Clustered-deferred: geometry → G-buffer (owns scene depth) → deferred lighting → HDR `target`
        // (color only) → sky/fog/post → composite into `ldr` (R8). `target` no longer owns depth.
        // At init internal == output (FSR off); EnsureUpscaleTargets adjusts once a volume requests FSR.
        outputW = targetW; outputH = targetH;
        target = new Dx12OffscreenTarget(dev, targetW, targetH, withDepth: false,
            colorFormat: Dx12OffscreenTarget.HdrFormat, colorReadable: true);
        ldr = new Dx12OffscreenTarget(dev, targetW, targetH, colorReadable: true);
        RegisterLdrUi();
        ldr.ColorToShaderResource();   // sample-safe before first composite (see AllocateResolutionTargets)
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
        BuildTransparentPass();
        BuildFog();
        BuildSsr();
        BuildSsgi();
        BuildTaa();
        BuildComposite();

        // GPU-driven geometry path (compute cull + ExecuteIndirect + bindless) for whole-mesh renderers.
        // DEFAULT ON (byte-identical to the CPU path, verified on Bistro + SunTemple); BALLISTIC_DX12_GPUDRIVEN=0
        // falls back to the per-submesh CPU draw loop. Mirrors the GL BALLISTIC_GPUDRIVEN convention.
        gpuDrivenOn = Environment.GetEnvironmentVariable("BALLISTIC_DX12_GPUDRIVEN") != "0";
        // Hi-Z occlusion cull: DEFAULT ON (verified byte-identical + culls 894->224 submeshes on SunTemple);
        // BALLISTIC_DX12_GPUDRIVEN_HIZ=0 disables it. Mirrors the GL BALLISTIC_GPUDRIVEN_HIZ convention.
        hizWanted = gpuDrivenOn && Environment.GetEnvironmentVariable("BALLISTIC_DX12_GPUDRIVEN_HIZ") != "0";
        // Cascade caching: DEFAULT ON (BALLISTIC_DX12_SHADOW_CACHE=0 disables).
        shadowCacheOn = Environment.GetEnvironmentVariable("BALLISTIC_DX12_SHADOW_CACHE") != "0";
        gpuDriven = new Dx12GpuDrivenRenderer(dev);

        AllocFsrOutput();   // output-res UAV target for FSR (allocated even when off — cheap, simplifies resize)
    }

    Dx12GpuDrivenRenderer gpuDriven;
    bool gpuDrivenOn;
    bool hizWanted;
    Vector3 hizLastCamPos;
    bool hizPrimed;     // false until we have a valid previous-frame depth (first frame / after a big jump)
    readonly System.Collections.Generic.List<IStaticMeshRenderer> wholeMeshRenderers = new();

    // Cascade caching: skip re-rendering the sun cascades when the texel-snapped fit matrices AND the caster
    // geometry are unchanged (the depth-array layers are retained → byte-identical; big win for a static camera).
    bool shadowCacheOn;
    readonly Matrix4x4[] lastCascadeMatrices = new Matrix4x4[CascadeCount];
    int lastCasterStamp;
    bool shadowMapEverRendered;

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
        // allowUav so RT reflections can write it via a UAV (SSR still writes it via the RTV).
        ssrTarget = new Dx12OffscreenTarget(dev, w, h, withDepth: false,
            colorFormat: Dx12OffscreenTarget.HdrFormat, colorReadable: true, allowUav: true);
        // Full-res scratch for the combine output (combine reads `target`, can't read+write it).
        ssrScene = new Dx12OffscreenTarget(dev, targetW, targetH, withDepth: false,
            colorFormat: Dx12OffscreenTarget.HdrFormat, colorReadable: true);
    }

    // SSGI PSOs: a half-res gather (PSGather, SSILVB horizon-bitmask) + a full-res combine (PSCombine,
    // adds the bounce into the lit scene). One root sig: SsgiConstants CBV(b0) + a 3-SRV table + clamp.
    unsafe void BuildSsgi() {
        var cbv = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        var srvRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 3, baseShaderRegister: 0);
        var srvTable = new RootParameter1(new RootDescriptorTable1(srvRange), ShaderVisibility.Pixel);
        var samp = new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0) {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp, MaxAnisotropy = 1,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        ssgiRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, new[] { cbv, srvTable }, new[] { samp })));

        string hlsl = BallisticEngine.DX12.EmbeddedShaderSource.ReadHlsl("Ssgi.hlsl");
        byte[] vs = Dx12ShaderCompiler.Compile(DxcShaderStage.Vertex, hlsl, "VSMain", "Ssgi.hlsl");
        ID3D12PipelineState MakePso(string entry) => dev.Device.CreateGraphicsPipelineState(
            new GraphicsPipelineStateDescription {
                RootSignature = ssgiRootSig, VertexShader = vs,
                PixelShader = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, hlsl, entry, "Ssgi.hlsl"),
                InputLayout = null, PrimitiveTopologyType = PrimitiveTopologyType.Triangle, SampleMask = uint.MaxValue,
                RasterizerState = RasterizerDescription.CullNone, BlendState = BlendDescription.Opaque,
                DepthStencilState = DepthStencilDescription.None,
                RenderTargetFormats = new[] { Dx12OffscreenTarget.HdrFormat }, DepthStencilFormat = Format.Unknown,
                SampleDescription = new SampleDescription(1, 0),
            });
        ssgiGatherPso = MakePso("PSGather");
        ssgiTemporalPso = MakePso("PSTemporal");
        ssgiCombinePso = MakePso("PSCombine");

        int cbSize = (System.Runtime.InteropServices.Marshal.SizeOf<SsgiConstants>() + 255) & ~255;
        ssgiCb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)cbSize), ResourceStates.GenericRead);
        ssgiCbMapped = ssgiCb.Map<byte>(0);
        // 3 SRVs each for gather + temporal + combine = 9 contiguous slots per frame.
        ssgiSrvVisible = new Dx12DescriptorHeap(dev,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 9, shaderVisible: true);
        AllocSsgiTargets();
    }

    void AllocSsgiTargets() {
        ssgiTarget?.Dispose(); ssgiScene?.Dispose(); ssgiHistoryA?.Dispose(); ssgiHistoryB?.Dispose();
        ssgiDenoised?.Dispose();
        // The zero-copy OIDN GPU path's shared buffer + dst-UAV are sized to the half-res GI; its Ensure(w,h)
        // detects the size change and rebuilds them on the next OIDN use (ssgiSharedFailed stays latched).
        int w = System.Math.Max(1, targetW / 2), h = System.Math.Max(1, targetH / 2);
        // allowUav so the RT-GI gather can write it via a UAV (the SSGI gather still uses the RTV).
        ssgiTarget = new Dx12OffscreenTarget(dev, w, h, withDepth: false,
            colorFormat: Dx12OffscreenTarget.HdrFormat, colorReadable: true, allowUav: true);
        ssgiHistoryA = new Dx12OffscreenTarget(dev, w, h, withDepth: false,
            colorFormat: Dx12OffscreenTarget.HdrFormat, colorReadable: true);
        ssgiHistoryB = new Dx12OffscreenTarget(dev, w, h, withDepth: false,
            colorFormat: Dx12OffscreenTarget.HdrFormat, colorReadable: true);
        ssgiDenoised = new Dx12OffscreenTarget(dev, w, h, withDepth: false,
            colorFormat: Dx12OffscreenTarget.HdrFormat, colorReadable: true, allowUav: true);  // OIDN GPU unpack writes it
        ssgiScene = new Dx12OffscreenTarget(dev, targetW, targetH, withDepth: false,
            colorFormat: Dx12OffscreenTarget.HdrFormat, colorReadable: true);
        ssgiHistValid = false;       // accumulated history is stale after a (re)allocation
        ssgiCpuColor = ssgiCpuOut = null;   // host buffers re-size to the new half-res
    }

    // Geometry pass PSO: same vertex layout + per-draw CBV(b0) + 6 material SRVs(t0..t5) as the forward
    // opaque path, but the pixel shader (GBuffer.hlsl) writes the 5-MRT fat G-buffer (+ motion) instead of
    // shading. Adds a per-pass MotionConstants CBV(b1) for the motion-vector reprojection.
    unsafe void BuildGeometryPass() {
        // b0 = per-draw DrawConstants (root CBV); table0 = 6 material SRVs t0..t5; b1 = MotionConstants
        // (per pass); s0 wrap sampler.
        var cbv = new RootParameter1(RootParameterType.ConstantBufferView,
            new RootDescriptor1(0, 0), ShaderVisibility.All);
        var matRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, MaterialSrvCount, baseShaderRegister: 0);
        var matTable = new RootParameter1(new RootDescriptorTable1(matRange), ShaderVisibility.Pixel);
        var motionCbv = new RootParameter1(RootParameterType.ConstantBufferView,
            new RootDescriptor1(1, 0), ShaderVisibility.Pixel);
        var wrap = new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0) {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Wrap,
            AddressV = TextureAddressMode.Wrap, AddressW = TextureAddressMode.Wrap, MaxAnisotropy = 16,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        gbufferRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.AllowInputAssemblerInputLayout,
                new[] { cbv, matTable, motionCbv }, new[] { wrap })));

        int motionCbSize = (System.Runtime.InteropServices.Marshal.SizeOf<MotionConstants>() + 255) & ~255;
        motionCb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)motionCbSize), ResourceStates.GenericRead);
        motionCbMapped = motionCb.Map<byte>(0);

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
        // 13 SRVs: G0..G3, depth, irradiance, prefilter, BRDF, shadow (t0..t8), cluster lights/grid/index
        // (t9..t11), RT shadow mask (t12).
        var srvRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 13, baseShaderRegister: 0);
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
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 13, shaderVisible: true);

        clusteredLights = new Dx12ClusteredLights(dev);
    }

    // Transparent forward PSO: same 4-stream vertex layout as the geometry pass, but a PIXEL shader that
    // shades forward (TransparentForward.hlsl) and ALPHA-BLENDS (SrcAlpha/InvSrcAlpha) with depth-test
    // LEqual + NO depth write against the G-buffer depth. Root sig: b0 per-draw TransparentConstants,
    // b1 per-frame FrameConstants, table0 = 6 material SRVs (t0..t5), table1 = 7 lighting SRVs (t6..t12:
    // irradiance/prefilter/BRDF/shadow + cluster lights/grid/index), s0 wrap + s1 clamp.
    unsafe void BuildTransparentPass() {
        var drawCbv = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        var frameCbv = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(1, 0), ShaderVisibility.Pixel);
        var matRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, MaterialSrvCount, baseShaderRegister: 0);
        var matTable = new RootParameter1(new RootDescriptorTable1(matRange), ShaderVisibility.Pixel);
        var lightRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 7, baseShaderRegister: 6);
        var lightTable = new RootParameter1(new RootDescriptorTable1(lightRange), ShaderVisibility.Pixel);
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
        transparentRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.AllowInputAssemblerInputLayout,
                new[] { drawCbv, frameCbv, matTable, lightTable }, new[] { wrap, clamp })));

        string hlsl = BallisticEngine.DX12.EmbeddedShaderSource.ReadHlsl("TransparentForward.hlsl");
        byte[] vs = Dx12ShaderCompiler.Compile(DxcShaderStage.Vertex, hlsl, "VSMain", "TransparentForward.hlsl");
        byte[] ps = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, hlsl, "PSMain", "TransparentForward.hlsl");
        var layout = new InputLayoutDescription(
            new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new InputElementDescription("NORMAL", 0, Format.R32G32B32_Float, 0, 1),
            new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 0, 2),
            new InputElementDescription("TANGENT", 0, Format.R32G32B32A32_Float, 0, 3));
        // Depth test LEqual, NO write (the G-buffer depth occludes; transparents don't write depth — sort
        // handles their order). Straight alpha blend over the HDR scene (composite tonemaps later).
        var ds = DepthStencilDescription.Default;
        ds.DepthWriteMask = DepthWriteMask.Zero;
        ds.DepthFunc = ComparisonFunction.LessEqual;
        transparentPso = dev.Device.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription {
            RootSignature = transparentRootSig, VertexShader = vs, PixelShader = ps, InputLayout = layout,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle, SampleMask = uint.MaxValue,
            RasterizerState = RasterizerDescription.CullClockwise,   // back-face cull (forward parity)
            BlendState = new BlendDescription(Blend.SourceAlpha, Blend.InverseSourceAlpha),
            DepthStencilState = ds,
            RenderTargetFormats = new[] { Dx12OffscreenTarget.HdrFormat },
            DepthStencilFormat = Dx12GBuffer.DepthFormat, SampleDescription = new SampleDescription(1, 0),
        });

        transparentCbSlotSize = (System.Runtime.InteropServices.Marshal.SizeOf<TransparentConstants>() + 255) & ~255;
        transparentCbSlotCount = 2048;   // transparent submesh draws per frame ceiling
        transparentCb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)((long)transparentCbSlotSize * transparentCbSlotCount)), ResourceStates.GenericRead);
        transparentCbMapped = transparentCb.Map<byte>(0);
        // Per frame: 7 lighting SRVs (bound once) + 6 material SRVs per draw.
        transparentSrvVisible = new Dx12DescriptorHeap(dev,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
            7 + transparentCbSlotCount * MaterialSrvCount, shaderVisible: true);
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

    readonly System.Diagnostics.Stopwatch cpuFrameSw = new();

    // Per-pass GPU timing (P0 GI measurement harness). Every DX12 pass is its own ExecuteSync = submit + a
    // blocking WaitForGpu (Dx12Device.ExecuteSync), so a CPU stopwatch around a pass's calls measures that
    // pass's GPU wall-time directly (the queue is idle between passes). Not a timestamp-query GPU-exclusive
    // number — it includes submit + fence-wait overhead — but for "is RT-GI 2ms or 20ms?" it is honest and
    // sufficient, and it needs zero query-heap plumbing through every ExecuteSync. Enable with
    // BALLISTIC_DX12_GI_TIMING=1 (or any BALLISTIC_STATS_OUT run). Recorded into RenderStats.GpuPasses, which
    // the .stats.json / `bal perf` sidecar already serializes.
    readonly System.Diagnostics.Stopwatch passSw = new();
    bool? giTimingOn;
    bool GiTimingEnabled => giTimingOn ??= (Environment.GetEnvironmentVariable("BALLISTIC_DX12_GI_TIMING") == "1"
        || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BALLISTIC_STATS_OUT")));

    // Run `body`, and if GI timing is on, record its GPU wall-time under `name` in RenderStats.GpuPasses.
    void TimePass(string name, Action body) {
        if (!GiTimingEnabled) { body(); return; }
        passSw.Restart();
        body();
        passSw.Stop();
        RenderStats.Scene.GpuPasses.Add((name, passSw.Elapsed.TotalMilliseconds));
    }

    public override unsafe RenderMetrics BeginRender(RendererArgs args) {
        IViewProjectionProvider vp = args.viewProjectionProvider;
        if (vp is null || target is null)
            return default;
        cpuFrameSw.Restart();   // CPU render-submission cost (the AI-measurable frame budget)
        if (GiTimingEnabled) RenderStats.Scene.GpuPasses.Clear();   // fresh per-pass GPU timings each frame

        // Resolve the upscale mode (volume, or a BALLISTIC_DX12_FSR env override for headless A/B) and make
        // the internal render resolution + FSR context match it (reallocates targets only on a mode change).
        // Done FIRST since it can change targetW/targetH (the projection aspect + jitter scale read them).
        EnsureUpscaleTargets(ResolveUpscaleMode());

        // Camera. The provider's view (LookAt) is convention-agnostic — convert 1:1. Rebuild the
        // projection DX-style (RH, z in [0,1]) since the provider's is OpenTK GL-convention (z in [-1,1]).
        Matrix4x4 view = ToNumerics(vp.GetViewMatrix());
        Matrix4x4 proj = Matrix4x4.CreatePerspectiveFieldOfView(
            FovYRadians, (float)targetW / targetH, CameraNear, CameraFar);
        Matrix4x4 projUnjittered = proj;   // before the jitter — the shadow cascade fit uses this (stable
                                           // across frames so cascade caching works; shadows shouldn't jitter)
        // UNJITTERED view*proj — used for motion vectors + the froxel/SSR/post math.
        Matrix4x4 viewProjUnjittered = view * proj;

        // Sub-pixel jitter: offset the projection by a Halton amount so the whole frame (geometry + SSR +
        // shadows) is consistently jittered; TAA/FSR resolve it against history. FSR REPLACES TAA but still
        // needs jitter (it reconstructs from jittered frames). currentJitter is reused by the FSR dispatch.
        // Deterministic capture: TAA off (its Halton jitter perturbs the G-buffer + accumulates a frame-count-
        // dependent history → non-diffable). FSR also needs jitter, so deterministic mode assumes FSR off
        // (the capture recipe sets BALLISTIC_DX12_FSR=off). Edges are aliased in deterministic captures — the
        // documented trade for frame-independence (same as the GL contract).
        bool taaOn = PostFX.TaaEnabled && !fsrActive && !DeterministicCapture;
        bool jitterOn = taaOn || fsrActive;
        currentJitter = jitterOn ? JitterOffset(taaFrame) : Vector2.Zero;
        if (jitterOn) {
            // NDC offset = 2 * pixelJitter / screen. DX clip y is up, so subtract for the +y pixel dir.
            proj.M31 += 2f * currentJitter.X / targetW;
            proj.M32 -= 2f * currentJitter.Y / targetH;
        }
        Matrix4x4 viewProj = view * proj;   // JITTERED — geometry/SSR/etc. render with this

        // Motion-vector constants (b1): UNJITTERED current + previous view*proj. First frame (or after a
        // resize) has no valid previous frame → use the current matrix so motion = 0 everywhere.
        Matrix4x4 viewProjPrevForMotion = motionPrevValid ? motionPrevViewProj : viewProjUnjittered;
        *(MotionConstants*)motionCbMapped = new MotionConstants {
            ViewProjCur = Matrix4x4.Transpose(viewProjUnjittered),
            ViewProjPrev = Matrix4x4.Transpose(viewProjPrevForMotion),
        };

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

        // GPU-driven: collect whole-mesh renderers once (used by BOTH the shadow pass and the geometry pass).
        wholeMeshRenderers.Clear();
        if (gpuDrivenOn) {
            foreach (IStaticMeshRenderer r in RuntimeSet<IStaticMeshRenderer>.ReadOnlyCollection)
                if (r is { IsActive: true, IsRenderable: true } && r.SubMeshIndex < 0 && r.SharedMesh != null)
                    wholeMeshRenderers.Add(r);
        }

        // Shadows first: render the sun cascades' depth (own upload command list) before opaque. Fit with the
        // UNJITTERED proj so the cascades are stable frame-to-frame (cascade caching + no TAA shadow jitter).
        RenderShadows(view, projUnjittered, light);

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
        int culled = 0;
        long tris = 0;
        srvVisible.Reset();
        int slot = 0;

        // Camera frustum planes from the UNJITTERED viewProj — per-submesh cull in the geometry pass.
        ExtractFrustumPlanes(viewProjUnjittered);

        // GPU-driven: whole-mesh renderers were collected before RenderShadows; build their bindless table.
        if (gpuDrivenOn)
            gpuDriven.EnsureMaterialTable(wholeMeshRenderers);

        // Hi-Z: build the occlusion pyramid from the PREVIOUS frame's depth (before the geometry pass clears
        // it). Camera-delta gate: disable for one frame after a big jump (stale depth) + the first frame.
        bool hizEnabled = false;
        if (hizWanted && wholeMeshRenderers.Count > 0) {
            float camDelta = (camPos - hizLastCamPos).Length();
            hizEnabled = hizPrimed && camDelta < 2.0f;
            hizLastCamPos = camPos;
            hizPrimed = true;
            gbuffer.DepthToNonPixelShaderResource();
            gpuDriven.BuildHiZ(gbuffer.DepthSrvCpu, targetW, targetH, hizEnabled);
        }

        var fallbackDiffuse = DefaultTextures.Neutral(TextureType.Diffuse) as Dx12Texture2D;

        // === GEOMETRY PASS: fill the fat G-buffer (no lighting — GBuffer.hlsl writes albedo/normal/ORM/
        // emissive + depth). Same vertex transform + material sampling as the old forward opaque. ===
        gbuffer.RenderGeometry(cl => {
            cl.SetGraphicsRootSignature(gbufferRootSig);
            cl.SetPipelineState(gbufferPso);
            cl.SetDescriptorHeaps(srvVisible.Heap);
            cl.SetGraphicsRootConstantBufferView(2, motionCb.GPUVirtualAddress);   // b1 motion (per pass)
            cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);

            foreach (IStaticMeshRenderer r in RuntimeSet<IStaticMeshRenderer>.ReadOnlyCollection) {
                if (r is null || !r.IsActive || !r.IsRenderable) continue;
                // Whole-mesh renderers are GPU-driven (compute cull + ExecuteIndirect) — skip them here.
                if (gpuDrivenOn && r.SubMeshIndex < 0) continue;
                Mesh mesh = r.SharedMesh;
                if (mesh is null) continue;

                var vb = mesh.VertexBuffer as Dx12Buffer<GLVector3>;
                var ib = mesh.IndexBuffer as Dx12IndexBuffer;
                var nb = mesh.NormalBuffer as Dx12Buffer<GLVector3>;
                var ub = mesh.UvBuffer as Dx12Buffer<Vector2>;
                var tb = mesh.TangentBuffer as Dx12Buffer<Vector4>;
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
                    // Per-submesh frustum cull (camera frustum from the UNJITTERED viewProj).
                    mesh.GetSubMeshBounds(s, out GLVector3 lmin, out GLVector3 lmax);
                    if (!AabbInFrustum(lmin, lmax, model)) { culled++; continue; }
                    Material mat = r.MaterialFor(s);
                    if (mat is null) continue;
                    // Transparent submeshes can't be deferred-shaded (no blending in a G-buffer) — they're
                    // drawn FORWARD after deferred lighting + sky (DrawTransparents). Skip them here.
                    if (mat.Transparent) continue;
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

            // GPU-driven whole-mesh geometry: compute cull + ExecuteIndirect + bindless materials, into the
            // same G-buffer. Uses the JITTERED viewProj for the per-draw Mvp (matches the CPU path) and the
            // UNJITTERED frustum planes for culling (byte-identical visible set).
            if (gpuDrivenOn && wholeMeshRenderers.Count > 0) {
                draws += gpuDriven.RenderInto(cl, wholeMeshRenderers, viewProj, frustumPlanes,
                    viewProjUnjittered, view, CameraNear, CameraFar, motionCb.GPUVirtualAddress);
                tris += gpuDriven.LastTris;
            }
        });

        // Hi-Z debug door: how many whole-mesh submeshes survived the GPU cull (frustum + Hi-Z occlusion).
        if (gpuDrivenOn && wholeMeshRenderers.Count > 0
            && Environment.GetEnvironmentVariable("BALLISTIC_DX12_HIZ_DEBUG") == "1") {
            var (vis, tot) = gpuDriven.DebugVisibleCount();
            Console.WriteLine($"[HiZDebug] visible submeshes {vis}/{tot} (hizEnabled={(hizEnabled ? 1 : 0)})");
        }

        // === CLUSTERED PUNCTUAL LIGHTS: gather active point/spot lights + CPU froxel-cull (before the
        // deferred pass reads the result). Lights are raw HDR (NOT pre-exposed — composite meters them,
        // same as the sun). ===
        GatherPunctualLights(view, proj);

        // === DEFERRED LIGHTING: read the G-buffer + depth → PBR sun + IBL + shadows + punctual → HDR. ===
        gbuffer.ToShaderResource();

        // === RT SUN SHADOWS (volume-driven; DXR): trace one shadow ray per pixel against the scene BVH into
        // a mask the deferred sun term reads (replaces the cascade PCF). Opt-in via the Shadows volume's RT
        // checkbox or BALLISTIC_DX12_RT_SHADOWS=1; falls back to cascades if DXR is unavailable. Runs after
        // the G-buffer is readable, before deferred lighting. ===
        rtShadowsThisFrame = false;
        string rtsEnv = Environment.GetEnvironmentVariable("BALLISTIC_DX12_RT_SHADOWS");
        bool rtShadowsWanted = rtsEnv == "1" || (rtsEnv != "0" && PostFX.RayTracedShadows);
        if (rtShadowsWanted && EnsureRtShadows())
            DrawRtShadows(viewProj, lightDir);

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

        // === TRANSPARENTS: forward, back-to-front, alpha-blended over the HDR scene + sky, depth-testing
        // the G-buffer depth (LEqual, no write). Runs before fog/SSR/TAA so they apply over the glass. ===
        DrawTransparents(view, viewProj, camPos, lightDir, lightColor, ambient);

        // --- SSGI (volume-driven screen-space GI): local one-bounce light added to the lit scene, BEFORE
        // fog/SSR so they apply over the GI-enriched colour (matches the GL order). Gather (SSILVB) +
        // motion-buffer temporal accumulation + OIDN denoise. Driven by the ScreenSpaceGlobalIllumination
        // VOLUME (PostFX.SsgiEnabled); BALLISTIC_DX12_SSGI=1/0 force-overrides for A/B + perf. NOTE: the OIDN
        // denoise round-trip is currently a CPU readback (slow); the zero-copy D3D12<->HIP path is the perf
        // follow-up (BALLISTIC_DX12_SSGI_OIDN=0 = fast temporal-only meanwhile). ---
        // GI: Off / SSGI / RT-GI (the GI volume dropdown; env doors override). RT-GI traces the scene BVH;
        // both SSGI and RT-GI share the temporal + OIDN + combine resolve. RT-GI falls back to SSGI w/o DXR.
        string ssgiEnv = Environment.GetEnvironmentVariable("BALLISTIC_DX12_SSGI");
        string rtgiEnv = Environment.GetEnvironmentVariable("BALLISTIC_DX12_RT_GI");
        GiMode giMode = rtgiEnv == "1" ? GiMode.RayTraced
                      : ssgiEnv == "0" ? GiMode.Off
                      : ssgiEnv == "1" ? GiMode.ScreenSpace
                      : PostFX.GiMode;
        // P7.0 NO-RT AUTO-DOWNGRADE: on a GPU without hardware ray tracing (the audience floor; or BALLISTIC_DX12_
        // FORCE_NORT=1 on the dev card), RayTraced GI → ScreenSpace BEFORE EnsureRtGi/DrawRtGi touch the DXR path.
        // This is ABSOLUTE — even the BALLISTIC_DX12_RT_GI=1 force door loses to it, because a forced RT path on a
        // non-DXR device is the device-removal/PC-crash hazard ([[gpu-hang-launch-safety]]). EnsureRtGi already
        // returns false without RT, so GI was guarded; this makes the intent explicit + logs once for all 3 effects.
        if (!dev.HasHardwareRayTracing) {
            if (giMode == GiMode.RayTraced) giMode = GiMode.ScreenSpace;
            WarnNoRtOnce();
        }
        if (giMode == GiMode.RayTraced) { if (EnsureRtGi()) TimePass("GI:RT", () => DrawRtGi(view, viewProj, proj, lightDir, lightColor, camPos)); else TimePass("GI:SSGI", () => DrawSsgi(view, proj)); }
        else if (giMode == GiMode.ScreenSpace) TimePass("GI:SSGI", () => DrawSsgi(view, proj));

        // --- Volumetric fog (post pass, reads depth+shadows, blends over HDR scene color) ---
        // BALLISTIC_FX_VOLUMETRIC=1 forces it on (same harness contract as the GL backend).
        bool fogOn = PostFX.VolumetricEnabled
            || Environment.GetEnvironmentVariable("BALLISTIC_FX_VOLUMETRIC") == "1";
        if (fogOn)
            DrawFog(view, viewProj, camPos, light);

        // --- Reflections (volume-driven, SSR vs RT). RT reflections trace the scene BVH (off-screen + sky
        // correct), reusing the SSR reflection target + combine; SSR is the screen-space fallback. ---
        if (PostFX.SsrEnabled && PostFX.SsrIntensity > 0f) {
            string rtrEnv = Environment.GetEnvironmentVariable("BALLISTIC_DX12_RT_REFLECTIONS");
            bool rtReflWanted = rtrEnv == "1" || (rtrEnv != "0" && PostFX.ReflectionMode == ReflectionMode.RayTraced);
            if (rtReflWanted && EnsureRtReflections())
                DrawRtReflections(view, viewProj, proj, camPos);
            else
                DrawSsr(view, proj);
        }

        bool ssaoOn = Environment.GetEnvironmentVariable("BALLISTIC_DX12_SSAO") != "0";

        if (fsrActive) {
            // --- FSR upscale path (replaces TAA): SSAO (internal res) → FSR reconstruct internal→output
            //     HDR → composite at output res. ---
            if (ssaoOn) DrawSsao(view, proj);
            RunFsr();
            DrawComposite(ssaoOn, fsrOutput);
        } else {
            // --- Native path: TAA → SSAO → composite (all at the single shared resolution). ---
            if (taaOn) DrawTaa();
            else taaHistoryValid = false;   // keep history fresh for when TAA turns back on
            if (ssaoOn) DrawSsao(view, proj);
            DrawComposite(ssaoOn, target);
        }

        // Editor display path: leave the LDR composite in PixelShaderResource so the editor's ImGui pass can
        // sample it via SceneColorHandle/GameColorHandle THIS frame. The player (PresentToScreen) keeps it in
        // RenderTarget for SaveFrame's readback; either way next frame's DrawComposite transitions it back.
        if (!PresentToScreen)
            ldr.ColorToShaderResource();

        // Advance the jitter phase (used by both TAA and FSR) and remember this frame's UNJITTERED view*proj
        // for next frame's motion vectors (independent of TAA, since FSR replaces TAA but still needs motion).
        if (jitterOn) taaFrame++;
        motionPrevViewProj = viewProjUnjittered;
        motionPrevValid = true;

        RenderStats.Scene.DrawCalls = draws;
        RenderStats.Scene.Triangles = tris;
        RenderStats.Scene.SubMeshesCulled = culled;
        RenderStats.Scene.CpuFrameMs = cpuFrameSw.Elapsed.TotalMilliseconds;
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
            Vector3 dir = Vector3.Transform(Vector3.UnitZ, s.transform.WorldRotation);
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
            UseRtShadows = rtShadowsThisFrame ? 1f : 0f,
        };

        // Copy the 13 SRVs: G0..G3, depth, irradiance, prefilter, BRDF, shadow (t0..t8), cluster
        // lights/grid/index (t9..t11), RT shadow mask (t12).
        deferredSrvVisible.Reset();
        int b = deferredSrvVisible.AllocateRange(13);
        // Only the 4 SHADED G-buffer RTs feed lighting (G0..G3 → t0..t3); the motion RT (RT4) is for TAA/FSR.
        for (int i = 0; i < Dx12GBuffer.ShadedRtCount; i++)
            dev.Device.CopyDescriptorsSimple(1, deferredSrvVisible.Cpu(b + i), gbuffer.ColorSrvCpu(i), heapType);
        dev.Device.CopyDescriptorsSimple(1, deferredSrvVisible.Cpu(b + 4), gbuffer.DepthSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, deferredSrvVisible.Cpu(b + 5), ibl.IrradianceSrv, heapType);
        dev.Device.CopyDescriptorsSimple(1, deferredSrvVisible.Cpu(b + 6), ibl.PrefilterSrv, heapType);
        dev.Device.CopyDescriptorsSimple(1, deferredSrvVisible.Cpu(b + 7), ibl.BrdfSrv, heapType);
        dev.Device.CopyDescriptorsSimple(1, deferredSrvVisible.Cpu(b + 8), shadowMap.SrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, deferredSrvVisible.Cpu(b + 9), clusteredLights.LightSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, deferredSrvVisible.Cpu(b + 10), clusteredLights.GridSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, deferredSrvVisible.Cpu(b + 11), clusteredLights.IndexSrvCpu, heapType);
        // RT shadow mask (t12) — the real mask when RT shadows ran this frame, else a valid unused fallback.
        dev.Device.CopyDescriptorsSimple(1, deferredSrvVisible.Cpu(b + 12),
            rtShadowMask != null ? rtShadowMask.ColorSrvCpu : gbuffer.DepthSrvCpu, heapType);

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

    // Forward transparent pass (clustered-deferred can't blend a G-buffer, so transparents are FORWARD).
    // Collects every Material.Transparent submesh (frustum-culled), sorts back-to-front by world-space
    // submesh-center distance, then draws each alpha-blended over the HDR scene + sky, depth-testing the
    // G-buffer depth (LEqual, no write). Full forward PBR (sun + IBL + shadows + clustered punctual),
    // sampling material maps directly. The per-frame lighting SRVs (IBL/shadow/cluster) bind once.
    unsafe void DrawTransparents(Matrix4x4 view, Matrix4x4 viewProj, Vector3 camPos,
                                 Vector3 lightDir, Vector3 lightColor, Vector3 ambient) {
        // 1) Gather transparent submeshes (per-submesh frustum cull, like the geometry pass).
        transparentItems.Clear();
        foreach (IStaticMeshRenderer r in RuntimeSet<IStaticMeshRenderer>.ReadOnlyCollection) {
            if (r is null || !r.IsActive || !r.IsRenderable) continue;
            Mesh mesh = r.SharedMesh;
            if (mesh is null) continue;
            Matrix4x4 model = ToNumerics(r.Transform.WorldMatrix);
            int only = r.SubMeshIndex;
            int first = only >= 0 ? only : 0;
            int last = only >= 0 ? only : mesh.SubMeshes.Length - 1;
            for (int s = first; s <= last; s++) {
                if ((uint)s >= (uint)mesh.SubMeshes.Length) break;
                if (mesh.SubMeshes[s].IndexCount <= 0) continue;
                Material mat = r.MaterialFor(s);
                if (mat is null || !mat.Transparent) continue;
                mesh.GetSubMeshBounds(s, out GLVector3 lmin, out GLVector3 lmax);
                if (!AabbInFrustum(lmin, lmax, model)) continue;
                // world-space submesh center for the back-to-front sort
                var localCenter = new Vector3((lmin.X + lmax.X) * 0.5f, (lmin.Y + lmax.Y) * 0.5f, (lmin.Z + lmax.Z) * 0.5f);
                Vector3 worldCenter = Vector3.Transform(localCenter, model);
                transparentItems.Add((r, s, (worldCenter - camPos).LengthSquared()));
            }
        }
        if (transparentItems.Count == 0) return;

        // Back-to-front: farthest first (descending squared distance).
        transparentItems.Sort((a, c) => c.dist.CompareTo(a.dist));

        // 2) Per-frame lighting SRVs (t6..t12: irradiance, prefilter, BRDF, shadow + cluster lights/grid/index).
        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        transparentSrvVisible.Reset();
        int lightBase = transparentSrvVisible.AllocateRange(7);
        dev.Device.CopyDescriptorsSimple(1, transparentSrvVisible.Cpu(lightBase + 0), ibl.IrradianceSrv, heapType);
        dev.Device.CopyDescriptorsSimple(1, transparentSrvVisible.Cpu(lightBase + 1), ibl.PrefilterSrv, heapType);
        dev.Device.CopyDescriptorsSimple(1, transparentSrvVisible.Cpu(lightBase + 2), ibl.BrdfSrv, heapType);
        dev.Device.CopyDescriptorsSimple(1, transparentSrvVisible.Cpu(lightBase + 3), shadowMap.SrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, transparentSrvVisible.Cpu(lightBase + 4), clusteredLights.LightSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, transparentSrvVisible.Cpu(lightBase + 5), clusteredLights.GridSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, transparentSrvVisible.Cpu(lightBase + 6), clusteredLights.IndexSrvCpu, heapType);

        var fallbackDiffuse = DefaultTextures.Neutral(TextureType.Diffuse) as Dx12Texture2D;
        float prefMaxMip = ibl != null ? ibl.PrefilterMipCount - 1 : 0f;
        float useIbl = iblActiveThisFrame ? 1f : 0f;
        float punctualCount = clusteredLights.LightCount;
        int tslot = 0;

        // 3) Draw back-to-front into the HDR color, depth-testing the G-buffer depth (already DepthRead).
        target.RenderColorWithExternalDepth(gbuffer.DsvHandle, cl => {
            cl.SetGraphicsRootSignature(transparentRootSig);
            cl.SetPipelineState(transparentPso);
            cl.SetDescriptorHeaps(transparentSrvVisible.Heap);
            cl.SetGraphicsRootConstantBufferView(1, frameCb.GPUVirtualAddress);
            cl.SetGraphicsRootDescriptorTable(3, transparentSrvVisible.Gpu(lightBase));
            cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);

            foreach (var item in transparentItems) {
                if (tslot >= transparentCbSlotCount) break;
                IStaticMeshRenderer r = item.r;
                Mesh mesh = r.SharedMesh;
                var vb = mesh.VertexBuffer as Dx12Buffer<GLVector3>;
                var ib = mesh.IndexBuffer as Dx12IndexBuffer;
                var nb = mesh.NormalBuffer as Dx12Buffer<GLVector3>;
                var ub = mesh.UvBuffer as Dx12Buffer<Vector2>;
                var tb = mesh.TangentBuffer as Dx12Buffer<Vector4>;
                if (vb?.Resource is null || ib?.Resource is null ||
                    nb?.Resource is null || ub?.Resource is null || tb?.Resource is null) continue;
                SubMeshData sub = mesh.SubMeshes[item.submesh];
                Material mat = r.MaterialFor(item.submesh);
                if (mat is null) continue;

                Matrix4x4 model = ToNumerics(r.Transform.WorldMatrix);
                Matrix4x4 mvp = model * viewProj;
                bool hasMetal = mat.Metallic is not null;
                bool hasRough = mat.Roughness is not null;
                var c = new TransparentConstants {
                    Mvp = Matrix4x4.Transpose(mvp), Model = Matrix4x4.Transpose(model),
                    View = Matrix4x4.Transpose(view),
                    LightDir = lightDir, Exposure = 1f, LightColor = lightColor, Metallic = mat.MetallicFactor,
                    Ambient = ambient, Roughness = mat.RoughnessFactor,
                    CameraPos = camPos, SpecularReflectance = mat.SpecularReflectance,
                    BaseColorFactor = ToNumerics(mat.BaseColorFactor),
                    EmissiveFactor = ToNumerics(mat.EmissiveColor) * mat.EmissiveIntensity,
                    HasEmissive = mat.IsEmissive ? 1f : 0f,
                    NormalStrength = mat.NormalStrength, NormalFlipY = mat.NormalFlipY ? 1f : 0f,
                    HasMetallicMap = hasMetal ? 1f : 0f, HasRoughnessMap = hasRough ? 1f : 0f,
                    PackedOrm = mat.PackedOrm ? 1f : 0f, Cutout = mat.Cutout ? 1f : 0f,
                    UseIBL = useIbl, PrefilterMaxMip = prefMaxMip,
                    Opacity = mat.Opacity, PunctualCount = punctualCount,
                    ScreenSize = new Vector2(targetW, targetH), ClusterNearFar = new Vector2(CameraNear, CameraFar),
                };
                *(TransparentConstants*)(transparentCbMapped + (long)tslot * transparentCbSlotSize) = c;
                cl.SetGraphicsRootConstantBufferView(0,
                    transparentCb.GPUVirtualAddress + (ulong)((long)tslot * transparentCbSlotSize));

                int matBase = transparentSrvVisible.AllocateRange(MaterialSrvCount);
                BindSrvInto(transparentSrvVisible, matBase + 0, mat.Diffuse, TextureType.Diffuse, fallbackDiffuse);
                BindSrvInto(transparentSrvVisible, matBase + 1, mat.Normal, TextureType.Normal, null);
                BindSrvInto(transparentSrvVisible, matBase + 2, mat.Metallic, TextureType.Metallic, null);
                BindSrvInto(transparentSrvVisible, matBase + 3, mat.Roughness, TextureType.Roughness, null);
                BindSrvInto(transparentSrvVisible, matBase + 4, mat.AO, TextureType.AO, null);
                BindSrvInto(transparentSrvVisible, matBase + 5, mat.Emissive, TextureType.Emissive, null);
                cl.SetGraphicsRootDescriptorTable(2, transparentSrvVisible.Gpu(matBase));

                Span<VertexBufferView> vbViews = stackalloc VertexBufferView[4];
                vbViews[0] = new VertexBufferView(vb.GpuAddress, (uint)vb.ByteSize, (uint)vb.Stride);
                vbViews[1] = new VertexBufferView(nb.GpuAddress, (uint)nb.ByteSize, (uint)nb.Stride);
                vbViews[2] = new VertexBufferView(ub.GpuAddress, (uint)ub.ByteSize, (uint)ub.Stride);
                vbViews[3] = new VertexBufferView(tb.GpuAddress, (uint)tb.ByteSize, (uint)tb.Stride);
                cl.IASetVertexBuffers(0, vbViews);
                cl.IASetIndexBuffer(new IndexBufferView(ib.GpuAddress, (uint)ib.ByteSize, Format.R32_UInt));
                cl.DrawIndexedInstanced((uint)sub.IndexCount, 1, (uint)sub.IndexStart, 0, 0);
                tslot++;
            }
        });
    }

    // TAA (volume-driven): resolve the jittered HDR scene against the motion-reprojected history. Reads the
    // current HDR color (target) + history + G-buffer motion vectors, writes the resolved color into the new
    // history buffer, then copies it back to `target` so the composite tonemaps the AA'd result. Reprojection
    // is a per-pixel motion-vector add (jitter-free). History ping-pongs; invalidated on resize / first frame.
    unsafe void DrawTaa() {
        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        Dx12OffscreenTarget history = taaWriteB ? taaHistoryA : taaHistoryB;   // read from the OTHER buffer
        Dx12OffscreenTarget writeHist = taaWriteB ? taaHistoryB : taaHistoryA;

        *(TaaConstants*)taaCbMapped = new TaaConstants {
            Feedback = PostFX.TaaFeedback, ValidHistory = taaHistoryValid ? 1f : 0f,
            TexelSize = new Vector2(1f / targetW, 1f / targetH),
        };

        target.ColorToShaderResource();
        history.ColorToShaderResource();
        // Motion RT is already PixelShaderResource (gbuffer.ToShaderResource transitioned all colors).
        taaSrvVisible.Reset();
        int b = taaSrvVisible.AllocateRange(3);
        dev.Device.CopyDescriptorsSimple(1, taaSrvVisible.Cpu(b + 0), target.ColorSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, taaSrvVisible.Cpu(b + 1), history.ColorSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, taaSrvVisible.Cpu(b + 2), gbuffer.ColorSrvCpu(Dx12GBuffer.MotionRtIndex), heapType);
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
        // taaFrame advances once per frame in BeginRender (shared by TAA + FSR jitter).
    }

    // The active upscale mode: the volume's PostFX.UpscaleMode, overridable by BALLISTIC_DX12_FSR for
    // headless A/B (off/nativeaa/quality/balanced/performance/ultra) — a kept test door.
    UpscaleMode ResolveUpscaleMode() {
        string env = Environment.GetEnvironmentVariable("BALLISTIC_DX12_FSR");
        if (string.IsNullOrEmpty(env)) return PostFX.UpscaleMode;
        return env.Trim().ToLowerInvariant() switch {
            "0" or "off" => UpscaleMode.Off,
            "1" or "native" or "nativeaa" => UpscaleMode.NativeAA,
            "quality" or "q" => UpscaleMode.Quality,
            "balanced" or "b" => UpscaleMode.Balanced,
            "performance" or "perf" or "p" => UpscaleMode.Performance,
            "ultra" or "ultraperformance" or "up" => UpscaleMode.UltraPerformance,
            _ => PostFX.UpscaleMode,
        };
    }

    // FSR temporal upscale: reconstruct the output-resolution HDR from the internal-res HDR color + depth +
    // motion + jitter. Replaces TAA. Inputs are transitioned to a shader-read state and the output to UAV;
    // the FFX DX12 backend restores imported resources to those declared states at dispatch end, so the
    // engine's per-resource state trackers stay consistent.
    unsafe void RunFsr() {
        target.ColorToShaderResource();      // internal HDR scene -> PixelShaderResource
        gbuffer.DepthToShaderResource();      // depth -> PixelShaderResource
        // motion RT is already PixelShaderResource (gbuffer.ToShaderResource transitioned all colors).
        fsrOutput.ColorToUnorderedAccess();
        bool reset = !motionPrevValid;        // first frame after a (re)allocation = reset the history
        dev.ExecuteSync(cl => {
            fsr.Dispatch(cl, target.RenderTarget, gbuffer.DepthResource,
                gbuffer.MotionResource, fsrOutput.RenderTarget,
                targetW, targetH, new Dx12FsrUpscaler.Vector2Jitter(currentJitter.X, currentJitter.Y),
                16.6667f, reset, PostFX.UpscaleSharpness > 0f, PostFX.UpscaleSharpness,
                CameraNear, CameraFar, FovYRadians);
        });
        fsrOutput.ColorToShaderResource();    // ready for the composite to sample
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

    // Screen-space GI (SSILVB horizon-bitmask gather): half-res gather reads the lit HDR color + G-buffer
    // depth/normal → ssgiTarget (raw one-bounce); combine adds it into the scene (via ssgiScene scratch,
    // copied back to `target`). Step B is gather+combine (noisy); temporal accumulation (motion buffer) +
    // OIDN denoise wrap it in step C. Profile dials are PostFX.Ssgi* (volume-driven).
    unsafe void DrawSsgi(Matrix4x4 view, Matrix4x4 proj) {
        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        FillSsgiConstants(view, proj);

        target.ColorToShaderResource();
        gbuffer.DepthToShaderResource();
        // motion RT (gbuffer RT4) is already PixelShaderResource (ToShaderResource transitioned all colors).

        // Gather (half-res) → ssgiTarget. SRVs: color t0, depth t1, normal t2.
        ssgiSrvVisible.Reset();
        int gb = ssgiSrvVisible.AllocateRange(3);
        dev.Device.CopyDescriptorsSimple(1, ssgiSrvVisible.Cpu(gb + 0), target.ColorSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, ssgiSrvVisible.Cpu(gb + 1), gbuffer.DepthSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, ssgiSrvVisible.Cpu(gb + 2), gbuffer.ColorSrvCpu(1), heapType);
        ssgiTarget.RenderColorOnly(cl => {
            cl.SetGraphicsRootSignature(ssgiRootSig); cl.SetPipelineState(ssgiGatherPso);
            cl.SetDescriptorHeaps(ssgiSrvVisible.Heap);
            cl.SetGraphicsRootConstantBufferView(0, ssgiCb.GPUVirtualAddress);
            cl.SetGraphicsRootDescriptorTable(1, ssgiSrvVisible.Gpu(gb));
            cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            cl.DrawInstanced(3, 1, 0, 0);
        });

        SsgiResolveAndCombine();   // temporal (motion) + OIDN denoise + composite into the scene
    }

    // Fill the shared SsgiConstants CBV (dials + matrices + pre-exposure + history flag). Used by the SSGI
    // gather AND the RT-GI gather (temporal/combine read it). Returns this frame's rotation index.
    unsafe int FillSsgiConstants(Matrix4x4 view, Matrix4x4 proj) {
        Matrix4x4.Invert(proj, out Matrix4x4 invProj);
        var pf = PostFX;
        int fi = ssgiFrame++ & 1023;
        float preExp = float.TryParse(Environment.GetEnvironmentVariable("BALLISTIC_DX12_EXPOSURE"),
            System.Globalization.CultureInfo.InvariantCulture, out float e) ? e : 1.0e-5f;
        float invPreExp = preExp > 0f ? 1f / preExp : 0f;
        *(SsgiConstants*)ssgiCbMapped = new SsgiConstants {
            Projection = Matrix4x4.Transpose(proj), InvProjection = Matrix4x4.Transpose(invProj),
            ViewMatrix = Matrix4x4.Transpose(view),
            Params0 = new Vector4(pf.SsgiRayLength, pf.SsgiFalloff, pf.SsgiThickness, 0f),
            Params1 = new Vector4(MathF.Max(pf.SsgiBounceBoost, 0f), Math.Clamp(pf.SsgiRayCount, 1, 8), fi, 0f),
            Params2 = new Vector4(1f / ssgiTarget.Width, 1f / ssgiTarget.Height, preExp, invPreExp),
            Combine0 = new Vector4(pf.SsgiIntensity, Math.Clamp(pf.SsgiLook, 0f, 1f),
                                   MathF.Max(pf.SsgiSaturation, 0f), MathF.Max(pf.SsgiOcclusionPower, 0f)),
            Tint = new Vector4(pf.SsgiTint.X, pf.SsgiTint.Y, pf.SsgiTint.Z, 0f),
            // HasHistory=0 in deterministic capture → PSTemporal returns the current GI directly (the SSGI/DDGI
            // temporal EMA is frame-count-dependent → would defeat byte-diffable captures). For DDGI the gather
            // is already noise-free so skipping temporal costs nothing; for SSGI the OIDN spatial denoise still runs.
            Params3 = new Vector4((ssgiHistValid && !DeterministicCapture) ? 1f : 0f, MathF.Max(pf.SsgiMaxHistory, 1f), GiIsolateOn() ? 1f : 0f, 0f),
        };
        return fi;
    }

    // Shared GI resolve tail: motion-buffer temporal accumulation + OIDN denoise + composite into the scene.
    // ssgiTarget holds the raw (noisy) one-bounce GI (from either the SSILVB gather or the RT gather); the
    // SsgiConstants CBV must already be filled. Used by both DrawSsgi and DrawRtGi.
    unsafe void SsgiResolveAndCombine() {
        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        Dx12OffscreenTarget histRead = ssgiHistWriteB ? ssgiHistoryA : ssgiHistoryB;
        Dx12OffscreenTarget histWrite = ssgiHistWriteB ? ssgiHistoryB : ssgiHistoryA;

        // Temporal (half-res) → histWrite. SRVs: currentGI t0, historyGI t1, motion t2 (gbuffer RT4).
        ssgiSrvVisible.Reset();
        ssgiTarget.ColorToShaderResource();
        histRead.ColorToShaderResource();
        int tb = ssgiSrvVisible.AllocateRange(3);
        dev.Device.CopyDescriptorsSimple(1, ssgiSrvVisible.Cpu(tb + 0), ssgiTarget.ColorSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, ssgiSrvVisible.Cpu(tb + 1), histRead.ColorSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, ssgiSrvVisible.Cpu(tb + 2), gbuffer.ColorSrvCpu(Dx12GBuffer.MotionRtIndex), heapType);
        histWrite.RenderColorOnly(cl => {
            cl.SetGraphicsRootSignature(ssgiRootSig); cl.SetPipelineState(ssgiTemporalPso);
            cl.SetDescriptorHeaps(ssgiSrvVisible.Heap);
            cl.SetGraphicsRootConstantBufferView(0, ssgiCb.GPUVirtualAddress);
            cl.SetGraphicsRootDescriptorTable(1, ssgiSrvVisible.Gpu(tb));
            cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            cl.DrawInstanced(3, 1, 0, 0);
        });

        // OIDN spatial denoise (replaces the GL a-trous). Preferred: the ZERO-COPY GPU path — OIDN's HIP
        // device shares a D3D12 buffer, so we copy the GI texture into it on the GPU, denoise in place on the
        // GPU, and copy back, with NO CPU readback (the readback/upload + Map was the cost). Fallback: the CPU
        // readback round-trip (host floats -> OIDN -> upload). BALLISTIC_DX12_SSGI_OIDN=0 skips denoise (A/B);
        // degrades gracefully if the OIDN DLLs aren't present.
        Dx12OffscreenTarget giForCombine = histWrite;
        bool oidnOn = Environment.GetEnvironmentVariable("BALLISTIC_DX12_SSGI_OIDN") != "0";
        if (oidnOn) {
            if (!ssgiOidnEnvRead) {
                ssgiOidnEnvRead = true;
                ssgiOidnForceReadback = Environment.GetEnvironmentVariable("BALLISTIC_DX12_OIDN_READBACK") == "1";
                ssgiOidnTiming = Environment.GetEnvironmentVariable("BALLISTIC_DX12_OIDN_TIMING") == "1";
            }
            if (!ssgiOidnTried) { ssgiOidnTried = true; ssgiOidn = new Dx12OidnDenoiser(dev.AdapterLuidBytes); }
            if (ssgiOidn != null && ssgiOidn.Valid) {
                int w = ssgiTarget.Width, h = ssgiTarget.Height;
                bool usedZeroCopy = false;
                if (ssgiOidnTiming) ssgiOidnSw.Restart();
                // Zero-copy GPU denoise (no CPU round-trip): pack the GI texture into a shared FLOAT buffer on
                // the GPU, OIDN denoises it in place on the GPU, unpack back — float precision, ~12x faster.
                if (ssgiOidn.SharedCapable && !ssgiSharedFailed && !ssgiOidnForceReadback) {
                    if (ssgiOidnGpu == null) ssgiOidnGpu = new Dx12OidnGpuPath(dev);
                    if (ssgiOidnGpu.Ensure(ssgiOidn, ssgiDenoised.RenderTarget, w, h)) {
                        histWrite.ColorToNonPixelShaderResource();   // GI texture as a compute SRV
                        ssgiDenoised.ColorToUnorderedAccess();        // denoise target as a compute UAV
                        ssgiOidnGpu.Pack(histWrite.ColorSrvCpu);      // GPU: texture -> shared float buf
                        if (ssgiOidn.ExecuteShared()) {               // GPU: OIDN denoise in place
                            ssgiOidnGpu.Unpack();                     // GPU: shared float buf -> texture
                            ssgiDenoised.ColorToShaderResource();     // for the combine
                            giForCombine = ssgiDenoised; usedZeroCopy = true;
                        } else { ssgiSharedFailed = true; }           // HIP execute failed → readback from now on
                    } else { ssgiSharedFailed = true; }               // import failed → readback from now on
                }
                // CPU readback fallback (shared path unavailable/failed/forced off this frame).
                if (ReferenceEquals(giForCombine, histWrite)) {
                    int n = w * h * 3;
                    if (ssgiCpuColor == null || ssgiCpuColor.Length != n) { ssgiCpuColor = new float[n]; ssgiCpuOut = new float[n]; }
                    histWrite.ReadColorRgb(ssgiCpuColor);
                    if (ssgiOidn.DenoiseHdr(ssgiCpuColor, null, null, ssgiCpuOut, w, h)) {
                        ssgiDenoised.WriteColorRgb(ssgiCpuOut);   // leaves ssgiDenoised in PixelShaderResource
                        giForCombine = ssgiDenoised;
                    }
                }
                if (ssgiOidnTiming && !ReferenceEquals(giForCombine, histWrite)) {
                    ssgiOidnSw.Stop();
                    ssgiOidnAccumMs += ssgiOidnSw.Elapsed.TotalMilliseconds; ssgiOidnAccumFrames++;
                    if (ssgiOidnAccumFrames % 30 == 0)
                        Console.WriteLine($"[OIDN] denoise avg {ssgiOidnAccumMs / ssgiOidnAccumFrames:F2}ms/frame over {ssgiOidnAccumFrames} ({(usedZeroCopy ? "ZERO-COPY" : "READBACK")})");
                }
            }
        }
        if (ReferenceEquals(giForCombine, histWrite)) histWrite.ColorToShaderResource();   // temporal-only path

        // Combine (full-res) → ssgiScene, reading scene (t0) + (denoised) GI (t1; t2 unused, valid descriptor).
        target.ColorToShaderResource();   // scene must be readable (no-op if the gather already set it)
        int cbi = ssgiSrvVisible.AllocateRange(3);
        dev.Device.CopyDescriptorsSimple(1, ssgiSrvVisible.Cpu(cbi + 0), target.ColorSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, ssgiSrvVisible.Cpu(cbi + 1), giForCombine.ColorSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, ssgiSrvVisible.Cpu(cbi + 2), giForCombine.ColorSrvCpu, heapType);
        ssgiScene.RenderColorOnly(cl => {
            cl.SetGraphicsRootSignature(ssgiRootSig); cl.SetPipelineState(ssgiCombinePso);
            cl.SetDescriptorHeaps(ssgiSrvVisible.Heap);
            cl.SetGraphicsRootConstantBufferView(0, ssgiCb.GPUVirtualAddress);
            cl.SetGraphicsRootDescriptorTable(1, ssgiSrvVisible.Gpu(cbi));
            cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            cl.DrawInstanced(3, 1, 0, 0);
        });
        ssgiScene.ColorToShaderResource();
        target.CopyColorFrom(ssgiScene);   // the GI-enriched scene becomes the new scene color

        ssgiHistWriteB = !ssgiHistWriteB;   // ping-pong; this frame's accumulation is next frame's history
        ssgiHistValid = true;
    }

    // Lazily build the DXR sun-shadow pipeline (scene AS owner, RT PSO, SBT, mask, heap) on first use.
    // Returns false (→ cascade fallback) if DXR is unavailable on this GPU. Uses the source-verified Vortice
    // 3.8.3 DXR API (same as Dx12DxrProbe).
    unsafe bool EnsureRtShadows() {
        if (!dxrChecked) {
            dxrChecked = true;
            dxrAvailable = dev.HasHardwareRayTracing;   // eager device-wide flag (FORCE_NORT-aware)
            if (!dxrAvailable) Console.WriteLine("[RTShadows] DXR unavailable — using cascaded shadows.");
        }
        if (!dxrAvailable) return false;
        if (rtShadowBuilt) return true;
        rtShadowBuilt = true;

        if (device5 == null) device5 = dev.Device.QueryInterface<ID3D12Device5>();
        if (sceneAS == null) sceneAS = new Dx12SceneAS(dev);

        // Global root sig: CBV(b0) + table {SRV t0 TLAS, t1 depth, t2 normal; UAV u0 mask}.
        var cbv = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        var srvRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 3, baseShaderRegister: 0);
        var uavRange = new DescriptorRange1(DescriptorRangeType.UnorderedAccessView, 1, baseShaderRegister: 0);
        var table = new RootParameter1(new RootDescriptorTable1(srvRange, uavRange), ShaderVisibility.All);
        rtShadowRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, new[] { cbv, table })));

        string hlsl = BallisticEngine.DX12.EmbeddedShaderSource.ReadHlsl("DxrShadows.hlsl");
        byte[] dxil = Dx12ShaderCompiler.Compile(DxcShaderStage.Library, hlsl, "", "DxrShadows.hlsl");
        var subs = new[] {
            new StateSubObject(new DxilLibraryDescription(dxil,
                new ExportDescription("RayGen"), new ExportDescription("Miss"), new ExportDescription("ClosestHit"))),
            new StateSubObject(new HitGroupDescription("HitGroup", HitGroupType.Triangles, "", "ClosestHit", "")),
            new StateSubObject(new RaytracingShaderConfig(4, 8)),   // payload = uint Occluded
            new StateSubObject(new RaytracingPipelineConfig(1)),
            new StateSubObject(new GlobalRootSignature(rtShadowRootSig)),
        };
        rtShadowPso = device5.CreateStateObject(new StateObjectDescription(StateObjectType.RaytracingPipeline, subs));

        using ID3D12StateObjectProperties props = rtShadowPso.QueryInterface<ID3D12StateObjectProperties>();
        uint idSize = Vortice.Direct3D12.D3D12.ShaderIdentifierSizeInBytes;
        rtShadowSbt = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer(RtSbtSlot * 3), ResourceStates.GenericRead);
        byte* sp = rtShadowSbt.Map<byte>(0);
        System.Runtime.CompilerServices.Unsafe.CopyBlock(sp + 0 * RtSbtSlot, (void*)props.GetShaderIdentifier("RayGen"), idSize);
        System.Runtime.CompilerServices.Unsafe.CopyBlock(sp + 1 * RtSbtSlot, (void*)props.GetShaderIdentifier("Miss"), idSize);
        System.Runtime.CompilerServices.Unsafe.CopyBlock(sp + 2 * RtSbtSlot, (void*)props.GetShaderIdentifier("HitGroup"), idSize);
        rtShadowSbt.Unmap(0);

        int cbSize = (System.Runtime.InteropServices.Marshal.SizeOf<RtShadowConstants>() + 255) & ~255;
        rtShadowCb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)cbSize), ResourceStates.GenericRead);
        rtShadowCbMapped = rtShadowCb.Map<byte>(0);
        rtShadowHeap = new Dx12DescriptorHeap(dev,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 4, shaderVisible: true);
        AllocRtShadowMask();
        return true;
    }

    void AllocRtShadowMask() {
        rtShadowMask?.Dispose();
        rtShadowMask = new Dx12OffscreenTarget(dev, targetW, targetH, withDepth: false,
            colorFormat: Format.R8_UNorm, colorReadable: true, allowUav: true);
    }

    // RT sun shadows: ensure the scene AS, then DispatchRays one shadow ray per pixel → rtShadowMask. The
    // mask is left in PixelShaderResource for the deferred pass (UseRtShadows). viewProj is the JITTERED
    // matrix the depth was rendered with (matches DrawDeferredLighting's world-pos reconstruction).
    unsafe void DrawRtShadows(Matrix4x4 viewProj, Vector3 lightDir) {
        sceneAS.Ensure(RuntimeSet<IStaticMeshRenderer>.ReadOnlyCollection);
        if (!sceneAS.Valid) return;

        Matrix4x4.Invert(viewProj, out Matrix4x4 invVP);
        Vector3 sun = lightDir.LengthSquared() < 1e-8f ? Vector3.UnitY : Vector3.Normalize(lightDir);
        *(RtShadowConstants*)rtShadowCbMapped = new RtShadowConstants {
            InvViewProj = Matrix4x4.Transpose(invVP), SunDir = sun, NormalBias = 0.05f,
        };

        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        sceneAS.CreateTlasSrv(rtShadowHeap.Cpu(0));
        dev.Device.CopyDescriptorsSimple(1, rtShadowHeap.Cpu(1), gbuffer.DepthSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, rtShadowHeap.Cpu(2), gbuffer.ColorSrvCpu(1), heapType);  // world normal
        dev.Device.CreateUnorderedAccessView(rtShadowMask.RenderTarget, null, new UnorderedAccessViewDescription {
            Format = Format.R8_UNorm, ViewDimension = UnorderedAccessViewDimension.Texture2D,
        }, rtShadowHeap.Cpu(3));

        rtShadowMask.ColorToUnorderedAccess();
        uint idSize = Vortice.Direct3D12.D3D12.ShaderIdentifierSizeInBytes;
        dev.ExecuteSync(cl => {
            cl.SetDescriptorHeaps(rtShadowHeap.Heap);
            cl.SetComputeRootSignature(rtShadowRootSig);
            cl.SetPipelineState1(rtShadowPso);
            cl.SetComputeRootConstantBufferView(0, rtShadowCb.GPUVirtualAddress);
            cl.SetComputeRootDescriptorTable(1, rtShadowHeap.Gpu(0));
            cl.DispatchRays(new DispatchRaysDescription {
                Width = (uint)targetW, Height = (uint)targetH, Depth = 1,
                RayGenerationShaderRecord = new GpuVirtualAddressRange { StartAddress = rtShadowSbt.GPUVirtualAddress, SizeInBytes = idSize },
                MissShaderTable = new GpuVirtualAddressRangeAndStride { StartAddress = rtShadowSbt.GPUVirtualAddress + RtSbtSlot, SizeInBytes = idSize, StrideInBytes = idSize },
                HitGroupTable = new GpuVirtualAddressRangeAndStride { StartAddress = rtShadowSbt.GPUVirtualAddress + 2 * RtSbtSlot, SizeInBytes = idSize, StrideInBytes = idSize },
            });
        });
        rtShadowMask.ColorToShaderResource();
        rtShadowsThisFrame = true;
    }

    // Lazily build the DXR reflection pipeline. Reuses device5 + sceneAS (created by whichever RT effect ran
    // first). Returns false (→ SSR fallback) when DXR is unavailable.
    unsafe bool EnsureRtReflections() {
        if (!dxrChecked) {
            dxrChecked = true;
            dxrAvailable = dev.HasHardwareRayTracing;   // eager device-wide flag (FORCE_NORT-aware)
            if (!dxrAvailable) Console.WriteLine("[RTReflections] DXR unavailable — using SSR.");
        }
        if (!dxrAvailable) return false;
        if (rtReflBuilt) return true;
        rtReflBuilt = true;

        if (device5 == null) device5 = dev.Device.QueryInterface<ID3D12Device5>();
        if (sceneAS == null) sceneAS = new Dx12SceneAS(dev);

        // CBV(b0) + table {SRV t0-t5, UAV u0} + static clamp sampler s0.
        var cbv = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        var srvRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 6, baseShaderRegister: 0);
        var uavRange = new DescriptorRange1(DescriptorRangeType.UnorderedAccessView, 1, baseShaderRegister: 0);
        var table = new RootParameter1(new RootDescriptorTable1(srvRange, uavRange), ShaderVisibility.All);
        var samp = new StaticSamplerDescription(ShaderVisibility.All, 0, 0) {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp, MaxAnisotropy = 1,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        rtReflRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, new[] { cbv, table }, new[] { samp })));

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

        int cbSize = (System.Runtime.InteropServices.Marshal.SizeOf<RtReflConstants>() + 255) & ~255;
        rtReflCb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)cbSize), ResourceStates.GenericRead);
        rtReflCbMapped = rtReflCb.Map<byte>(0);
        rtReflHeap = new Dx12DescriptorHeap(dev,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 7, shaderVisible: true);
        return true;
    }

    // RT reflections: trace a reflection ray per pixel → ssrTarget (reflected color + strength), then reuse
    // the SSR combine (depth-aware upsample + Fresnel lerp into the scene). Replaces the SSR march.
    unsafe void DrawRtReflections(Matrix4x4 view, Matrix4x4 viewProj, Matrix4x4 proj, Vector3 camPos) {
        sceneAS.Ensure(RuntimeSet<IStaticMeshRenderer>.ReadOnlyCollection);
        if (!sceneAS.Valid) { DrawSsr(view, proj); return; }   // no geometry → fall back to SSR

        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        Matrix4x4.Invert(viewProj, out Matrix4x4 invVP);
        *(RtReflConstants*)rtReflCbMapped = new RtReflConstants {
            InvViewProj = Matrix4x4.Transpose(invVP), CameraPos = camPos, Intensity = PostFX.SsrIntensity,
            PrefilterMaxMip = ibl != null ? ibl.PrefilterMipCount - 1 : 0f, NormalBias = 0.05f,
        };

        // The G-buffer is in the combined shader-read state; color (target) bring to SRV for the combine.
        target.ColorToShaderResource();

        sceneAS.CreateTlasSrv(rtReflHeap.Cpu(0));
        dev.Device.CopyDescriptorsSimple(1, rtReflHeap.Cpu(1), gbuffer.DepthSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, rtReflHeap.Cpu(2), gbuffer.ColorSrvCpu(1), heapType);   // world normal
        dev.Device.CopyDescriptorsSimple(1, rtReflHeap.Cpu(3), gbuffer.ColorSrvCpu(2), heapType);   // material
        dev.Device.CopyDescriptorsSimple(1, rtReflHeap.Cpu(4), ibl.IrradianceSrv, heapType);
        dev.Device.CopyDescriptorsSimple(1, rtReflHeap.Cpu(5), ibl.PrefilterSrv, heapType);
        dev.Device.CreateUnorderedAccessView(ssrTarget.RenderTarget, null, new UnorderedAccessViewDescription {
            Format = Dx12OffscreenTarget.HdrFormat, ViewDimension = UnorderedAccessViewDimension.Texture2D,
        }, rtReflHeap.Cpu(6));

        ssrTarget.ColorToUnorderedAccess();
        uint idSize = Vortice.Direct3D12.D3D12.ShaderIdentifierSizeInBytes;
        dev.ExecuteSync(cl => {
            cl.SetDescriptorHeaps(rtReflHeap.Heap);
            cl.SetComputeRootSignature(rtReflRootSig);
            cl.SetPipelineState1(rtReflPso);
            cl.SetComputeRootConstantBufferView(0, rtReflCb.GPUVirtualAddress);
            cl.SetComputeRootDescriptorTable(1, rtReflHeap.Gpu(0));
            cl.DispatchRays(new DispatchRaysDescription {
                Width = (uint)ssrTarget.Width, Height = (uint)ssrTarget.Height, Depth = 1,
                RayGenerationShaderRecord = new GpuVirtualAddressRange { StartAddress = rtReflSbt.GPUVirtualAddress, SizeInBytes = idSize },
                MissShaderTable = new GpuVirtualAddressRangeAndStride { StartAddress = rtReflSbt.GPUVirtualAddress + RtSbtSlot, SizeInBytes = idSize, StrideInBytes = idSize },
                HitGroupTable = new GpuVirtualAddressRangeAndStride { StartAddress = rtReflSbt.GPUVirtualAddress + 2 * RtSbtSlot, SizeInBytes = idSize, StrideInBytes = idSize },
            });
        });
        ssrTarget.ColorToShaderResource();

        // Reuse the SSR combine (depth-aware upsample + Fresnel lerp into the scene color).
        Matrix4x4.Invert(proj, out Matrix4x4 invProj);
        *(SsrConstants*)ssrCbMapped = new SsrConstants {
            Projection = Matrix4x4.Transpose(proj), InvProjection = Matrix4x4.Transpose(invProj),
            ViewMatrix = Matrix4x4.Transpose(view), Intensity = PostFX.SsrIntensity,
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

    // Lazily build the DXR GI pipeline. Reuses device5 + sceneAS. Returns false (→ SSGI fallback) without DXR.
    unsafe bool EnsureRtGi() {
        if (!dxrChecked) {
            dxrChecked = true;
            dxrAvailable = dev.HasHardwareRayTracing;   // eager device-wide flag (FORCE_NORT-aware)
            if (!dxrAvailable) Console.WriteLine("[RTGI] DXR unavailable — using SSGI.");
        }
        if (!dxrAvailable) return false;
        if (rtGiBuilt) return true;
        rtGiBuilt = true;

        if (device5 == null) device5 = dev.Device.QueryInterface<ID3D12Device5>();
        if (sceneAS == null) sceneAS = new Dx12SceneAS(dev);

        // P1 world-radiance hit shading: the closest-hit shader decodes the hit MATERIAL bindlessly (exactly
        // like GBufferBindless), so the root sig must allow ResourceDescriptorHeap[] indexing
        // (SamplerHeapDirectlyIndexed not needed — we use a static sampler). Layout:
        //   CBV b0 RtGiConstants | CBV b1 RtGiSun (sun dir/color + frame) | table{SRV t0-t4, UAV u0} |
        //   SRV t5 GpuMaterials (root) | SRV t6 RtInstance[] (root) + static clamp + wrap samplers.
        var cbv0 = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        var cbv1 = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(1, 0), ShaderVisibility.All);
        var srvRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 5, baseShaderRegister: 0);
        var uavRange = new DescriptorRange1(DescriptorRangeType.UnorderedAccessView, 1, baseShaderRegister: 0);
        var table = new RootParameter1(new RootDescriptorTable1(srvRange, uavRange), ShaderVisibility.All);
        var matSrv = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(5, 0), ShaderVisibility.All);
        var instSrv = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(6, 0), ShaderVisibility.All);
        var lightSrv = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(7, 0), ShaderVisibility.All);  // punctual lights
        var clampSamp = new StaticSamplerDescription(ShaderVisibility.All, 0, 0) {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp, MaxAnisotropy = 1,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        var wrapSamp = new StaticSamplerDescription(ShaderVisibility.All, 1, 0) {   // albedo texture sampling
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Wrap,
            AddressV = TextureAddressMode.Wrap, AddressW = TextureAddressMode.Wrap, MaxAnisotropy = 1,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        rtGiRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(
                RootSignatureFlags.ConstantBufferViewShaderResourceViewUnorderedAccessViewHeapDirectlyIndexed,
                new[] { cbv0, cbv1, table, matSrv, instSrv, lightSrv }, new[] { clampSamp, wrapSamp })));

        string hlsl = BallisticEngine.DX12.EmbeddedShaderSource.ReadHlsl("DxrGi.hlsl");
        byte[] dxil = Dx12ShaderCompiler.Compile(DxcShaderStage.Library, hlsl, "", "DxrGi.hlsl");
        var subs = new[] {
            new StateSubObject(new DxilLibraryDescription(dxil,
                new ExportDescription("RayGen"), new ExportDescription("Miss"), new ExportDescription("ClosestHit"))),
            new StateSubObject(new HitGroupDescription("HitGroup", HitGroupType.Triangles, "", "ClosestHit", "")),
            new StateSubObject(new RaytracingShaderConfig(16, 8)),
            new StateSubObject(new RaytracingPipelineConfig(1)),
            new StateSubObject(new GlobalRootSignature(rtGiRootSig)),
        };
        rtGiPso = device5.CreateStateObject(new StateObjectDescription(StateObjectType.RaytracingPipeline, subs));

        using ID3D12StateObjectProperties props = rtGiPso.QueryInterface<ID3D12StateObjectProperties>();
        uint idSize = Vortice.Direct3D12.D3D12.ShaderIdentifierSizeInBytes;
        rtGiSbt = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer(RtSbtSlot * 3), ResourceStates.GenericRead);
        byte* sp = rtGiSbt.Map<byte>(0);
        System.Runtime.CompilerServices.Unsafe.CopyBlock(sp + 0 * RtSbtSlot, (void*)props.GetShaderIdentifier("RayGen"), idSize);
        System.Runtime.CompilerServices.Unsafe.CopyBlock(sp + 1 * RtSbtSlot, (void*)props.GetShaderIdentifier("Miss"), idSize);
        System.Runtime.CompilerServices.Unsafe.CopyBlock(sp + 2 * RtSbtSlot, (void*)props.GetShaderIdentifier("HitGroup"), idSize);
        rtGiSbt.Unmap(0);

        int cbSize = (System.Runtime.InteropServices.Marshal.SizeOf<RtGiConstants>() + 255) & ~255;
        rtGiCb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)cbSize), ResourceStates.GenericRead);
        rtGiCbMapped = rtGiCb.Map<byte>(0);
        int sunSize = (System.Runtime.InteropServices.Marshal.SizeOf<RtGiSun>() + 255) & ~255;
        rtGiSunCb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)sunSize), ResourceStates.GenericRead);
        rtGiSunCbMapped = rtGiSunCb.Map<byte>(0);
        rtGiHeap = new Dx12DescriptorHeap(dev,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 6, shaderVisible: true);
        rtGeometry = new Dx12RtGeometry(dev);
        return true;
    }

    // The 6 RT-GI table descriptors (t0 TLAS, t1 depth, t2 normal, t3 irradiance, t4 scene color, u0 output)
    // must live in the SAME heap as the bindless material/geometry SRVs (only one CBV/SRV/UAV heap binds at a
    // time, and the hit shader's ResourceDescriptorHeap[] reads the bindless heap). So they get a RESERVED
    // tail range of the BindlessHeap (16384 cap; materials bump from 0 and never reach here), written each
    // frame. The table root param points at this base; bindless reads index the same heap by their slot.
    const int RtGiTableBase = 16384 - 8;

    // DDGI trace pass (P2.1) reserves its OWN 2-slot tail below RtGi's: [0] = TLAS SRV (t0), [1] = irradiance
    // cube SRV (t3), so the trace root table's two ranges map to adjacent bindless-tail descriptors. Below
    // RtGiTableBase (16376) so the two reservations never collide; materials bump from 0 and never reach here.
    const int DdgiTableBase = 16384 - 12;   // slots 16372, 16373, 16374

    // Phase 4 screen-probe TRACE reserves its OWN 3-slot tail below DDGI's: [0] = TLAS (t0), [1] = irr cube
    // (t3), [2] = DDGI irradiance atlas (t4, the far-field handoff). Slots 16368/16369/16370 — below
    // DdgiTableBase (16372) so the three reservations (ScreenProbe < DDGI < RtGi) never collide; materials bump
    // from 0 and never reach here.
    const int ScreenProbeTableBase = 16384 - 16;   // slots 16368, 16369, 16370

    // RT global illumination: trace a cosine-hemisphere ray per pixel → raw one-bounce GI in ssgiTarget,
    // then the SHARED SSGI resolve (temporal + OIDN + combine). viewProj is the JITTERED matrix (matches the
    // depth); proj drives the SSGI dials/combine. lightDir/lightColor = the sun (raw HDR) for the world-space
    // hit re-shading (P1). EnsureMaterialTable + rtGeometry.Ensure MUST run before this (bindless ids).
    unsafe void DrawRtGi(Matrix4x4 view, Matrix4x4 viewProj, Matrix4x4 proj, Vector3 lightDir, Vector3 lightColor, Vector3 camPos) {
        sceneAS.Ensure(RuntimeSet<IStaticMeshRenderer>.ReadOnlyCollection);
        if (!sceneAS.Valid) { DrawSsgi(view, proj); return; }   // no geometry → fall back to SSGI

        // --- DDGI world-probe radiance cache (BALLISTIC_DX12_DDGI=1). P2.0 allocates the probe atlases + snaps
        // the camera-centered grid; P2.1 (below, after the bindless table is written) runs the trace+blend
        // probe-update passes. The atlases are written but not yet read (the gather lands in P2.2), so this is
        // still image-inert — verifying the update pipeline (non-zero, smooth probe tiles + no device-removal). ---
        if (DdgiEnabled) {
            if (ddgi == null) ddgi = new Dx12Ddgi(dev);
            ddgi.Build();
            ddgi.Update(camPos);
            if (!ddgiLogged) {
                ddgiLogged = true;
                Vector3 o = ddgi.Origin;
                Console.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"[DDGI] grid {Dx12Ddgi.ProbesX}x{Dx12Ddgi.ProbesY}x{Dx12Ddgi.ProbesZ}={Dx12Ddgi.ProbeCount} probes; " +
                    $"origin=({o.X:0.#},{o.Y:0.#},{o.Z:0.#}) spacing={ddgi.Spacing.X:0.#}m covers ~{ddgi.Spacing.X*(Dx12Ddgi.ProbesX-1):0}x{ddgi.Spacing.Y*(Dx12Ddgi.ProbesY-1):0}x{ddgi.Spacing.Z*(Dx12Ddgi.ProbesZ-1):0}m; " +
                    $"irrAtlas={Dx12Ddgi.IrradianceAtlasW}x{Dx12Ddgi.IrradianceAtlasH} depthAtlas={Dx12Ddgi.DepthAtlasW}x{Dx12Ddgi.DepthAtlasH}; {Dx12Ddgi.RaysPerProbe} rays/probe"));
                // P2.5 budget readout: round-robin fraction + per-frame probe/ray count + persistent grid VRAM
                // (VRAM-budgeted FIRST per the plan — the GTX-1660's smaller VRAM must never be blown).
                Console.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"[DDGI] round-robin 1/{ddgi.CurrentUpdateFraction}: {ddgi.ProbesPerFrame} probes/frame x {Dx12Ddgi.RaysPerProbe} = {ddgi.ProbesPerFrame*Dx12Ddgi.RaysPerProbe} rays/frame; " +
                    $"grid VRAM {ddgi.GridVramBytes/(1024.0*1024.0):0.0} MB"));
            }
        }
        // The bindless material table (byte-identical to the raster G-buffer) feeds the world-space hit
        // shading. The geometry pass builds it only when gpuDrivenOn — ensure it here too (stamp-cached no-op
        // if already built) so RT-GI works with the CPU geometry path. Then the per-instance geometry SRVs.
        gpuDriven.EnsureMaterialTable(wholeMeshRenderers);
        rtGeometry.Ensure(RuntimeSet<IStaticMeshRenderer>.ReadOnlyCollection, gpuDriven);

        int fi = FillSsgiConstants(view, proj);   // dials + matrices for the shared temporal/combine
        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        Matrix4x4.Invert(viewProj, out Matrix4x4 invVP);
        *(RtGiConstants*)rtGiCbMapped = new RtGiConstants {
            InvViewProj = Matrix4x4.Transpose(invVP), ViewProj = Matrix4x4.Transpose(viewProj),
            Params = new Vector4(SsgiPreExposure(), MathF.Max(PostFX.SsgiRayLength, 0.1f), 0f, fi),
        };
        Vector3 sunDir = lightDir.LengthSquared() < 1e-8f ? Vector3.UnitY : Vector3.Normalize(lightDir);
        *(RtGiSun*)rtGiSunCbMapped = new RtGiSun {
            SunDir = sunDir, NormalBias = 0.03f, SunColor = lightColor, LightCount = clusteredLights.LightCount,
        };

        // --- P2.1 DDGI probe update (trace+blend). Reuses the SAME bindless heap + root-SRV addresses + the
        // RtGiSun CBV as the RT-GI pass, so the probe hit-shading is byte-identical to DxrGi. Writes the trace
        // table's 2 descriptors (TLAS@DdgiTableBase, irr cube@+1) into the bindless tail, then dispatches in
        // its own ExecuteSync (each DX12 pass = its own submit; lets GI:DDGI be timed separately). Atlases are
        // written but not yet read (gather = P2.2) → image still inert; this validates the update pipeline. ---
        if (DdgiEnabled && ddgi != null && ddgi.Allocated) {
            Dx12DescriptorHeap bh = Dx12Backend.BindlessHeap;
            var ddgiHeapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
            sceneAS.CreateTlasSrv(bh.Cpu(DdgiTableBase + 0));                                              // t0 TLAS
            dev.Device.CopyDescriptorsSimple(1, bh.Cpu(DdgiTableBase + 1), ibl.IrradianceSrv, ddgiHeapType); // t3 irr cube
            // t4 = LAST frame's irradiance atlas (P2.3 multi-bounce feedback). The trace samples it as the hit
            // ambient; DispatchDdgi transitions it UAV→SRV→UAV around the trace within its command list.
            dev.Device.CreateShaderResourceView(ddgi.IrradianceTex, new ShaderResourceViewDescription {
                Format = Format.R16G16B16A16_Float,
                ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
                Shader4ComponentMapping = ShaderComponentMapping.Default,
                Texture2D = new Texture2DShaderResourceView { MipLevels = 1 },
            }, bh.Cpu(DdgiTableBase + 2));
            // One trace+blend+classify cycle (its own submit). `full` = warm-up / round-robin off (every probe).
            // Reuses the descriptors written above (TLAS/cube/prev-irr stay valid across the warm-up replays).
            void RunDdgiUpdate(bool full) => dev.ExecuteSync(cl => {
                cl.SetDescriptorHeaps(bh.Heap);
                ddgi.DispatchDdgi(cl, bh, bh.Gpu(DdgiTableBase),
                    rtGiSunCb.GPUVirtualAddress, gpuDriven.MaterialsGpuAddress,
                    rtGeometry.InstancesGpuAddress, clusteredLights.LightBufGpuAddress,
                    hysteresis: 0.97f, intensity: MathF.Max(PostFX.SsgiIntensity, 0f),
                    feedback: true,         // P2.3 multi-bounce
                    fullUpdate: full);
            });

            // --- P2.5 WARM-UP (capture-path determinism): on the FIRST DDGI frame, converge the field by
            // replaying the update many times (each its own submit — never one giant command list) FULL-grid, so
            // a paused screenshot is the STEADY STATE, byte-deterministic + independent of SCREENSHOT_FRAME. No-op
            // in play (TryWarmUp returns false unless BALLISTIC_SCREENSHOT is set / _WARMUP overrides). ---
            ddgi.TryWarmUp(() => RunDdgiUpdate(full: true));

            // Own stopwatch (NOT TimePass — nesting it inside the outer GI:RT TimePass would clobber the shared
            // passSw and corrupt GI:RT's reading). Each DX12 pass is its own ExecuteSync = its GPU wall-time.
            // FREEZE on the PAUSED capture path: once warmed up, skip the per-frame round-robin update so every
            // captured frame reads the SAME converged atlas (one more update would make the image frame-dependent).
            // Gated on the paused capture (static camera → the frozen atlas is sampled at the correct, unchanged
            // probe positions). A MOVING capture (or play) keeps updating live — freezing a moving camera would
            // sample a stale-pose atlas as the grid re-snaps (audit Finding C).
            // WarmupEnabled is now itself gated on the paused capture (Dx12Ddgi.WarmupIterations), so freeze ==
            // WarmupEnabled: warm-up converged the field, then we hold it. Play/moving captures keep updating live.
            if (!ddgi.WarmupEnabled) {
                var ddgiSw = GiTimingEnabled ? System.Diagnostics.Stopwatch.StartNew() : null;
                RunDdgiUpdate(full: false);   // the per-frame round-robin update (1/N probes)
                if (ddgiSw != null) { ddgiSw.Stop(); RenderStats.Scene.GpuPasses.Add(("GI:DDGI", ddgiSw.Elapsed.TotalMilliseconds)); }
            }
            if (!ddgiDebugDumped && Environment.GetEnvironmentVariable("BALLISTIC_DX12_DDGI_DEBUG") == "1") {
                ddgiDebugDumped = true; ddgi.DumpIrradianceStats();
            }

            // The compute gather/place reads depth as SRV t0 → NON_PIXEL state (shared by both GI sources below).
            gbuffer.DepthToNonPixelShaderResource();

            // --- PHASE 4: screen-space radiance probes (DEFAULT when DDGI is on; BALLISTIC_DX12_SCREENPROBE=0
            // opts out to the per-pixel DDGI gather below). Runs a DOWNSAMPLED screen-probe final gather: Place
            // (one probe per 16x16 tile, snapped to the G-buffer surface) → Trace (64 short hemisphere rays,
            // miss → DDGI field) → Blend (rays → octahedral radiance tile) → Integrate (bilateral 2x2-probe
            // upsample → ssgiTarget). The screen probes are the near/mid field; the DDGI cache we just updated is
            // the far field (the trace's ray-miss handoff) — the published Lumen screen-trace → world-cache
            // hierarchy. Same ssgiTarget contract → the shared resolve composites it (and GI-isolate shows it).
            // The DDGI gather below is the SCREENPROBE=0 fallback (reproduces the pre-flip image byte-for-byte). ---
            if (ScreenProbeEnabled) {
                DrawScreenProbeGather(invVP);
                SsgiResolveAndCombine();
                return;
            }

            // --- P2.2 DDGI GATHER: per-pixel sample the probe field (8-probe trilinear + Chebyshev leak test)
            // → albedo*E pre-exposed into ssgiTarget, REPLACING the RT per-pixel ray-march as the GI source
            // (the plan: DDGI is the world cache; SSGI stays the near-field companion). Then the shared
            // SsgiResolveAndCombine composites it (and GI-isolate shows it). G-buffer depth/normal/albedo are
            // in the combined shader-read state from the deferred pass. ---
            ssgiTarget.ColorToUnorderedAccess();
            var gatherSw = GiTimingEnabled ? System.Diagnostics.Stopwatch.StartNew() : null;
            dev.ExecuteSync(cl => {
                ddgi.DispatchGather(cl, gbuffer.DepthSrvCpu, gbuffer.ColorSrvCpu(1), gbuffer.ColorSrvCpu(0),
                    ssgiTarget.RenderTarget, ssgiTarget.Width, ssgiTarget.Height,
                    Matrix4x4.Transpose(invVP), SsgiPreExposure());
            });
            if (gatherSw != null) { gatherSw.Stop(); RenderStats.Scene.GpuPasses.Add(("GI:DDGIGather", gatherSw.Elapsed.TotalMilliseconds)); }
            ssgiTarget.ColorToShaderResource();
            SsgiResolveAndCombine();   // shared: motion temporal + OIDN + composite
            return;
        }

        // G-buffer is in the combined shader-read state (RT compute-stage can read depth+normal); the lit
        // scene color is the bounce source (project the hit back to it), so bring it to SRV. The 6 table
        // descriptors go into the BindlessHeap's reserved tail so the bindless hit shading shares the heap.
        target.ColorToShaderResource();
        Dx12DescriptorHeap bindless = Dx12Backend.BindlessHeap;
        sceneAS.CreateTlasSrv(bindless.Cpu(RtGiTableBase + 0));
        dev.Device.CopyDescriptorsSimple(1, bindless.Cpu(RtGiTableBase + 1), gbuffer.DepthSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, bindless.Cpu(RtGiTableBase + 2), gbuffer.ColorSrvCpu(1), heapType);   // world normal
        dev.Device.CopyDescriptorsSimple(1, bindless.Cpu(RtGiTableBase + 3), ibl.IrradianceSrv, heapType);        // off-screen fallback
        dev.Device.CopyDescriptorsSimple(1, bindless.Cpu(RtGiTableBase + 4), target.ColorSrvCpu, heapType);       // lit scene color
        dev.Device.CreateUnorderedAccessView(ssgiTarget.RenderTarget, null, new UnorderedAccessViewDescription {
            Format = Dx12OffscreenTarget.HdrFormat, ViewDimension = UnorderedAccessViewDimension.Texture2D,
        }, bindless.Cpu(RtGiTableBase + 5));

        ssgiTarget.ColorToUnorderedAccess();
        uint idSize = Vortice.Direct3D12.D3D12.ShaderIdentifierSizeInBytes;
        dev.ExecuteSync(cl => {
            cl.SetDescriptorHeaps(bindless.Heap);   // bindless heap = the bound CBV/SRV/UAV heap (table + ResourceDescriptorHeap[])
            cl.SetComputeRootSignature(rtGiRootSig);
            cl.SetPipelineState1(rtGiPso);
            cl.SetComputeRootConstantBufferView(0, rtGiCb.GPUVirtualAddress);
            cl.SetComputeRootConstantBufferView(1, rtGiSunCb.GPUVirtualAddress);
            cl.SetComputeRootDescriptorTable(2, bindless.Gpu(RtGiTableBase));
            cl.SetComputeRootShaderResourceView(3, gpuDriven.MaterialsGpuAddress);    // t5 GpuMaterials
            cl.SetComputeRootShaderResourceView(4, rtGeometry.InstancesGpuAddress);   // t6 RtInstance[]
            cl.SetComputeRootShaderResourceView(5, clusteredLights.LightBufGpuAddress);  // t7 punctual lights
            cl.DispatchRays(new DispatchRaysDescription {
                Width = (uint)ssgiTarget.Width, Height = (uint)ssgiTarget.Height, Depth = 1,
                RayGenerationShaderRecord = new GpuVirtualAddressRange { StartAddress = rtGiSbt.GPUVirtualAddress, SizeInBytes = idSize },
                MissShaderTable = new GpuVirtualAddressRangeAndStride { StartAddress = rtGiSbt.GPUVirtualAddress + RtSbtSlot, SizeInBytes = idSize, StrideInBytes = idSize },
                HitGroupTable = new GpuVirtualAddressRangeAndStride { StartAddress = rtGiSbt.GPUVirtualAddress + 2 * RtSbtSlot, SizeInBytes = idSize, StrideInBytes = idSize },
            });
        });
        ssgiTarget.ColorToShaderResource();

        SsgiResolveAndCombine();   // shared: motion temporal + OIDN + composite
    }

    // PHASE 4 (P4.0): the screen-space radiance probe final gather. Runs the 4 sub-passes — Place, Trace,
    // Blend, Integrate — leaving the (raw, pre-exposed) GI in ssgiTarget for the shared SsgiResolveAndCombine.
    // Called from DrawRtGi only when ScreenProbeEnabled AND DDGI is on/allocated (the trace hands off to the
    // DDGI world cache for its far field). The DDGI field was already updated this frame above, so the
    // screen-probe rays sample a current cache. G-buffer depth is in NonPixelShaderResource on entry; albedo +
    // normal are in the combined shader-read state from the deferred pass. invVP is the UN-transposed inverse
    // view-projection (we transpose for the shaders, matching the DDGI gather convention).
    unsafe void DrawScreenProbeGather(Matrix4x4 invVP) {
        if (screenProbe == null) screenProbe = new Dx12ScreenProbe(dev);
        screenProbe.EnsureAllocated(ssgiTarget.Width, ssgiTarget.Height);
        screenProbe.Build();

        // Per-frame constants: the screen-probe grid + the DDGI grid description the trace samples on miss.
        var ddgiGrid = ddgi.GridConstants();
        screenProbe.PrepareConstants(Matrix4x4.Transpose(invVP),
            maxRayDist: MathF.Max(PostFX.SsgiRayLength, 3f),   // SHORT near/mid-field ray (DDGI handles far)
            preExposure: SsgiPreExposure(), intensity: MathF.Max(PostFX.SsgiIntensity, 0f),
            deterministic: DeterministicCapture, ddgiGrid);

        if (!screenProbeLogged) {
            screenProbeLogged = true;
            Console.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"[SCREENPROBE] grid {screenProbe.ProbesX}x{screenProbe.ProbesY}={screenProbe.ProbeCount} probes " +
                $"(1 per {Dx12ScreenProbe.Downsample}x{Dx12ScreenProbe.Downsample} px); {Dx12ScreenProbe.OctTexels}x{Dx12ScreenProbe.OctTexels} octahedral, " +
                $"{Dx12ScreenProbe.RaysPerProbe} rays/probe = {screenProbe.ProbeCount * Dx12ScreenProbe.RaysPerProbe} rays/frame; " +
                $"VRAM {screenProbe.GridVramBytes / (1024.0 * 1024.0):0.0} MB"));
        }

        var sw = GiTimingEnabled ? System.Diagnostics.Stopwatch.StartNew() : null;

        // PLACE: probePos/probeNormal from the G-buffer (depth NonPixelSRV, normal = G-buffer RT1).
        screenProbe.DispatchPlace(gbuffer.DepthSrvCpu, gbuffer.ColorSrvCpu(1));

        // TRACE: write the 3-descriptor bindless tail block ([0] TLAS, [1] irr cube, [2] DDGI atlas), then
        // dispatch with the shared bindless geo/material addresses (same as the DDGI/RT-GI pass).
        Dx12DescriptorHeap bh = Dx12Backend.BindlessHeap;
        var ddgiHeapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        sceneAS.CreateTlasSrv(bh.Cpu(ScreenProbeTableBase + 0));                                               // t0 TLAS
        dev.Device.CopyDescriptorsSimple(1, bh.Cpu(ScreenProbeTableBase + 1), ibl.IrradianceSrv, ddgiHeapType); // t3 irr cube
        dev.Device.CreateShaderResourceView(ddgi.IrradianceTex, new ShaderResourceViewDescription {            // t4 DDGI atlas
            Format = Format.R16G16B16A16_Float, ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Texture2D = new Texture2DShaderResourceView { MipLevels = 1 },
        }, bh.Cpu(ScreenProbeTableBase + 2));
        // The DDGI atlas is in UnorderedAccess (left so by DispatchDdgi) → the trace reads it as a NonPixelSRV.
        dev.ExecuteSync(cl => cl.ResourceBarrierTransition(ddgi.IrradianceTex,
            ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource));
        screenProbe.DispatchTrace(bh, bh.Gpu(ScreenProbeTableBase),
            rtGiSunCb.GPUVirtualAddress, gpuDriven.MaterialsGpuAddress, rtGeometry.InstancesGpuAddress,
            clusteredLights.LightBufGpuAddress, ddgi.ProbeStateGpuAddress);
        // DDGI atlas back to UnorderedAccess for next frame's DDGI blend.
        dev.ExecuteSync(cl => cl.ResourceBarrierTransition(ddgi.IrradianceTex,
            ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess));

        // BLEND: rays → octahedral radiance tile (+ border).
        screenProbe.DispatchBlend();

        // INTEGRATE: full-res nearest-probe upsample → ssgiTarget (pre-exposed albedo*E).
        ssgiTarget.ColorToUnorderedAccess();
        screenProbe.DispatchIntegrate(gbuffer.DepthSrvCpu, gbuffer.ColorSrvCpu(1), gbuffer.ColorSrvCpu(0),
            ssgiTarget.RenderTarget, ssgiTarget.Width, ssgiTarget.Height);
        ssgiTarget.ColorToShaderResource();

        if (sw != null) { sw.Stop(); RenderStats.Scene.GpuPasses.Add(("GI:ScreenProbe", sw.Elapsed.TotalMilliseconds)); }
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

    // Bloom: bright-pass the HDR `src` (already in SRV state) → bloomA; blur H (bloomA→bloomB);
    // blur V (bloomB→bloomA). Result lands in bloomA at half-res for the composite to add. `src` is the
    // scene HDR (native) or the FSR-upscaled HDR (so bloom is at output res when upscaling).
    unsafe void DrawBloom(Dx12OffscreenTarget src) {
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

        // Bright-pass reads the HDR scene (already in SRV state from DrawComposite).
        Pass(bloomBrightPso, src, bloomA, 0, new Vector2(texW, texH), 1.0f);
        Pass(bloomBlurHPso, bloomA, bloomB, 1, new Vector2(texW, texH), 0f);
        Pass(bloomBlurVPso, bloomB, bloomA, 2, new Vector2(texW, texH), 0f);
        bloomA.ColorToShaderResource();   // ready for the composite to sample
    }

    // Tonemap the HDR `hdr` source (the native scene target, or the FSR-upscaled output) into the LDR
    // output at OUTPUT resolution. Auto-exposure drives the exposure; bloom (if on) runs first (inside
    // this), reading the same HDR source. Sources at internal/half res are sampled by UV (resolution-safe).
    unsafe void DrawComposite(bool ssaoOn, Dx12OffscreenTarget hdr) {
        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;

        // Manual exposure override (BALLISTIC_DX12_EXPOSURE) disables auto-exposure; else auto-meter.
        bool manual = float.TryParse(Environment.GetEnvironmentVariable("BALLISTIC_DX12_EXPOSURE"),
            System.Globalization.CultureInfo.InvariantCulture, out float manualExp);

        hdr.ColorToShaderResource();   // HDR source → SRV (for both the lum pass and composite)

        if (!manual) {
            // Auto-exposure metering: reduce the HDR source to a 1×1 geometric-mean luminance.
            dev.Device.CopyDescriptorsSimple(1, lumSrvVisible.Cpu(0), hdr.ColorSrvCpu, heapType);
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
        if (bloomOn) DrawBloom(hdr);

        // ExposureKey: middle-grey target ~0.18 (the HDR scene is physical radiance; auto-meter rescales).
        *(CompositeConstants*)compositeCbMapped = new CompositeConstants {
            Exposure = manual ? manualExp : 1.0e-5f,
            BloomIntensity = bloomOn ? 0.6f : 0f,
            AutoExposure = manual ? 0f : 1f,
            ExposureKey = 0.18f,
            UseAo = ssaoOn ? 1f : 0f,
        };

        dev.Device.CopyDescriptorsSimple(1, compositeSrvVisible.Cpu(0), hdr.ColorSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, compositeSrvVisible.Cpu(1),
            bloomOn ? bloomA.ColorSrvCpu : hdr.ColorSrvCpu, heapType);   // bloom slot
        dev.Device.CopyDescriptorsSimple(1, compositeSrvVisible.Cpu(2),
            manual ? hdr.ColorSrvCpu : lumTarget.ColorSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, compositeSrvVisible.Cpu(3),
            ssaoOn ? ssaoA.ColorSrvCpu : hdr.ColorSrvCpu, heapType);     // AO slot

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
        // Restore the INTERNAL scene target to RenderTarget for next frame's geometry/deferred pass (FSR
        // left it in PixelShaderResource; in the native path hdr == target). fsrOutput stays in shader-read
        // — RunFsr transitions it to UAV next frame from any state.
        target.ColorToRenderTarget();
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
        Vector3 euler = Skybox.Active.RotationEuler;
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

    // Hash of all active shadow-caster transforms — changes when geometry moves/appears (so cascade caching
    // re-renders). Camera/sun motion is caught separately by the cascade fit matrices. No reflection (typed).
    int ComputeShadowCasterStamp() {
        var h = new System.HashCode();
        foreach (IStaticMeshRenderer r in RuntimeSet<IStaticMeshRenderer>.ReadOnlyCollection) {
            if (r is null || !r.IsActive || !r.IsRenderable) continue;
            GLMatrix4 m = r.Transform.WorldMatrix;
            h.Add(m.M11); h.Add(m.M12); h.Add(m.M13); h.Add(m.M14);
            h.Add(m.M21); h.Add(m.M22); h.Add(m.M23); h.Add(m.M24);
            h.Add(m.M31); h.Add(m.M32); h.Add(m.M33); h.Add(m.M34);
            h.Add(m.M41); h.Add(m.M42); h.Add(m.M43); h.Add(m.M44);
            h.Add(r.SubMeshIndex);
        }
        return h.ToHashCode();
    }

    // Render the sun cascades' depth (one depth-array layer per cascade) before the opaque pass. Uses the
    // dedicated upload command list (separate from the render list), then leaves the array as an SRV the
    // opaque shader samples. Cascade caching skips the pass when nothing the shadows depend on changed.
    unsafe void RenderShadows(Matrix4x4 camView, Matrix4x4 camProj, LightUniforms light) {
        shadowsThisFrame = false;
        if (DirectionalLight.Instance is null) return;   // no sun → no shadows

        Vector3 sunTravel = -ToNumerics(light.Direction);   // light.Direction is TOWARD the light
        if (sunTravel.LengthSquared() < 1e-8f) return;
        float shadowDistance = DirectionalLight.Instance.ShadowDistance;
        Dx12ShadowMath.ComputeCascades(camView, camProj, sunTravel, shadowDistance, ShadowMapSize,
            cascadeMatrices, cascadeDepthRanges);

        // Cascade caching: if every cascade's fit matrix AND the caster geometry are unchanged since the last
        // render, the shadow-map layers still hold valid depth — skip the whole pass (byte-identical). The
        // big static-camera win. Camera/sun motion changes the fit matrices; geometry motion changes the stamp.
        int casterStamp = ComputeShadowCasterStamp();
        bool cascadesUnchanged = shadowMapEverRendered && casterStamp == lastCasterStamp;
        for (int c = 0; cascadesUnchanged && c < CascadeCount; c++)
            cascadesUnchanged &= cascadeMatrices[c].Equals(lastCascadeMatrices[c]);
        if (shadowCacheOn && cascadesUnchanged) {
            shadowsThisFrame = true;   // the cached shadow map is still valid
            if (Environment.GetEnvironmentVariable("BALLISTIC_DX12_SHADOW_CACHE_DEBUG") == "1")
                Console.WriteLine("[ShadowCache] cascades unchanged — skipped re-render.");
            return;
        }
        lastCasterStamp = casterStamp;
        for (int c = 0; c < CascadeCount; c++) lastCascadeMatrices[c] = cascadeMatrices[c];
        shadowMapEverRendered = true;

        // Fill per (cascade, submesh) LightMvp constants, mirroring the opaque iteration.
        int slot = 0;
        var fills = new System.Collections.Generic.List<(int cascade, Dx12Buffer<GLVector3> vb, Dx12IndexBuffer ib, int start, int count, int cbSlot)>();
        for (int c = 0; c < CascadeCount; c++) {
            // Cull shadow casters against THIS cascade's light frustum (a caster off-screen for the camera
            // but inside this cascade still casts — that's why we cull per the LIGHT frustum, not the camera).
            ExtractFrustumPlanes(cascadeMatrices[c]);
            foreach (IStaticMeshRenderer r in RuntimeSet<IStaticMeshRenderer>.ReadOnlyCollection) {
                if (r is null || !r.IsActive || !r.IsRenderable) continue;
                // Whole-mesh casters are GPU-driven (per-cascade compute cull + ExecuteIndirect) — skip here.
                if (gpuDrivenOn && r.SubMeshIndex < 0) continue;
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
                    mesh.GetSubMeshBounds(s, out GLVector3 lmin, out GLVector3 lmax);
                    if (!AabbInFrustum(lmin, lmax, model)) continue;   // outside this cascade
                    if (slot >= shadowCbSlotCount) break;
                    *(ShadowConstants*)(shadowCbMapped + (long)slot * shadowCbSlotSize) =
                        new ShadowConstants { LightMvp = Matrix4x4.Transpose(lightMvp) };
                    fills.Add((c, vb, ib, sub.IndexStart, sub.IndexCount, slot));
                    slot++;
                }
            }
        }
        bool gpuShadows = gpuDrivenOn && wholeMeshRenderers.Count > 0;
        if (fills.Count == 0 && !gpuShadows) return;

        dev.ExecuteUpload(cl => {
            // GPU-driven whole-mesh casters: per-cascade compute cull (must precede the depth draws).
            if (gpuShadows) gpuDriven.BuildShadowCull(cl, wholeMeshRenderers, cascadeMatrices);
            shadowMap.ToDepthWrite(cl);
            for (int c = 0; c < CascadeCount; c++) {
                shadowMap.RenderCascade(cl, c, cc => {
                    // CPU per-submesh casters for this cascade.
                    cc.SetGraphicsRootSignature(shadowRootSig);
                    cc.SetPipelineState(shadowPso);
                    cc.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                    foreach (var f in fills) {
                        if (f.cascade != c) continue;
                        cc.SetGraphicsRootConstantBufferView(0,
                            shadowCb.GPUVirtualAddress + (ulong)((long)f.cbSlot * shadowCbSlotSize));
                        cc.IASetVertexBuffers(0, new VertexBufferView(f.vb.GpuAddress, (uint)f.vb.ByteSize, (uint)f.vb.Stride));
                        cc.IASetIndexBuffer(new IndexBufferView(f.ib.GpuAddress, (uint)f.ib.ByteSize, Format.R32_UInt));
                        cc.DrawIndexedInstanced((uint)f.count, 1, (uint)f.start, 0, 0);
                    }
                    // GPU-driven whole-mesh casters for this cascade (ExecuteIndirect into the same layer).
                    if (gpuShadows) gpuDriven.DrawShadowCascade(cc, c);
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

        float cloudTime = Dx12SkyCloudParams.CloudTime(sky);
        var sc = new ProcSkyConstants {
            ViewProjNoTranslate = Matrix4x4.Transpose(viewNoT * proj),
            SunDirection = sunDir, SunAngularRadius = MathF.Max(sunAngularRadius, 1e-4f),
            SunRadiance = ToNumerics(light.Color), SunDiskIntensity = MathF.Max(sky.SunDiskIntensity, 0f),
            GroundAlbedo = ToNumerics(sky.GroundColor), AirDensity = MathF.Max(sky.AirDensity, 0f),
            Haze = MathF.Max(sky.Haze, 0f), HazeAnisotropy = Math.Clamp(sky.HazeAnisotropy, 0f, 0.99f),
            OzoneDensity = MathF.Max(sky.OzoneDensity, 0f), MultiScatter = MathF.Max(sky.MultipleScattering, 1f),
            Exposure = MathF.Max(sky.Exposure, 0f),
            // Volumetric clouds + cirrus + stars (clamps mirror GLProceduralSkyPass).
            CloudsEnabled = sky.CloudsEnabled ? 1f : 0f, CloudCoverage = Math.Clamp(sky.CloudCoverage, 0f, 1f),
            CloudDensity = MathF.Max(sky.CloudDensity, 0f), CloudAltitude = Math.Clamp(sky.CloudAltitude, 600f, 20000f),
            CloudThickness = Math.Clamp(sky.CloudThickness, 100f, 20000f), CloudScale = MathF.Max(sky.CloudScale, 0.05f),
            CloudDetail = Math.Clamp(sky.CloudDetail, 0f, 1f), CloudAmbient = MathF.Max(sky.CloudAmbient, 0f),
            CloudWindOffset = Dx12SkyCloudParams.WindOffset(sky, cloudTime),
            CloudWindAngle = Dx12SkyCloudParams.WindRadians(sky),
            CirrusCoverage = Math.Clamp(sky.CirrusCoverage, 0f, 1f), StarIntensity = MathF.Max(sky.StarIntensity, 0f),
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
    void BindSrv(int visibleSlot, Texture2D tex, TextureType type, Dx12Texture2D explicitFallback)
        => BindSrvInto(srvVisible, visibleSlot, tex, type, explicitFallback);

    void BindSrvInto(Dx12DescriptorHeap heap, int slot, Texture2D tex, TextureType type, Dx12Texture2D explicitFallback) {
        var dx = (tex as Dx12Texture2D)
                 ?? explicitFallback
                 ?? (DefaultTextures.Neutral(type) as Dx12Texture2D);
        dev.Device.CopyDescriptorsSimple(1, heap.Cpu(slot), dx.SrvCpu,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
    }

    public override void PostRenderCleanUp() {
        foreach (IStaticMeshRenderer r in RuntimeSet<IStaticMeshRenderer>.ReadOnlyCollection)
            if (r != null) r.RenderedThisFrame = false;
    }

    // Readback comes from the LDR composite (R8) — the HDR scene target isn't a valid BMP source.
    public void SaveFrame(string path) => ldr?.SaveBmp(path);

    // Raw G-buffer dump for the agent's "raw perception" (`bal gbuffer`): writes depth (linear-ish window
    // depth, R32F), world normal (RGBA16F, packed N*0.5+0.5), and albedo (RGBA8 sRGB) as raw little-endian
    // .bin files + a manifest.json describing dims/format/encoding so the agent can decode them. Reads the
    // G-buffer AFTER a frame (resources are in ShaderRead state). Returns the manifest object (for the CLI).
    public object DumpGBuffer(string dir) {
        if (gbuffer == null) return new { ok = false, error = "no g-buffer (renderer not initialized)" };
        System.IO.Directory.CreateDirectory(dir);
        int w = gbuffer.Width, h = gbuffer.Height;

        byte[] depth  = gbuffer.ReadbackRaw(-1, out int depthBpp);                 // R32_Float, 4 B/px
        byte[] normal = gbuffer.ReadbackRaw(1, out int normalBpp);                 // RGBA16F, 8 B/px (packed)
        byte[] albedo = gbuffer.ReadbackRaw(0, out int albedoBpp);                 // RGBA8 sRGB, 4 B/px

        System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, "depth.bin"), depth);
        System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, "normal.bin"), normal);
        System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, "albedo.bin"), albedo);

        return new {
            ok = true, width = w, height = h,
            buffers = new object[] {
                new { name = "depth",  file = "depth.bin",  format = "R32_Float", bytesPerPixel = depthBpp,
                      encoding = "window depth [0,1]; world pos = unproject(uv, depth) via InvViewProj" },
                new { name = "normal", file = "normal.bin", format = "R16G16B16A16_Float", bytesPerPixel = normalBpp,
                      encoding = "world normal PACKED as N*0.5+0.5 in RGB (half floats); unpack N = rgb*2-1" },
                new { name = "albedo", file = "albedo.bin", format = "R8G8B8A8_UNorm_sRGB", bytesPerPixel = albedoBpp,
                      encoding = "albedo.rgb sRGB; a = specular F0" },
            },
        };
    }
    // Output (display/readback) resolution — equals the internal render res unless FSR is upscaling.
    public int Width => outputW;
    public int Height => outputH;

    // Internal pipeline steps — no engine/editor caller (BeginRender draws opaques itself).
    public override void RenderOpaque(IReadOnlyCollection<IStaticMeshRenderer> renderTargets,
        RendererArgs args, bool isShadowPass) { }
    public override void RenderSkybox(IReadOnlyCollection<ISkyboxDrawable> renderTargets, RendererArgs args) { }
    public override void RenderInstancing(BatchGroup<IStaticMeshRenderer> batchGroup, RendererArgs args) { }
    public override void RenderInstancing(Mesh mesh, Material material, GLMatrix4[] transforms, RendererArgs args) { }

    // --- Frustum culling (CPU, per submesh) ------------------------------------------------------------
    // 6 frustum planes (xyz = normal, w = d) extracted from a row-major view*proj (Gribb-Hartmann). Tested
    // with the positive-vertex / 8-corner-AABB rule. Mirrors the GL per-submesh cull so the geometry pass
    // and shadow pass only draw what the (camera or light) frustum can see.
    readonly Vector4[] frustumPlanes = new Vector4[6];

    void ExtractFrustumPlanes(Matrix4x4 m) {
        // Row-major System.Numerics: rows are (M11..M14), (M21..M24), ... Gribb-Hartmann combines rows.
        // left = row4 + row1, right = row4 - row1, bottom = row4 + row2, top = row4 - row2,
        // near = row3 (DX z[0,1]: near = row3, not row4+row3), far = row4 - row3.
        Vector4 r1 = new(m.M11, m.M21, m.M31, m.M41);
        Vector4 r2 = new(m.M12, m.M22, m.M32, m.M42);
        Vector4 r3 = new(m.M13, m.M23, m.M33, m.M43);
        Vector4 r4 = new(m.M14, m.M24, m.M34, m.M44);
        frustumPlanes[0] = r4 + r1;   // left
        frustumPlanes[1] = r4 - r1;   // right
        frustumPlanes[2] = r4 + r2;   // bottom
        frustumPlanes[3] = r4 - r2;   // top
        frustumPlanes[4] = r3;        // near (DX: z >= 0)
        frustumPlanes[5] = r4 - r3;   // far
        for (int i = 0; i < 6; i++) {
            Vector3 n = new(frustumPlanes[i].X, frustumPlanes[i].Y, frustumPlanes[i].Z);
            float len = n.Length();
            if (len > 1e-6f) frustumPlanes[i] /= len;
        }
    }

    // True if the world-space AABB (8 corners of the local box transformed by `model`) is at least partly
    // inside the frustum. Positive-vertex test: for each plane, if the farthest-along-the-normal corner is
    // behind the plane, the whole box is outside.
    bool AabbInFrustum(GLVector3 localMin, GLVector3 localMax, Matrix4x4 model) {
        // Transform the 8 corners to world, take their AABB (cheap + matches the GL whole-corner loop).
        Vector3 wlo = new(float.MaxValue), whi = new(float.MinValue);
        for (int c = 0; c < 8; c++) {
            var lc = new Vector3((c & 1) == 0 ? localMin.X : localMax.X,
                                 (c & 2) == 0 ? localMin.Y : localMax.Y,
                                 (c & 4) == 0 ? localMin.Z : localMax.Z);
            Vector3 w = Vector3.Transform(lc, model);
            wlo = Vector3.Min(wlo, w); whi = Vector3.Max(whi, w);
        }
        for (int i = 0; i < 6; i++) {
            Vector4 p = frustumPlanes[i];
            // Positive vertex (farthest along the plane normal).
            Vector3 pv = new(p.X >= 0 ? whi.X : wlo.X, p.Y >= 0 ? whi.Y : wlo.Y, p.Z >= 0 ? whi.Z : wlo.Z);
            if (p.X * pv.X + p.Y * pv.Y + p.Z * pv.Z + p.W < 0f) return false;   // fully outside this plane
        }
        return true;
    }

    static Matrix4x4 ToNumerics(GLMatrix4 m) => new(
        m.M11, m.M12, m.M13, m.M14,
        m.M21, m.M22, m.M23, m.M24,
        m.M31, m.M32, m.M33, m.M34,
        m.M41, m.M42, m.M43, m.M44);
    static Vector3 ToNumerics(GLVector3 v) => new(v.X, v.Y, v.Z);
    static Vector4 ToNumerics(Vector4 v) => v;
}
