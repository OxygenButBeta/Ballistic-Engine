using System;
using System.Collections.Generic;

namespace BallisticEngine.UI;

// Registry of named UI fonts as CPU FontAtlases (no GL). EngineBootstrap bakes/registers fonts here;
// the GL UI pass reads them and uploads textures on demand. Keeps the UI layer free of GL and the
// font baker — atlases are plain CPU data flowing through untouched.
//
// "Default" is the fallback used when an element specifies no font (or an unknown one). Register
// additional fonts by family name (matching CSS font-family) so a ported design's `font-family:
// 'Cinzel'` resolves to the right atlas. Version bumps on any change so the renderer re-uploads.
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

    // Registers a font under a family name (CSS font-family). Case-insensitive. Overwrites an existing
    // entry of the same name. A null atlas is ignored.
    public static void Register(string family, FontAtlas atlas)
    {
        if (string.IsNullOrEmpty(family) || atlas == null) return;
        _byName[family] = atlas;
        Version++;
    }

    // Resolves a family name to its atlas, falling back to Default when unknown/empty. Never throws.
    public static FontAtlas Resolve(string family)
    {
        if (!string.IsNullOrEmpty(family) && _byName.TryGetValue(family, out var atlas))
            return atlas;
        return _default;
    }

    // Resolves a family + weight/style to a variant atlas by name convention (P6.4): "Inter" + bold ->
    // "Inter-Bold", + italic -> "Inter-Italic", + both -> "Inter-BoldItalic". Falls back to the plain
    // family, then Default, if a variant isn't registered — so bold text degrades to regular, never blank.
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

    // Font fallback chain (P9.1): atlases tried IN ORDER when the primary font lacks a glyph (CJK, emoji,
    // symbols). Register e.g. an emoji atlas + a CJK atlas here; text rendering picks the first atlas that
    // has each codepoint, so mixed-script strings render instead of showing tofu boxes.
    static readonly List<FontAtlas> _fallbacks = new();
    public static IReadOnlyList<FontAtlas> Fallbacks => _fallbacks;

    public static void AddFallback(FontAtlas atlas)
    {
        if (atlas != null && !_fallbacks.Contains(atlas)) { _fallbacks.Add(atlas); Version++; }
    }

    public static void ClearFallbacks() { if (_fallbacks.Count > 0) { _fallbacks.Clear(); Version++; } }

    // Resolve which atlas should render `codepoint` for a primary atlas: the primary if it has the glyph,
    // else the first fallback that does, else the primary (renders its .notdef/skips).
    public static FontAtlas AtlasForGlyph(FontAtlas primary, char codepoint)
    {
        if (primary != null && primary.Glyphs.ContainsKey(codepoint)) return primary;
        for (int i = 0; i < _fallbacks.Count; i++)
            if (_fallbacks[i].Glyphs.ContainsKey(codepoint)) return _fallbacks[i];
        return primary;
    }
}
