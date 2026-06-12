namespace BallisticEngine.AssetPipeline.Loaders;

// Loads a .baud artifact into an AudioClip (the audio analogue of MeshLoader). No GL/driver work
// here — the backend buffer is created lazily on first Play, so loading a clip is just a PCM blit
// and is safe off any particular thread.
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
