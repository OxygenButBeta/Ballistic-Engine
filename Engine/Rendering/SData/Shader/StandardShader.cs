namespace BallisticEngine;

public abstract class StandardShader(string vertexCode, string fragmentCode, string identityExtra = null) : Shader {
    public override ResourceIdentity Identity { get; } = identityExtra is null
        ? ResourceIdentity.Combine(vertexCode, fragmentCode)
        : ResourceIdentity.Combine(vertexCode, fragmentCode, identityExtra);

    public string VertexCode { get; } = vertexCode;
    public string FragmentCode { get; } = fragmentCode;

    ShaderProperties properties = StandardShaderProperties.Build();
    public override ShaderProperties Properties => properties;
    public void SetProperties(ShaderProperties value) => properties = value ?? StandardShaderProperties.Build();

    public string SurfaceSource { get; set; }
    public string SurfaceKey { get; set; }

    public string SurfaceSourcePath { get; set; }
    public bool HasCustomSurface => SurfaceSource is not null;

    protected abstract void Compile(string vertexCode, string fragmentCode);
}