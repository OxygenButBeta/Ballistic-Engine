namespace BallisticEngine.UI;

public static class UIFonts
{
    static readonly Dictionary<string, FontAtlas> _byName = new(StringComparer.OrdinalIgnoreCase);
    static FontAtlas _default;

    public static int Version { get; private set; }

    public static FontAtlas Default
    {
        get => _default;
        set { _default = value; Version++; }
    }

    public static void Register(string family, FontAtlas atlas)
    {
        if (string.IsNullOrEmpty(family) || atlas == null) return;
        _byName[family] = atlas;
        Version++;
    }

    public static FontAtlas Resolve(string family)
    {
        if (!string.IsNullOrEmpty(family) && _byName.TryGetValue(family, out var atlas))
            return atlas;
        return _default;
    }

    public static FontAtlas Resolve(string family, bool bold, bool italic)
    {
        if (!bold && !italic) return Resolve(family);
        if (!string.IsNullOrEmpty(family))
        {
            string suffix = bold && italic ? "-BoldItalic" : bold ? "-Bold" : "-Italic";
            if (_byName.TryGetValue(family + suffix, out var v)) return v;
            if (_byName.TryGetValue(family, out var b)) return b;
        }
        return _default;
    }

    public static IReadOnlyDictionary<string, FontAtlas> All => _byName;

    static readonly List<FontAtlas> _fallbacks = new();
    public static IReadOnlyList<FontAtlas> Fallbacks => _fallbacks;

    public static void AddFallback(FontAtlas atlas)
    {
        if (atlas != null && !_fallbacks.Contains(atlas)) { _fallbacks.Add(atlas); Version++; }
    }

    public static void ClearFallbacks() { if (_fallbacks.Count > 0) { _fallbacks.Clear(); Version++; } }

    public static FontAtlas AtlasForGlyph(FontAtlas primary, char codepoint)
    {
        if (primary != null && primary.Glyphs.ContainsKey(codepoint)) return primary;
        for (int i = 0; i < _fallbacks.Count; i++)
            if (_fallbacks[i].Glyphs.ContainsKey(codepoint)) return _fallbacks[i];
        return primary;
    }
}
