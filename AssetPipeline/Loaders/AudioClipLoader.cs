namespace BallisticEngine.AssetPipeline.Loaders;

public static class AudioClipLoader {
    public static AudioClip Load(AssetImportPipeline pipeline, Guid guid, string assetPath) {
        if (!pipeline.TryReadArtifactBytes(guid, out var bytes)) {
            Debugging.LogError($"'{assetPath}' has no Library artifact (import failed or pending?).");
            return null;
        }

        using var stream = new MemoryStream(bytes);
        AudioData data = AudioArtifact.Read(stream, assetPath);
        return new AudioClip(in data, Path.GetFileNameWithoutExtension(assetPath));
    }
}
