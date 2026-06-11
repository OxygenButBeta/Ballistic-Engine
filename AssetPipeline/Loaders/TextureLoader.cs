namespace BallisticEngine.AssetPipeline.Loaders;

public static class TextureLoader {
    public static Texture2D Load(AssetImportPipeline pipeline, Guid guid, string assetPath) {
        pipeline.TryGetMeta(guid, out MetaFile meta);
        TextureType type = TextureImporter.TypeFromSettings(meta?.Settings);

        // Warm data from the scene prefetcher (decoded + inflated on a worker) — only GL upload here.
        if (AssetDataCache.TryTakeTexture(guid, out TextureData prefetched))
            return GraphicAPI.CreateTexture2D(in prefetched, type);

        if (!pipeline.TryReadArtifactBytes(guid, out var bytes)) {
            Debugging.LogError($"'{assetPath}' has no Library artifact (import failed or pending?).");
            return null;
        }

        using var stream = new MemoryStream(bytes);
        TextureData data = TextureArtifact.Read(stream, assetPath);
        return GraphicAPI.CreateTexture2D(in data, type);
    }

    // CPU-only decode (file read + Deflate inflate) for the prefetcher. Returns false on failure.
    public static bool TryDecode(AssetImportPipeline pipeline, Guid guid, out TextureData data) {
        data = default;
        if (!pipeline.TryReadArtifactBytes(guid, out var bytes))
            return false;

        using var stream = new MemoryStream(bytes);
        data = TextureArtifact.Read(stream);
        return true;
    }
}
