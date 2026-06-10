using OpenTK.Graphics.OpenGL4;

public static class GLSLShaderUtilities {
    public static int CompileProgram(string code, ShaderType type, bool throwOnError = true) {
        var shader = GL.CreateShader(type);
        GL.ShaderSource(shader, ToAscii(code));
        GL.CompileShader(shader);
        GL.GetShader(shader, ShaderParameter.CompileStatus, out var success);

        if (success != 0 || !throwOnError)
            return shader;

        var infoLog = GL.GetShaderInfoLog(shader);
        throw new Exception($"{type} compilation failed:\n{infoLog}");
    }

    // GL.ShaderSource(int, string) passes the char count as the length of the UTF-8
    // encoded buffer, so every non-ASCII character (e.g. in comments) truncates the
    // source by a byte. GLSL is ASCII-only outside comments; degrade the rest to '?'.
    static string ToAscii(string code) {
        if (!code.Any(c => c > 127))
            return code;

        var chars = code.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
            if (chars[i] > 127)
                chars[i] = '?';

        return new string(chars);
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