namespace BallisticEngine.AssetPipeline.Loaders;

public static class TextureLoader {
    public static Texture2D Load(AssetImportPipeline pipeline, Guid guid, string assetPath) {
        if (!pipeline.TryGetArtifactPath(guid, out var artifactPath)) {
            Debugging.LogError($"'{assetPath}' has no Library artifact (import failed or pending?).");
            return null;
        }

        pipeline.TryGetMeta(guid, out MetaFile meta);
        TextureType type = TextureImporter.TypeFromSettings(meta?.Settings);

        TextureData data = TextureArtifact.Read(artifactPath);
        return GraphicAPI.CreateTexture2D(in data, type);
    }
}
