using System;
using System.IO;
using System.Reflection;

namespace BallisticEngine.DX12;

// Reads an embedded .hlsl shader from THIS assembly (the DX12 backend), mirroring the engine's
// EmbeddedShaderSource for GLSL. Shaders live under Shaders\ and are embedded via the csproj glob.
public static class EmbeddedShaderSource {
    static readonly Assembly Asm = typeof(EmbeddedShaderSource).Assembly;

    // `name` is the file name (e.g. "Triangle.hlsl"); resource names are
    // "BallisticEngine.DX12.Shaders.<name>". Throws if missing (a typo'd shader name should fail loud).
    public static string ReadHlsl(string name) {
        // Match by suffix so subfolders under Shaders\ work too.
        string suffix = "." + name.Replace('/', '.').Replace('\\', '.');
        foreach (string res in Asm.GetManifestResourceNames())
            if (res.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) {
                using Stream s = Asm.GetManifestResourceStream(res)!;
                using var r = new StreamReader(s);
                return r.ReadToEnd();
            }
        throw new FileNotFoundException(
            $"Embedded HLSL '{name}' not found. Available: {string.Join(", ", Asm.GetManifestResourceNames())}");
    }
}
