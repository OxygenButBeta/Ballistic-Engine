namespace BallisticEngine;

public abstract class StandardShader(string vertexCode, string fragmentCode) : Shader {
    public override ResourceIdentity Identity { get; } = ResourceIdentity.Combine(vertexCode, fragmentCode);

    // Retained so the renderer can derive depth-only companions (z-prepass) that rasterize
    // with this shader's exact vertex math.
    public string VertexCode { get; } = vertexCode;

    protected abstract void Compile(string vertexCode, string fragmentCode);
}