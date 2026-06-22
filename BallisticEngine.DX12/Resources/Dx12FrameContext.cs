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

    public int GrainFrame { get; set; }

    public int FrameCounter { get; set; }
}
