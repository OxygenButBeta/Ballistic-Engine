using System.Globalization;

namespace BallisticEngine.AssetPipeline.Unity;

public static class UnityMaterialParser {
    static readonly string[] DiffuseSlots =
        ["_albedo", "_basecolormap", "_maintex", "_basemap", "_baselayeralbedomap", "_color"];
    static readonly string[] NormalSlots =
        ["_normal", "_normalmap", "_bumpmap", "_baselayernormalmap"];

    static readonly string[] PackedMaskSlots =
        ["_maskmap", "_dr", "_dro", "_ordp", "_ord", "_baselayerordmap", "_metallicglossmap", "_mr"];
    static readonly string[] OcclusionSlots = ["_occlusionmap", "_ao"];

    public static UnityMaterialData Parse(string text) {
        var data = new UnityMaterialData();
        string[] lines = text.Replace("\r\n", "\n").Split('\n');

        for (var i = 0; i < lines.Length; i++) {
            var trimmed = lines[i].Trim();
            if (trimmed.Length < 2 || trimmed[0] != '-' || !trimmed.Contains(':'))
                continue;

            var slotPart = trimmed[1..].Trim();
            if (!slotPart.StartsWith('_'))
                continue;
            var colon = slotPart.IndexOf(':');
            if (colon < 0)
                continue;
            var slot = slotPart[..colon].Trim().ToLowerInvariant();

            if (i + 1 >= lines.Length)
                continue;
            var next = lines[i + 1].Trim();
            if (!next.StartsWith("m_Texture:", StringComparison.Ordinal))
                continue;
            var guid = ExtractGuid(next);
            if (guid is null)
                continue;

            if (data.DiffuseGuid is null && Matches(slot, DiffuseSlots)) data.DiffuseGuid = guid;
            else if (data.NormalGuid is null && Matches(slot, NormalSlots)) data.NormalGuid = guid;
            else if (data.MaskGuid is null && Matches(slot, PackedMaskSlots)) { data.MaskGuid = guid; data.MaskIsPacked = true; }
            else if (data.OcclusionGuid is null && Matches(slot, OcclusionSlots)) data.OcclusionGuid = guid;
        }

        foreach (var raw in lines) {
            var t = raw.Trim();
            if (t.StartsWith("- _Metallic:", StringComparison.Ordinal))
                data.Metallic = ParseFloat(t["- _Metallic:".Length..]);
            else if (t.StartsWith("- _Smoothness:", StringComparison.Ordinal))
                data.Smoothness = ParseFloat(t["- _Smoothness:".Length..]);
            else if (t.StartsWith("- _AlphaCutoffEnable: 1", StringComparison.Ordinal) ||
                     t.StartsWith("- _Mode: 1", StringComparison.Ordinal))
                data.AlphaCutout = true;
            else if (data.BaseColor is null &&
                     (t.StartsWith("- _BaseColor:", StringComparison.Ordinal) ||
                      t.StartsWith("- _Color:", StringComparison.Ordinal)))
                data.BaseColor = ParseColor(t);
        }

        return data;
    }

    static bool Matches(string slot, string[] names) {
        foreach (var n in names)
            if (slot == n)
                return true;
        return false;
    }

    static string ExtractGuid(string line) {
        var idx = line.IndexOf("guid:", StringComparison.Ordinal);
        if (idx < 0)
            return null;
        var rest = line[(idx + 5)..].Trim();
        var end = rest.IndexOfAny([',', '}', ' ']);
        var guid = (end < 0 ? rest : rest[..end]).Trim();
        return UnityMetaGuidMap.IsHexGuid(guid) ? guid : null;
    }

    static float? ParseFloat(string s) =>
        float.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : null;

    static float[] ParseColor(string line) {
        var brace = line.IndexOf('{');
        var end = brace >= 0 ? line.IndexOf('}', brace) : -1;
        if (brace < 0 || end < 0)
            return null;
        float r = 1, g = 1, b = 1, a = 1;
        foreach (var part in line[(brace + 1)..end].Split(',')) {
            var kv = part.Split(':', 2);
            if (kv.Length != 2) continue;
            float v = ParseFloat(kv[1]) ?? 1f;
            switch (kv[0].Trim()) {
                case "r": r = v; break;
                case "g": g = v; break;
                case "b": b = v; break;
                case "a": a = v; break;
            }
        }
        return [r, g, b, a];
    }
}
