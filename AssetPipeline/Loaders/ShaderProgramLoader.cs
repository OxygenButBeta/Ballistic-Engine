namespace BallisticEngine.AssetPipeline.Loaders;

// .shader asset: { "version": 1, "vertex": "<.glsl ref>", "fragment": "<.glsl ref>" }
public sealed class ShaderDefinition {
    public int Version { get; set; } = 1;
    public string Vertex { get; set; }
    public string Fragment { get; set; }
}

public static class ShaderProgramLoader {
    public static StandardShader Load(BallisticProject project, string assetPath) {
        var definition = PipelineJson.Read<ShaderDefinition>(project.ResolveAbsolute(assetPath));

        var vertexCode = ReadGlsl(project, definition.Vertex, assetPath);
        var fragmentCode = ReadGlsl(project, definition.Fragment, assetPath);
        if (vertexCode is null || fragmentCode is null)
            return null;

        return GraphicAPI.CreateStandardShader(vertexCode, fragmentCode);
    }

    static string ReadGlsl(BallisticProject project, string reference, string ownerPath) {
        var glslAssetPath = AssetRef.IsGuidRef(reference, out Guid guid)
            ? AssetDatabase.GuidToAssetPath(guid)
            : reference;

        if (glslAssetPath is null) {
            Debugging.LogError($"'{ownerPath}': shader stage reference '{reference}' does not resolve to an asset.");
            return null;
        }

        var absolute = project.ResolveAbsolute(glslAssetPath);
        if (!File.Exists(absolute)) {
            Debugging.LogError($"'{ownerPath}': shader source '{glslAssetPath}' does not exist.");
            return null;
        }

        return File.ReadAllText(absolute);
    }
}
