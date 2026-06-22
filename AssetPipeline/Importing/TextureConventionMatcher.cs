namespace BallisticEngine.AssetPipeline;

public static class TextureConventionMatcher {
    static readonly (TextureType Slot, string[] Suffixes)[] SlotSuffixes = [
        (TextureType.Diffuse,   ["albedo", "basecolor", "base_color", "diffuse", "color", "col", "bc"]),
        (TextureType.Normal,    ["normal", "nrm", "norm", "nor", "_n"]),
        (TextureType.Roughness, ["roughness", "rough", "rgh"]),
        (TextureType.Metallic,  ["metalness", "metallic", "metal", "mtl"]),
        (TextureType.AO,        ["ao", "occlusion", "ambientocclusion", "ambient_occlusion"]),
        (TextureType.Emissive,  ["emissive", "emission", "emit"]),
    ];

    static readonly string[] OpacitySuffixes = ["opacity", "alpha", "mask"];

    static readonly string[] GlossSuffixes = ["gloss", "glossiness"];

    static readonly string[] ResolutionTags = ["8k", "4k", "2k", "1k", "512", "1024", "2048", "4096"];

    public sealed class Match {
        public readonly Dictionary<TextureType, string> Textures = new();
        public bool HasOpacity;
        public bool GlossOnly;
    }

    public static Match Find(string directory, string modelStem, Func<string, bool> isSupported) {
        var result = new Match();
        if (!Directory.Exists(directory))
            return result;

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

            var token = MapToken(name, modelStem);

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

    static string MapToken(string fullName, string modelStem) {
        var rest = fullName.Length > modelStem.Length
            ? fullName[modelStem.Length..]
            : fullName;
        rest = rest.TrimStart('_', '-', ' ').ToLowerInvariant();

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
