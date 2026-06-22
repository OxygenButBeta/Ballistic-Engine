namespace BallisticEngine.DX12;

public enum Dx12ResourceUsage {
    None = 0,
    GBufferShaderRead,
    GBufferDepthShaderRead,
    GBufferDepthReadOnly,
    SceneColorShaderRead,
}
