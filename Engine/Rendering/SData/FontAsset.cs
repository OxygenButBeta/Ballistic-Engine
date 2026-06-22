namespace BallisticEngine;

public sealed class FontAsset : BObject
{
    public FontAtlas Atlas { get; }

    public FontAsset(FontAtlas atlas, string name)
    {
        Atlas = atlas;
        Name = name;
    }
}
