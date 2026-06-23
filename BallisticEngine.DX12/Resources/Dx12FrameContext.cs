using Vortice.Direct3D12;

namespace BallisticEngine.DX12;

public sealed class Dx12FrameContext {
    public Matrix4x4 View                { get; init; }
    public Matrix4x4 Proj                { get; init; }
    public Matrix4x4 ViewProj            { get; init; }
    public Matrix4x4 ProjUnjittered      { get; init; }
    public Matrix4x4 ViewProjUnjittered  { get; init; }
    public Matrix4x4 PrevViewProjUnjittered { get; init; }
    public Vector2   CurrentJitter       { get; init; }
    public Vector3   CamPos              { get; init; }

    public Vector3 LightDir    { get; init; }
    public Vector3 LightColor  { get; init; }
    public Vector3 Ambient     { get; init; }
    public float   Exposure    { get; init; }

    public List<IStaticMeshRenderer> WholeMeshRenderers { get; init; }
    public Vector4[] FrustumPlanes { get; init; }
    public Matrix4x4[] CascadeMatrices { get; init; }

    public int TargetW { get; init; }
    public int TargetH { get; init; }
    public int OutputW { get; init; }
    public int OutputH { get; init; }

    public bool DeterministicCapture { get; init; }

    public CpuDescriptorHandle SsaoResult { get; init; }

    public CpuDescriptorHandle AoResult { get; init; }

    public Action AoToNonPixelShaderResource { get; init; }

    public bool SkyOcclusionActive { get; init; }

    public bool TaaActive { get; init; }
    public bool FsrActive { get; init; }

    public Dx12FsrUpscaler      Fsr            { get; init; }
    public Dx12OffscreenTarget  FsrOutput      { get; init; }
    public bool                 MotionPrevValid{ get; init; }

    public UpscalerKind         ActiveUpscaler { get; init; }
    public Dx12DlssUpscaler     Dlss           { get; init; }
    public Dx12XessUpscaler     Xess           { get; init; }

    public Dx12Device          Dev            { get; init; }
    public Dx12OffscreenTarget Target         { get; init; }
    public Dx12OffscreenTarget Ldr            { get; init; }
    public Dx12GBuffer         GBuffer        { get; init; }
    public Dx12IblBaker        Ibl            { get; init; }
    public Dx12SkyLuts         SkyLuts        { get; init; }
    public Dx12ClusteredLights ClusteredLights{ get; init; }
    public Dx12ShadowMap       ShadowMap      { get; init; }

    public Dx12VirtualShadowMap Vsm           { get; init; }
    public bool VsmActiveThisFrame            { get; init; }
    public Dx12GpuDrivenRenderer GpuDriven    { get; init; }

    public Dx12OffscreenTarget RtShadowMask   { get; init; }

    public CpuDescriptorHandle CapsuleShadowMask { get; set; }
    public bool CapsuleShadowsThisFrame { get; set; }

    public ulong FrameCbAddress { get; init; }

    public Dx12DxrShared Dxr { get; init; }

    // Aurora GI scene substrate (per-triangle radiance cache + per-instance meta). The Reflections pass
    // (after the Aurora GI pass at event 500) reads it so rough reflections sample the SAME multi-bounce GI
    // the diffuse uses. Null when Aurora is off; reflections gates on AuroraActiveThisFrame too.
    public Dx12AuroraScene AuroraScene { get; init; }

    public bool BarriersDerived { get; init; }

    // FAZ -1c — render-graph v2 (BALLISTIC_DX12_RG=1) drives the Composite pass this frame, so the
    // v1 composite pass's Enabled() returns false (skip) to avoid compositing twice. Default false →
    // door-off behaviour is byte-identical (v1 owns composite).
    public bool RgV2OwnsComposite { get; init; }

    // FAZ -1d — render-graph v2 (BALLISTIC_DX12_RG=1) drives the TAA pass this frame, so the v1 TAA
    // pass's Enabled() returns false (skip) to avoid resolving TAA twice. Default false → door-off
    // behaviour is byte-identical (v1 owns TAA). Only set when TAA would run at all (!FsrActive).
    public bool RgV2OwnsTaa { get; init; }

    // FAZ -1d — render-graph v2 (BALLISTIC_DX12_RG=1) drives the remaining PostProcess leaf passes this
    // frame (Motion Blur / Depth of Field / FSR), so the matching v1 pass's Enabled() returns false (skip)
    // to avoid running them twice. Each mirrors RgV2OwnsTaa: default false → door-off is byte-identical;
    // set only when that pass would actually run (MotionBlur/DoF gate on PostFX-enabled & !deterministic,
    // FSR on FsrActive — see where these are assigned in DX12HDRenderer's frame-context build).
    public bool RgV2OwnsMotionBlur { get; init; }
    public bool RgV2OwnsDof { get; init; }
    public bool RgV2OwnsFsr { get; init; }

    // FAZ -1d-FINAL (atmosphere group) — when render-graph v2 owns the WHOLE frame (v1 bypassed) it will
    // drive the atmosphere passes too, in event order: Sky(350) -> AerialPerspective(400) ->
    // Transparents(450) -> [GI 500] -> Fog(550). The matching v1 pass's Enabled() returns false (skip)
    // when its flag is set, so the pass runs exactly once. These mirror RgV2OwnsTaa.
    //
    // STAY FALSE FOR NOW (plumbing only): the current v2 block (RunRenderGraphV2) runs at the END of the
    // frame, AFTER v1's whole graph. The atmosphere passes sit in the MIDDLE (350-550, before the still-v1
    // GI(500) / Reflections(600)), so appending them to the end-of-frame block would REORDER them after GI
    // and Reflections -> wrong output. They are only enabled once v1 is bypassed and the v2 graph owns the
    // frame in event order. Default false => door-off byte-identical AND door-on unchanged (no v1 pass skips,
    // RunRenderGraphV2 does not run them — see the FAZ -1d-FINAL note there). The RecordV2 bodies + the v1
    // Enabled() `&& !RgV2Owns*` guards are in place, ready for that final wiring.
    // NOTE (FAZ -1d-FINAL): these mid-frame owner flags are `set` (not `init`) because their conditions
    // mirror the v1 passes' Enabled() predicates, several of which need the fully-built ctx (WouldRun/WillRun
    // take a ctx). They are assigned in DX12HDRenderer's frame-context build immediately AFTER the object
    // initializer (alongside ctx.AuroraActiveThisFrame), from `rgV2FullFrame && <the v1 run condition>`.
    public bool RgV2OwnsSky { get; set; }
    public bool RgV2OwnsAerialPersp { get; set; }
    public bool RgV2OwnsTransparents { get; set; }
    public bool RgV2OwnsFog { get; set; }

    // FAZ -1d-FINAL (reflections + GI group) — when render-graph v2 owns the WHOLE frame (v1 bypassed) it
    // will drive these too, in event order: [GI 500] (Aurora OR Lumen — mutually exclusive) -> Reflections(600).
    // The matching v1 pass's Enabled() returns false (skip) when its flag is set, so the pass runs exactly
    // once. These mirror RgV2OwnsSky/Fog.
    //
    // STAY FALSE FOR NOW (plumbing only): the current v2 block (RunRenderGraphV2) runs at the END of the
    // frame, AFTER v1's whole graph. GI(500) / Reflections(600) sit MID-frame; appending them to the
    // end-of-frame block would REORDER them relative to still-v1 passes -> wrong output. They are only
    // enabled once v1 is bypassed and the v2 graph owns the frame in event order. Default false => door-off
    // byte-identical AND door-on unchanged (no v1 pass skips, RunRenderGraphV2 does not run them). The
    // RecordV2 bodies + the v1 Enabled() `&& !RgV2Owns*` guards are in place, ready for that final wiring.
    // Aurora/Lumen are still mutually exclusive (Aurora.WouldRun has `&& !Dx12LumenGiPass.Armed(ctx)`), so
    // at most one of the two GI flags is ever meaningfully set; both gate only the instance Enabled, not the
    // static WouldRun (which other code reads to mirror ctx.AuroraActiveThisFrame / ctx.LumenActiveThisFrame).
    public bool RgV2OwnsReflections { get; set; }
    public bool RgV2OwnsAuroraGi { get; set; }
    public bool RgV2OwnsLumenGi { get; set; }

    // FAZ -1d-FINAL (lighting group) — when render-graph v2 owns the WHOLE frame (v1 bypassed) it will drive
    // the lighting/AO passes too, in event order: GTAO(200) -> RTAO(250) / CapsuleShadows(250) ->
    // Deferred OpaqueLighting(300). The matching v1 pass's Enabled() returns false (skip) when its flag is
    // set, so the pass runs exactly once. These mirror RgV2OwnsSky/Reflections.
    //
    // STAY FALSE FOR NOW (plumbing only): the current v2 block (RunRenderGraphV2) runs at the END of the
    // frame, AFTER v1's whole graph. These passes sit MID-frame (200-300, before the still-v1 GI(500) /
    // Reflections(600)); appending them to the end-of-frame block would REORDER them after GI/Reflections ->
    // wrong output. They are only enabled once v1 is bypassed and the v2 graph owns the frame in event order.
    // Default false => door-off byte-identical AND door-on unchanged (no v1 pass skips, RunRenderGraphV2 does
    // not run them). The RecordV2 bodies + the v1 Enabled() `&& !RgV2Owns*` guards are in place, ready for
    // that final wiring.
    public bool RgV2OwnsDeferred { get; set; }
    public bool RgV2OwnsGtao { get; set; }
    public bool RgV2OwnsRtao { get; set; }
    public bool RgV2OwnsCapsuleShadow { get; set; }

    public Dx12RenderDoors      Doors    { get; init; }
    public PostProcessSettings  PostFX   { get; init; }
    public RenderStats          Stats    { get; init; }

    public Dx12OffscreenTarget SceneColor { get; set; }

    public bool   IblActiveThisFrame { get; set; }
    public bool   ShadowsThisFrame   { get; set; }
    public bool   RtShadowsThisFrame { get; set; }

    public bool   AuroraActiveThisFrame { get; set; }

    // Lumen GI active this frame (FAZ 0). Mirrors AuroraActiveThisFrame: set in BeginRender from
    // Dx12LumenGiPass.WouldRun. FAZ 0 writes no GI, so the deferred pass does NOT yet key its IBL-diffuse
    // suppression off this flag (that flips in FAZ 6 when screen-probe GI first contributes diffuse — see the
    // // FAZ 6 marker in Dx12DeferredLightingPass.Record). Set now so later phases need no extra wiring.
    public bool   LumenActiveThisFrame { get; set; }

    // FAZ 10 — VOLUMETRIC GI. When Lumen GI runs with the world-space radiance cache armed, the Lumen pass publishes
    // the cache's sampling params here (event 500), so the LATER fog pass (event ~700) can in-scatter the actual
    // indirect radiance at each march sample instead of a flat constant SkyAmbient. Valid==false → fog uses its old
    // constant ambient (byte-identical). This is the same RC_PARAMS the screen-probe gather samples (world-space,
    // view-independent), so the fog GI is consistent with the surface GI. Set by Dx12LumenGiPass.Record.
    public LumenRcParamsForVolumetrics LumenRc { get; set; }

    public int GrainFrame { get; set; }

    public int FrameCounter { get; set; }
}

// FAZ 10 — the Lumen world-space radiance cache's sampling parameters, published by the Lumen GI pass for the fog pass
// to in-scatter indirect light (volumetric GI). Mirrors the RC_PARAMS block in LumenRadianceCacheSample.hlsl. Valid=false
// (default) → the consumer falls back to its constant ambient (no behaviour change). A plain struct (default = all-zero,
// Valid=false) so a frame with no Lumen / no radiance cache leaves it inert.
public struct LumenRcParamsForVolumetrics {
    public bool Valid;
    public System.Numerics.Vector3 Origin; public float ProbeSpacing;
    public uint GridRes, AtlasInProbes, ProbeRes, FinalProbeRes;
    public float TraceStop;
    public int IndirBindless, RadBindless, HitBindless;
    // FAZ 10 — the raw textures, for a consumer that creates explicit SRVs (transparent forward, no bindless heap).
    public Vortice.Direct3D12.ID3D12Resource IndirTex, RadTex, HitTex;
}
