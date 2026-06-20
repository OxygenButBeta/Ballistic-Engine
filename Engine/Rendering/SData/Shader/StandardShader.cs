namespace BallisticEngine;

public abstract class StandardShader(string vertexCode, string fragmentCode) : Shader {
    public override ResourceIdentity Identity { get; } = ResourceIdentity.Combine(vertexCode, fragmentCode);

    // Retained so the renderer can derive depth-only companions (z-prepass) that rasterize
    // with this shader's exact vertex math, AND GPU-driven companions (MDI + bindless) that
    // reuse the exact same shading math with the data source swapped to SSBOs.
    public string VertexCode { get; } = vertexCode;
    public string FragmentCode { get; } = fragmentCode;

    // The declared property set. Defaults to the canonical Standard (PBR) list; the shader loader
    // overwrites it when the .shader asset carries its own Properties block (a custom shader). The
    // renderer reads material values through these properties' semantics; the editor generates the
    // material inspector from them.
    ShaderProperties properties = StandardShaderProperties.Build();
    public override ShaderProperties Properties => properties;
    public void SetProperties(ShaderProperties value) => properties = value ?? StandardShaderProperties.Build();

    // Custom Surface() HLSL body (Unity-style surface shader). When non-null, the renderer draws materials
    // using this shader through a per-material PSO (compiled from the surface body + the engine's G-buffer
    // skeleton) on the legacy CPU path, instead of the embedded Standard PSO. Null = the Standard path (the
    // common case — byte-identical). SurfaceKey is a stable identity for the PSO cache + hot-reload (the
    // source asset path + a content hash). Lives on the backend-agnostic StandardShader so the asset loader
    // sets it without a DX12 reference.
    public string SurfaceSource { get; set; }
    public string SurfaceKey { get; set; }
    public bool HasCustomSurface => SurfaceSource is not null;

    protected abstract void Compile(string vertexCode, string fragmentCode);
}