
namespace BallisticEngine.DX12;

// DX12 StandardShader. IMPORTANT DESIGN NOTE (full-DX strategy): the engine's shader API is GL-shaped —
// per-name uniform setters (SetMatrix4/SetFloat3/...) and Activate/Deactivate program binding. DX12 has
// no such model: shading is driven by constant buffers + descriptor tables the DX12 renderer fills
// directly. And the source the engine passes here is GLSL (legacy material/shader assets), not valid HLSL.
//
// So this class does NOT compile the engine's GLSL. It is a lightweight HANDLE that satisfies
// Material.Shader and SharedResources caching. The DX12 renderer uses its OWN embedded HLSL
// (StandardOpaque.hlsl) for the opaque path and ignores the engine's source entirely. The uniform
// setters are no-ops — the engine core never sets uniforms by name (the GL renderer that did is deleted).
// This is the impedance break the full-DX decision lets us take cleanly (no GL uniform model to reimplement).
public sealed class Dx12StandardShader : StandardShader {
    public override int UID { get; }
    static int nextId = 1;

    public Dx12StandardShader(string vertexCode, string fragmentCode, string identityExtra = null)
        : base(vertexCode, fragmentCode, identityExtra) {
        UID = nextId++;
    }

    // No GLSL->HLSL compile: the DX12 renderer owns its HLSL. This satisfies the abstract member only.
    protected override void Compile(string vertexCode, string fragmentCode) { }

    protected override void ActivateShader() { }
    protected override void DeactivateShader() { }
    protected override void OnDispose() { }

    // Uniform setters: no-ops by design (see class note). The DX12 renderer never routes through these.
    public override void SetBool(string name, bool value) { }
    public override void SetInt(string name, int value) { }
    public override void SetFloat(string name, float value) { }
    public override void SetFloat2(string name, Vector2 value) { }
    public override void SetFloat3(string name, Vector3 value) { }
    public override void SetFloat4(string name, Vector4 value) { }
    public override void SetMatrix4(string name, ref Matrix4 value, bool transpose = false) { }
}
