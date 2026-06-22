namespace BallisticEngine.AssetPipeline.Loaders;

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
