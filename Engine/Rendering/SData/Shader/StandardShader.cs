namespace BallisticEngine;

public abstract class StandardShader(string vertexCode, string fragmentCode) : Shader {
    public override ResourceIdentity Identity { get; } = ResourceIdentity.Combine(vertexCode, fragmentCode);

    // Retained so the renderer can derive depth-only companions (z-prepass) that rasterize
    // with this shader's exact vertex math, AND GPU-driven companions (MDI + bindless) that
    // reuse the exact same shading math with the data source swapped to SSBOs.
    public string VertexCode { get; } = vertexCode;
    public string FragmentCode { get; } = fragmentCode;

    protected abstract void Compile(string vertexCode, string fragmentCode);
}