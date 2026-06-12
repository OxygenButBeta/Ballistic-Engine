namespace BallisticEngine.AssetPipeline;

// Binds PBR textures BY FILENAME CONVENTION when a model's source material references none of
// its own — the Quixel Megascans / textures.com / Substance case: the FBX ships an empty
// "DefaultMaterial" and the maps live as sibling files named "<stem>_4K_Normal.jpg" etc.
// Without this every such pack imports grey. Runs ONLY as a fallback (the model bound nothing),
// so materials that DO carry texture refs (glTF, authored FBX) are never overridden.
//
// Matching is suffix-based and case-insensitive. A file matches a slot when its name (after the
// model stem) ends with one of the slot's suffixes, optionally preceded by a resolution tag
// like "_4K"/"_2K"/"_1K"/"_8K". Highest-priority slot wins a file; within a slot, the first
// suffix in the list wins (so "_BaseColor" beats "_Diffuse" when both somehow exist).
public static class TextureConventionMatcher {
    // Slot -> suffix vocabulary, in priority order. Engine has no sampler for Specular/Cavity/
    // Bump/Displacement/Gloss/Translucency, so those are intentionally ABSENT and stay ignored.
    static readonly (TextureType Slot, string[] Suffixes)[] SlotSuffixes = [
        // Diffuse / albedo / base color — the most-aliased slot across content sources.
        (TextureType.Diffuse,   ["albedo", "basecolor", "base_color", "diffuse", "color", "col", "bc"]),
        (TextureType.Normal,    ["normal", "nrm", "norm", "nor", "_n"]),
        (TextureType.Roughness, ["roughness", "rough", "rgh"]),
        (TextureType.Metallic,  ["metalness", "metallic", "metal", "mtl"]),
        (TextureType.AO,        ["ao", "occlusion", "ambientocclusion", "ambient_occlusion"]),
        (TextureType.Emissive,  ["emissive", "emission", "emit"]),
    ];

    // Opacity/mask maps don't get their own slot (the renderer keys cutout off the diffuse alpha),
    // but their PRESENCE tells us the material is alpha-masked. Reported separately.
    static readonly string[] OpacitySuffixes = ["opacity", "alpha", "mask"];

    // Roughness vs gloss: gloss is inverted roughness and the loader has no invert path, so we
    // only ever bind roughness. Gloss-only files are reported so the caller can warn.
    static readonly string[] GlossSuffixes = ["gloss", "glossiness"];

    // Resolution tags Megascans/others insert between the stem and the map name ("_4K_Normal").
    static readonly string[] ResolutionTags = ["8k", "4k", "2k", "1k", "512", "1024", "2048", "4096"];

    public sealed class Match {
        public readonly Dictionary<TextureType, string> Textures = new(); // slot -> absolute path
        public bool HasOpacity;     // an opacity/mask map exists -> material is alpha-masked
        public bool GlossOnly;      // gloss present but no roughness -> roughness left unbound
    }

    // Scans `directory` for files sharing `modelStem` and matches them to slots. Only files whose
    // name STARTS with the model stem are considered, so two props sharing a folder don't cross-bind.
    // `isSupported` gates on the texture importer's accepted extensions.
    public static Match Find(string directory, string modelStem, Func<string, bool> isSupported) {
        var result = new Match();
        if (!Directory.Exists(directory))
            return result;

        // Best (lowest) priority index claimed per slot, so a later weaker suffix can't overwrite.
        var claimedPriority = new Dictionary<TextureType, int>();
        var sawRoughness = false;
        var sawGloss = false;

        string[] files;
        try {
            files = Directory.GetFiles(directory);
        }
        catch (Exception exception) {
            Debugging.LogWarning($"Texture convention scan of '{directory}' failed: {exception.Message}");
            return result;
        }

        foreach (var file in files) {
            var name = Path.GetFileNameWithoutExtension(file);
            if (name is null || !name.StartsWith(modelStem, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!isSupported(Path.GetExtension(file).ToLowerInvariant()))
                continue;

            var token = MapToken(name, modelStem); // the part after stem + resolution tag, lowercased

            if (MatchesAny(token, OpacitySuffixes)) {
                result.HasOpacity = true;
                continue;
            }
            if (MatchesAny(token, GlossSuffixes)) {
                sawGloss = true;
                continue;
            }

            for (var priority = 0; priority < SlotSuffixes.Length; priority++) {
                (TextureType slot, string[] suffixes) = SlotSuffixes[priority];
                if (!MatchesAny(token, suffixes))
                    continue;

                if (slot == TextureType.Roughness)
                    sawRoughness = true;

                // Lower priority index wins the slot.
                if (claimedPriority.TryGetValue(slot, out var existing) && existing <= priority)
                    break;

                result.Textures[slot] = Path.GetFullPath(file);
                claimedPriority[slot] = priority;
                break;
            }
        }

        result.GlossOnly = sawGloss && !sawRoughness;
        return result;
    }

    // Strips the model stem and any leading resolution tag, returning the slot token for matching.
    // "tl0_4K_BaseColor" with stem "tl0" -> "basecolor"; "rock_Normal" -> "normal".
    static string MapToken(string fullName, string modelStem) {
        var rest = fullName.Length > modelStem.Length
            ? fullName[modelStem.Length..]
            : fullName;
        rest = rest.TrimStart('_', '-', ' ').ToLowerInvariant();

        // Drop a leading resolution tag ("4k_normal" -> "normal").
        foreach (var tag in ResolutionTags) {
            if (rest.StartsWith(tag, StringComparison.Ordinal)) {
                var after = rest[tag.Length..].TrimStart('_', '-', ' ');
                if (after.Length > 0) {
                    rest = after;
                    break;
                }
            }
        }
        return rest;
    }

    // True when the token IS a suffix or ends with it after a separator ("rock_basecolor" -> "basecolor").
    static bool MatchesAny(string token, string[] suffixes) {
        foreach (var suffix in suffixes) {
            if (token.Equals(suffix, StringComparison.Ordinal))
                return true;
            if (token.EndsWith(suffix, StringComparison.Ordinal)) {
                var boundary = token[token.Length - suffix.Length - 1];
                if (boundary is '_' or '-' or ' ')
                    return true;
            }
        }
        return false;
    }
}
