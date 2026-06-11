namespace BallisticEngine.AssetPipeline.Loaders;

// .bterrain artifact -> TerrainAsset (the editable height field the Terrain component references).
public static class TerrainLoader {
    public static TerrainAsset Load(AssetImportPipeline pipeline, Guid guid, string assetPath) {
        if (!pipeline.TryReadArtifactBytes(guid, out var bytes)) {
            Debugging.LogError($"'{assetPath}' has no Library artifact (import failed or pending?).");
            return null;
        }

        using var stream = new MemoryStream(bytes);
        TerrainData data = TerrainArtifact.Read(stream, assetPath);
        if (!data.IsValid) {
            Debugging.LogError($"'{assetPath}': terrain artifact is invalid.");
            return null;
        }

        return new TerrainAsset(in data) { Name = Path.GetFileNameWithoutExtension(assetPath) };
    }
}
