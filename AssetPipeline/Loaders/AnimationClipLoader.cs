namespace BallisticEngine.AssetPipeline.Loaders;

// Loads a .banim into an AnimationClip. The .banim IS the artifact (the ModelImporter writes it as a
// sibling asset, GUID-stamped, like a native asset) — so it reads straight from the project source,
// pack-aware via ContentText, the same way FontLoader reads a .ttf. Pure CPU; the clip is sampled on
// the main thread each frame by the Animator.
public static class AnimationClipLoader {
    public static AnimationClip Load(BallisticProject project, string assetPath) {
        byte[] bytes = ContentText.ReadBytes(project, assetPath);
        if (bytes is null) {
            Debugging.LogError($"'{assetPath}': animation clip not found.");
            return null;
        }

        using var stream = new MemoryStream(bytes);
        AnimationClipData data = AnimationArtifact.Read(stream, assetPath);
        return new AnimationClip(in data, Path.GetFileNameWithoutExtension(assetPath));
    }
}
