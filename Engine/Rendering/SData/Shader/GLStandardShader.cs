using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace BallisticEngine;

public sealed class GLStandardShader : StandardShader {
    public override int UID { get; } = GL.CreateProgram();
    readonly Dictionary<string, int> UniformLookup = new();

    public GLStandardShader(string vertexCode, string fragmentCode) : base(vertexCode, fragmentCode) {
        Compile(vertexCode, fragmentCode);
    }

    protected override void Compile(string vertexCode, string fragmentCode) {
        GLSLShaderUtilities.AttachToShader(
            UID,
            true,
            GLSLShaderUtilities.CompileProgram(vertexCode, ShaderType.VertexShader),
            GLSLShaderUtilities.CompileProgram(fragmentCode, ShaderType.FragmentShader));
    }

    int GetUniformLocation(string uniformName) {
        if (UniformLookup.TryGetValue(uniformName, out var location))
            return location;

        location = GL.GetUniformLocation(UID, uniformName);

        UniformLookup[uniformName] = location;
        return location;
    }

    protected override void OnDispose() {
        base.Dispose();
        GL.DeleteProgram(UID);
    }

    protected override void ActivateShader() => GL.UseProgram(UID);
    protected override void DeactivateShader() => GL.UseProgram(0);
    
    public override void SetBool(string name, bool value) => GL.Uniform1(GetUniformLocation(name), value ? 1 : 0);
    public override void SetInt(string name, int value) => GL.Uniform1(GetUniformLocation(name), value);
    public override void SetFloat(string name, float value) => GL.Uniform1(GetUniformLocation(name), value);
    public override void SetFloat2(string name, Vector2 value) => GL.Uniform2(GetUniformLocation(name), value);
    public override void SetFloat3(string name, Vector3 value) => GL.Uniform3(GetUniformLocation(name), value);
    public override void SetFloat4(string name, Vector4 value) => GL.Uniform4(GetUniformLocation(name), value);
    public override void SetMatrix4(string name,ref Matrix4 value, bool transpose = false) =>
        GL.UniformMatrix4(GetUniformLocation(name), transpose, ref value);
}