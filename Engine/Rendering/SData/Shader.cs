using BallisticEngine.Rendering;
using OpenTK.Graphics.OpenGL4;

namespace BallisticEngine;

public class Shader : BObject, IDisposable {
    public int ID { get; private set; }

    void CompileShader(string vert, string frag) {
        
        var vertexShader = CompileShader(vert, ShaderType.VertexShader);
        var fragmentShader = CompileShader(frag, ShaderType.FragmentShader);

        ID = GL.CreateProgram();
        GL.AttachShader(ID, vertexShader);
        GL.AttachShader(ID, fragmentShader);
        GL.LinkProgram(ID);

        GL.GetProgram(ID, GetProgramParameterName.LinkStatus, out int linkStatus);
        if (linkStatus == 0) {
            var infoLog = GL.GetProgramInfoLog(ID);
            throw new Exception($"Program linking failed:\n{infoLog}");
        }

        GL.DeleteShader(vertexShader);
        GL.DeleteShader(fragmentShader);
    }
    public Shader() {
        var vertexCode = DefaultShader.VertexShader;
        var fragmentCode = DefaultShader.FragmentShader;
        CompileShader(vertexCode, fragmentCode);
    }
    public Shader(string vertexCode, string fragmentCode) {
        CompileShader(vertexCode, fragmentCode);
    }

    int CompileShader(string code, ShaderType type) {
        var shader = GL.CreateShader(type);
        GL.ShaderSource(shader, code);
        GL.CompileShader(shader);

        GL.GetShader(shader, ShaderParameter.CompileStatus, out int success);
        if (success != 0)
            return shader;

        var infoLog = GL.GetShaderInfoLog(shader);
        throw new Exception($"{type} compilation failed:\n{infoLog}");
    }

    public void Activate() => GL.UseProgram(ID);
    public void UnBind() => GL.UseProgram(0);
    public void Dispose() => GL.DeleteProgram(ID);
}