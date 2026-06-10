using System.Reflection;

namespace BallisticEngine;

// Shaders the GL backend cannot run without (e.g. the fullscreen-quad blit) are
// embedded in the assembly so they exist regardless of project content or CWD.
public static class EmbeddedShaderSource {
    public static string Read(string fileName) {
        Assembly assembly = Assembly.GetExecutingAssembly();

        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));

        if (resourceName is null)
            throw new FileNotFoundException($"Embedded shader '{fileName}' not found in {assembly.GetName().Name}.");

        using Stream stream = assembly.GetManifestResourceStream(resourceName);
        using StreamReader reader = new(stream!);
        return reader.ReadToEnd();
    }
}
