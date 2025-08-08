using BallisticEngine.Rendering;
using BallisticEngine.Shared.Runtime_Set;
using OpenTK.Graphics.OpenGL4;
using static System.Int32;

namespace BallisticEngine;

public class Shader : BObject, IDisposable {
    public int UID { get; private set; }
    static int LastAssignedUID;
    bool NeedsActivation() => UID != LastAssignedUID;

    class CompiledShaderCode {
        public readonly int ShaderUID;

        public CompiledShaderCode(int shaderUid) {
            ShaderUID = shaderUid;
        }
    }

    // ReSharper disable once MemberCanBePrivate.Global
    public static Shader Create(string vertexCode, string fragmentCode) {
        var VertHash = FNV1a.HashStr(vertexCode);
        var FragHash = FNV1a.HashStr(fragmentCode);

        if (RuntimeCache<int, Shader>.TryGetValue(HashCode.Combine(VertHash, FragHash), out Shader shader)) {
            Debugging.SystemLog("Shader loaded from cache: " + VertHash + ", " + FragHash, SystemLogPriority.Medium);
            return shader;
        }

        shader = new Shader(vertexCode, fragmentCode);
        RuntimeCache<int, Shader>.Add(HashCode.Combine(VertHash, FragHash), shader);
        return shader;
    }

    public static Shader CreateOrGetDefault() {
        return Create(DefaultShader.VertexShader, DefaultShader.FragmentShader);
    }

    void LoadOrCompileShader(string vert, string frag) {
        var VertHash = FNV1a.HashStr(vert);
        var FragHash = FNV1a.HashStr(frag);

        var IsVertexShaderCompiled =
            RuntimeCache<int, CompiledShaderCode>.TryGetValue(VertHash, out CompiledShaderCode compiledVertexShader);

        var FragmentShaderCompiled =
            RuntimeCache<int, CompiledShaderCode>.TryGetValue(FragHash, out CompiledShaderCode compiledFragmentShader);

        int VertexShaderID;
        if (IsVertexShaderCompiled) {
            VertexShaderID = compiledVertexShader.ShaderUID;
        }
        else {
            VertexShaderID = CompileShader(vert, ShaderType.VertexShader);
            RuntimeCache<int, CompiledShaderCode>.Add(VertHash, new CompiledShaderCode(VertexShaderID));
        }

        int FragmentShaderID;
        if (FragmentShaderCompiled) {
            FragmentShaderID = compiledFragmentShader.ShaderUID;
        }
        else {
            FragmentShaderID = CompileShader(frag, ShaderType.FragmentShader);
            RuntimeCache<int, CompiledShaderCode>.Add(FragHash, new CompiledShaderCode(FragmentShaderID));
        }


        UID = GL.CreateProgram();
        GL.AttachShader(UID, VertexShaderID);
        GL.AttachShader(UID, FragmentShaderID);
        GL.LinkProgram(UID);
        GL.GetProgram(UID, GetProgramParameterName.LinkStatus, out var linkStatus);

        if (linkStatus != 0)
            return;

        var infoLog = GL.GetProgramInfoLog(UID);
        throw new Exception($"Shader linking failed:\n{infoLog}");
    }


    Shader(string vertexCode, string fragmentCode) {
        LoadOrCompileShader(vertexCode, fragmentCode);
            Debugging.SystemLog("Shader Created ", SystemLogPriority.Critical);
        
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

    public void Activate() {
        if (!NeedsActivation()) 
            return;
        
        LastAssignedUID = UID;
        GL.UseProgram(UID);
    }

    public void Deactivate() {
    LastAssignedUID = MinValue;;
        GL.UseProgram(0);
    } 
        
    public void Dispose() => GL.DeleteProgram(UID);
}