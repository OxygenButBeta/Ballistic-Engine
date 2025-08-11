using OpenTK.Graphics.OpenGL4;

public static class GLSLShaderUtilities {
    public static int CompileProgram(string code, ShaderType type, bool throwOnError = true) {
        var shader = GL.CreateShader(type);
        GL.ShaderSource(shader, code);
        GL.CompileShader(shader);
        GL.GetShader(shader, ShaderParameter.CompileStatus, out var success);

        if (success != 0 || !throwOnError)
            return shader;

        var infoLog = GL.GetShaderInfoLog(shader);
        throw new Exception($"{type} compilation failed:\n{infoLog}");
    }

    public static void AttachToShader(int Program, bool throwOnError = true, params int[] shaders) {
        foreach (var shader in shaders)
            GL.AttachShader(Program, shader);

        GL.LinkProgram(Program);
        GL.GetProgram(Program, GetProgramParameterName.LinkStatus, out var linkStatus);

        if (linkStatus != 0 || !throwOnError)
            return;

        var infoLog = GL.GetProgramInfoLog(Program);
        throw new Exception($"Shader linking failed:\n{infoLog}");
    }
}