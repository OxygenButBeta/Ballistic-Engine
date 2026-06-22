namespace BallisticEngine.AssetPipeline.Loaders;

public static class ShaderProgramLoader {
    public static StandardShader Load(BallisticProject project, string assetPath) {
        var definition = ContentText.ReadJson<ShaderDefinition>(project, assetPath);
        if (definition is null) {
            Debugging.LogError($"'{assetPath}': shader definition not found.");
            return null;
        }

        var vertexCode = ReadGlsl(project, definition.Vertex, assetPath);
        var fragmentCode = ReadGlsl(project, definition.Fragment, assetPath);
        if (vertexCode is null || fragmentCode is null)
            return null;

        bool isCustom = definition.Properties is { Length: > 0 } || !string.IsNullOrWhiteSpace(definition.Surface);
        var shader = GraphicAPI.CreateStandardShader(vertexCode, fragmentCode, isCustom ? assetPath : null);
        if (shader is not null && definition.Properties is { Length: > 0 } defs) {
            var props = new ShaderProperty[defs.Length];
            for (int i = 0; i < defs.Length; i++)
                props[i] = defs[i].ToShaderProperty(assetPath);
            shader.SetProperties(new ShaderProperties(props));
        }

        if (shader is not null && !string.IsNullOrWhiteSpace(definition.Surface)) {
            string surfacePath = AssetRef.IsGuidRef(definition.Surface, out Guid sg)
                ? AssetDatabase.GuidToAssetPath(sg) : definition.Surface;
            string body = surfacePath is not null ? ContentText.Read(project, surfacePath) : null;
            if (body is null)
                Debugging.LogError($"'{assetPath}': surface source '{definition.Surface}' did not load; " +
                                   "material renders as Standard.");
            else {
                shader.SurfaceSource = body;
                shader.SurfaceKey = surfacePath ?? definition.Surface;
                shader.SurfaceSourcePath = surfacePath;
            }
        }
        return shader;
    }

    static string ReadGlsl(BallisticProject project, string reference, string ownerPath) {
        var glslAssetPath = AssetRef.IsGuidRef(reference, out Guid guid)
            ? AssetDatabase.GuidToAssetPath(guid)
            : reference;

        if (glslAssetPath is null) {
            Debugging.LogError($"'{ownerPath}': shader stage reference '{reference}' does not resolve to an asset.");
            return null;
        }

        var code = ContentText.Read(project, glslAssetPath);
        if (code is null) {
            Debugging.LogError($"'{ownerPath}': shader source '{glslAssetPath}' does not exist.");
            return null;
        }

        return code;
    }
}
