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

    public static IReadOnlyDictionary<string, FontAtlas> All => _byName;
}
