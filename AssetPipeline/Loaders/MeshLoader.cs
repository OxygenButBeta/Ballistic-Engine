namespace BallisticEngine.AssetPipeline.Loaders;

public static class MeshLoader {
    public static Mesh Load(AssetImportPipeline pipeline, Guid guid, string assetPath) {
        if (!pipeline.TryGetArtifactPath(guid, out var artifactPath)) {
            Debugging.LogError($"'{assetPath}' has no Library artifact (import failed or pending?).");
            return null;
        }

        MeshData data = MeshArtifact.Read(artifactPath);
        return Mesh.Create(in data);
    }
}
