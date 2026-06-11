namespace BallisticEngine;

// A loadable font asset: a BObject wrapper around a baked FontAtlas, so a .ttf under Assets/ resolves
// through AssetDatabase.Load<FontAsset>("Assets/...ttf") like any other asset. The atlas is CPU data
// (no GL); the UI renderer uploads it. v1 bakes at a fixed pixel height — a future importer can carry
// per-font settings (size, charset) in a .meta sidecar.
public sealed class FontAsset : BObject
{
    public FontAtlas Atlas { get; }

    public FontAsset(FontAtlas atlas, string name)
    {
        Atlas = atlas;
        Name = name;
    }
}
