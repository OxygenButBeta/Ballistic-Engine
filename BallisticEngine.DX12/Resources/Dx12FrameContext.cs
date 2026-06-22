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

    public Dx12DdgiProbeGrid DdgiGrid { get; init; }

    public bool BarriersDerived { get; init; }

    public Dx12RenderDoors      Doors    { get; init; }
    public PostProcessSettings  PostFX   { get; init; }
    public RenderStats          Stats    { get; init; }

    public Dx12OffscreenTarget SceneColor { get; set; }

    public bool   IblActiveThisFrame { get; set; }
    public bool   ShadowsThisFrame   { get; set; }
    public bool   RtShadowsThisFrame { get; set; }

    public bool   GiActiveThisFrame { get; set; }

    public int GrainFrame { get; set; }

    public int FrameCounter { get; set; }
}
