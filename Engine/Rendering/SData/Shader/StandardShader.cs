namespace BallisticEngine;

public abstract class StandardShader(string vertexCode, string fragmentCode) : Shader {
    public override ResourceIdentity Identity { get; } = ResourceIdentity.Combine(vertexCode, fragmentCode);
    protected abstract void Compile(string vertexCode, string fragmentCode);
}