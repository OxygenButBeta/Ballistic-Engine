namespace BallisticEngine.AssetPipeline.Loaders;

public static class FontLoader
{
    const float DefaultPixelHeight = 48f;

    public static FontAsset Load(BallisticProject project, string assetPath)
    {
        var absolute = project.ResolveAbsolute(assetPath);
        var atlas = FontBaker.Bake(absolute, DefaultPixelHeight);
        if (atlas is null)
        {
            Debugging.LogError($"'{assetPath}': failed to bake font.");
            return null;
        }
        return new FontAsset(atlas, System.IO.Path.GetFileNameWithoutExtension(assetPath));
    }
}
