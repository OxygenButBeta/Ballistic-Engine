namespace BallisticEngine.AssetPipeline.Loaders;

// .ttf -> FontAsset. Bakes the TrueType file into an SDF FontAtlas (via FontBaker, the only StbTrueType
// user) at the default UI pixel height and wraps it as a loadable BObject. Never throws — a missing or
// bad font logs and returns null, like every other loader (the renderer then skips that text).
//
// v1 bakes at a fixed height; a later importer can read per-font options (size, charset, SDF spread)
// from a .meta sidecar the same way other importers do.
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
