using System.Reflection;

namespace BallisticEngine.DX12;

public static class EmbeddedShaderSource {
    static readonly Assembly Asm = typeof(EmbeddedShaderSource).Assembly;

    public static string ReadHlsl(string name) {
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
