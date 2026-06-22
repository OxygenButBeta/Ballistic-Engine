namespace BallisticEngine.AssetPipeline.Loaders;

public sealed class CubemapDefinition {
    public int Version { get; set; } = 1;
    public Dictionary<string, string> Faces { get; set; } = new();
    public string Equirect { get; set; }
    public int FaceSize { get; set; } = 512;
}

public static class CubemapLoader {
    static readonly string[] FaceOrder = ["right", "left", "top", "bottom", "front", "back"];

    public static Texture3D Load(BallisticProject project, AssetImportPipeline pipeline, string assetPath) {
        var definition = ContentText.ReadJson<CubemapDefinition>(project, assetPath);
        if (definition is null) {
            Debugging.LogError($"'{assetPath}': cubemap definition not found.");
            return null;
        }

        if (!string.IsNullOrEmpty(definition.Equirect))
            return LoadEquirect(definition, pipeline, assetPath);

        var faces = new TextureData[6];
        for (var i = 0; i < FaceOrder.Length; i++) {
            if (definition.Faces is null || !definition.Faces.TryGetValue(FaceOrder[i], out var reference)) {
                Debugging.LogError($"'{assetPath}': missing '{FaceOrder[i]}' face.");
                return null;
            }

            Guid guid;
            if (!AssetRef.IsGuidRef(reference, out guid) && !AssetDatabase.TryGetGuid(reference, out guid)) {
                Debugging.LogError($"'{assetPath}': face '{FaceOrder[i]}' reference '{reference}' does not resolve.");
                return null;
            }

            if (!pipeline.TryReadArtifactBytes(guid, out var bytes)) {
                Debugging.LogError($"'{assetPath}': face '{reference}' has no Library artifact.");
                return null;
            }

            using var stream = new MemoryStream(bytes);
            faces[i] = TextureArtifact.Read(stream, reference);
        }

        return GraphicAPI.CreateCubemap(faces);
    }

    static Texture3D LoadEquirect(CubemapDefinition definition, AssetImportPipeline pipeline, string assetPath) {
        Guid guid;
        if (!AssetRef.IsGuidRef(definition.Equirect, out guid) &&
            !AssetDatabase.TryGetGuid(definition.Equirect, out guid)) {
            Debugging.LogError($"'{assetPath}': equirect reference '{definition.Equirect}' does not resolve.");
            return null;
        }

        if (!pipeline.TryReadArtifactBytes(guid, out var bytes)) {
            Debugging.LogError($"'{assetPath}': equirect '{definition.Equirect}' has no Library artifact.");
            return null;
        }

        using var stream = new MemoryStream(bytes);
        TextureData panorama = TextureArtifact.Read(stream, definition.Equirect);
        TextureData[] faces = EquirectToCubemap.Convert(in panorama, definition.FaceSize);
        return GraphicAPI.CreateCubemap(faces);
    }
}
