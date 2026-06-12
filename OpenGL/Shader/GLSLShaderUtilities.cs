using OpenTK.Graphics.OpenGL4;

public static class GLSLShaderUtilities {
    public static int CompileProgram(string code, ShaderType type, bool throwOnError = true) {
        var shader = GL.CreateShader(type);
        var source = ToAscii(code);
        if (type == ShaderType.VertexShader)
            source = InjectInvariantPosition(source);
        GL.ShaderSource(shader, source);
        GL.CompileShader(shader);
        GL.GetShader(shader, ShaderParameter.CompileStatus, out var success);

        if (success != 0 || !throwOnError)
            return shader;

        var infoLog = GL.GetShaderInfoLog(shader);
        throw new Exception($"{type} compilation failed:\n{infoLog}");
    }

    // Every vertex shader gets `invariant gl_Position;`: the z-prepass re-renders geometry
    // with the same vertex source under a different program, and the main pass depth-tests
    // EQUAL against that depth — the GLSL invariance guarantee is what makes the two
    // rasterize bit-identically (without it, holes appear wherever the optimizer differs).
    static string InjectInvariantPosition(string code) {
        if (code.Contains("invariant gl_Position"))
            return code;
        var versionEnd = code.IndexOf('\n', Math.Max(code.IndexOf("#version", StringComparison.Ordinal), 0));
        return versionEnd < 0
            ? code
            : code.Insert(versionEnd + 1, "invariant gl_Position;\n");
    }

    // GL.ShaderSource(int, string) passes the char count as the length of the UTF-8
    // encoded buffer, so every non-ASCII character (e.g. in comments) truncates the
    // source by a byte. GLSL is ASCII-only outside comments; degrade the rest to '?'.
    // Public so compute-shader compile paths that don't go through CompileProgram (the
    // GPU-driven cull) can sanitize too — an em-dash in a comment truncates the tail and
    // yields "unexpected end of file".
    public static string ToAscii(string code) {
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