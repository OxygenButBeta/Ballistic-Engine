using BallisticEngine.Rendering;
using BallisticEngine.Shared.Runtime_Set;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using static System.Int32;

namespace BallisticEngine;

public abstract class IShader : BObject, IDisposable, ISharedResource
{
    public ResourceIdentity Identity { get; }
    public int UID { get; protected set; }
    // public abstract void SetBool(string name, bool value);
    // public abstract void SetInt(string name, int value);
    // public abstract void SetFloat(string name, float value);
    // public abstract void SetVector2(string name, Vector2 value);
    // public abstract void SetVector3(string name, Vector3 value);
    // public abstract void SetVector4(string name, Vector4 value);
    // public abstract void SetMatrix4(string name, Matrix4 value);

    protected IShader(string rawShaderCode, ShaderType shaderType)
    {
        Identity = ResourceIdentity.Combine(rawShaderCode);
        SharedResources<IShader>.AddResource(this);
    }

    protected abstract void CompileShader(string code, ShaderType type);

    public abstract void Dispose();
    public abstract void Activate();
    public abstract void Deactivate();
}

public class GLShader : IShader
{
    public GLShader(string rawShaderCode, ShaderType shaderType) : base(rawShaderCode, shaderType)
    {
    }

    protected override void CompileShader(string code, ShaderType type)
    {
        throw new NotImplementedException();
    }

    public override void Dispose()
    {
        throw new NotImplementedException();
    }

    public override void Activate()
    {
        throw new NotImplementedException();
    }

    public override void Deactivate()
    {
        throw new NotImplementedException();
    }
}
public class LegacyShader : BObject, IDisposable, ISharedResource
{
    public ResourceIdentity Identity { get; }
    public int UID { get; private set; }

    class CompiledShaderProgram(int shaderUid, ResourceIdentity identity) : ISharedResource
    {
        public readonly int ShaderUID = shaderUid;
        public ResourceIdentity Identity { get; } = identity;
    }

    // ReSharper disable once MemberCanBePrivate.Global
    public static LegacyShader Create(string vertexCode, string fragmentCode)
    {
        ResourceIdentity CombinedShaderID = ResourceIdentity.Combine(vertexCode, fragmentCode);
        return !SharedResources<LegacyShader>.TryGetResource(CombinedShaderID, out LegacyShader sharedShader)
            ? new LegacyShader(vertexCode, fragmentCode, CombinedShaderID)
            : sharedShader;
    }

    public static LegacyShader CreateOrGetDefault()
    {
        return Create(DefaultShader.VertexShader, DefaultShader.FragmentShader);
    }

    void LoadOrCompileShader(string vert, string frag)
    {
        ResourceIdentity VertID = vert;
        ResourceIdentity FragID = frag;

        var IsVertexShaderCompiled =
            SharedResources<CompiledShaderProgram>.TryGetResource(VertID,
                out CompiledShaderProgram compiledVertexShaderProgram);

        var FragmentShaderCompiled =
            SharedResources<CompiledShaderProgram>.TryGetResource(FragID,
                out CompiledShaderProgram compiledFragmentShaderProgram);

        int VertexShaderID;
        if (IsVertexShaderCompiled)
        {
            VertexShaderID = compiledVertexShaderProgram.ShaderUID;
        }
        else
        {
            VertexShaderID = CompileShader(vert, ShaderType.VertexShader);
            SharedResources<CompiledShaderProgram>.AddResource(new CompiledShaderProgram(VertexShaderID, VertID));
        }

        int FragmentShaderID;
        if (FragmentShaderCompiled)
        {
            FragmentShaderID = compiledFragmentShaderProgram.ShaderUID;
        }
        else
        {
            FragmentShaderID = CompileShader(frag, ShaderType.FragmentShader);
            SharedResources<CompiledShaderProgram>.AddResource(new CompiledShaderProgram(FragmentShaderID, FragID));
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


    protected LegacyShader(string vertexCode, string fragmentCode, ResourceIdentity identity)
    {
        Identity = identity;
        SharedResources<LegacyShader>.AddResource(this);
        LoadOrCompileShader(vertexCode, fragmentCode);
    }

    int CompileShader(string code, ShaderType type)
    {
        var shader = GL.CreateShader(type);
        GL.ShaderSource(shader, code);
        GL.CompileShader(shader);

        GL.GetShader(shader, ShaderParameter.CompileStatus, out int success);
        if (success != 0)
            return shader;

        var infoLog = GL.GetShaderInfoLog(shader);
        throw new Exception($"{type} compilation failed:\n{infoLog}");
    }

    public void Activate()
    {
        GL.UseProgram(UID);
    }

    public void Deactivate()
    {
        GL.UseProgram(0);
    }

    public void Dispose() => GL.DeleteProgram(UID);
}