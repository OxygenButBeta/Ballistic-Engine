
namespace BallisticEngine.DX12;

public sealed class Dx12StandardShader : StandardShader {
    public override int UID { get; }
    static int nextId = 1;

    public Dx12StandardShader(string vertexCode, string fragmentCode, string identityExtra = null)
        : base(vertexCode, fragmentCode, identityExtra) {
        UID = nextId++;
    }

    protected override void Compile(string vertexCode, string fragmentCode) { }

    protected override void ActivateShader() { }
    protected override void DeactivateShader() { }
    protected override void OnDispose() { }

    public override void SetBool(string name, bool value) { }
    public override void SetInt(string name, int value) { }
    public override void SetFloat(string name, float value) { }
    public override void SetFloat2(string name, Vector2 value) { }
    public override void SetFloat3(string name, Vector3 value) { }
    public override void SetFloat4(string name, Vector4 value) { }
    public override void SetMatrix4(string name, ref Matrix4 value, bool transpose = false) { }
}
