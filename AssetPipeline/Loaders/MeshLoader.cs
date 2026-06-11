namespace BallisticEngine.AssetPipeline.Loaders;

public static class MeshLoader {
    public static Mesh Load(AssetImportPipeline pipeline, Guid guid, string assetPath) {
        // Warm data from the scene prefetcher (decoded on a worker) — take it and only do GL upload.
        if (AssetDataCache.TryTakeMesh(guid, out MeshData prefetched))
            return Mesh.Create(in prefetched);

        // Pack-aware: bytes come from a mounted content pack (shipped player) or the loose file.
        if (!pipeline.TryReadArtifactBytes(guid, out var bytes)) {
            Debugging.LogError($"'{assetPath}' has no Library artifact (import failed or pending?).");
            return null;
        }

        using var stream = new MemoryStream(bytes);
        MeshData data = MeshArtifact.Read(stream, assetPath);
        return Mesh.Create(in data);
    }

    // CPU-only decode for the prefetcher (safe off the GL thread). Returns false on any failure.
    public static bool TryDecode(AssetImportPipeline pipeline, Guid guid, out MeshData data) {
        data = default;
        if (!pipeline.TryReadArtifactBytes(guid, out var bytes))
            return false;

        using var stream = new MemoryStream(bytes);
        data = MeshArtifact.Read(stream);
        return true;
    }
}
