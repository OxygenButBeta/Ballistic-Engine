using System.Globalization;

namespace BallisticEngine.AssetPipeline.Unity;

// Parses a Unity .mat (YAML) into the texture-slot guids + scalar factors we can map to an engine
// material. Unity materials bind textures by name in m_SavedProperties.m_TexEnvs (each "- _Slot:\n
// m_Texture: {fileID, guid}") and scalars in m_Floats / m_Colors. Slot names vary wildly across
// pipelines (Standard, URP/Lit, HDRP/Lit, Megascans shadergraphs), so we match by KNOWN slot names
// and fall back to nothing (the caller can still try filename convention on the model).
public sealed class UnityMaterialData {
    public string DiffuseGuid;
    public string NormalGuid;
    public string MaskGuid;       // packed map (metallic/AO/roughness) — engine reads as packed ORM
    public string OcclusionGuid;
    public bool MaskIsPacked;     // the mask/metallic slot is a packed ORD/ORM map, not a plain metallic

    public float[] BaseColor;     // linear RGBA, if stated
    public float? Metallic;
    public float? Smoothness;     // Unity smoothness = 1 - roughness
    public bool AlphaCutout;      // HDRP _AlphaCutoffEnable / legacy cutout — foliage cards etc.

    public bool HasAnyTexture => DiffuseGuid is not null || NormalGuid is not null
        || MaskGuid is not null || OcclusionGuid is not null;
}

public static class UnityMaterialParser {
    // Slot name -> logical channel. Lowercased compare. Diffuse/normal/mask each list the common
    // Standard/URP/HDRP/Megascans names. Packed slots (ORD/ORM/MaskMap/_DR) are flagged packed.
    static readonly string[] DiffuseSlots =
        ["_albedo", "_basecolormap", "_maintex", "_basemap", "_baselayeralbedomap", "_color"];
    static readonly string[] NormalSlots =
        ["_normal", "_normalmap", "_bumpmap", "_baselayernormalmap"];
    // Packed mask maps (metallic/AO/roughness/detail packed into channels).
    static readonly string[] PackedMaskSlots =
        ["_maskmap", "_dr", "_dro", "_ordp", "_ord", "_baselayerordmap", "_metallicglossmap", "_mr"];
    static readonly string[] OcclusionSlots = ["_occlusionmap", "_ao"];

    public static UnityMaterialData Parse(string text) {
        var data = new UnityMaterialData();
        string[] lines = text.Replace("\r\n", "\n").Split('\n');

        // m_TexEnvs entries look like:
        //   - _BaseColorMap:
        //       m_Texture: {fileID: 2800000, guid: abc..., type: 3}
        // The slot name is on one line; the guid on the next "m_Texture:" line.
        for (var i = 0; i < lines.Length; i++) {
            var trimmed = lines[i].Trim();
            if (trimmed.Length < 2 || trimmed[0] != '-' || !trimmed.Contains(':'))
                continue;

            // "- _Slot:" — extract slot name (strip leading "- " and trailing ":").
            var slotPart = trimmed[1..].Trim();
            if (!slotPart.StartsWith('_'))
                continue;
            var colon = slotPart.IndexOf(':');
            if (colon < 0)
                continue;
            var slot = slotPart[..colon].Trim().ToLowerInvariant();

            // The texture guid is on the following "m_Texture:" line.
            if (i + 1 >= lines.Length)
                continue;
            var next = lines[i + 1].Trim();
            if (!next.StartsWith("m_Texture:", StringComparison.Ordinal))
                continue;
            var guid = ExtractGuid(next);
            if (guid is null)
                continue; // no texture bound to this slot

            if (data.DiffuseGuid is null && Matches(slot, DiffuseSlots)) data.DiffuseGuid = guid;
            else if (data.NormalGuid is null && Matches(slot, NormalSlots)) data.NormalGuid = guid;
            else if (data.MaskGuid is null && Matches(slot, PackedMaskSlots)) { data.MaskGuid = guid; data.MaskIsPacked = true; }
            else if (data.OcclusionGuid is null && Matches(slot, OcclusionSlots)) data.OcclusionGuid = guid;
        }

        // Scalars: m_Floats has "- _Metallic: 0.5", "- _Smoothness: 0.7"; m_Colors has "- _Color: {r,g,b,a}".
        foreach (var raw in lines) {
            var t = raw.Trim();
            if (t.StartsWith("- _Metallic:", StringComparison.Ordinal))
                data.Metallic = ParseFloat(t["- _Metallic:".Length..]);
            else if (t.StartsWith("- _Smoothness:", StringComparison.Ordinal))
                data.Smoothness = ParseFloat(t["- _Smoothness:".Length..]);
            // Alpha-cutout (foliage/leaf cards): HDRP "_AlphaCutoffEnable: 1" or the legacy Standard
            // cutout rendering mode "_Mode: 1". Without this, leaf quads render as opaque rectangles.
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

    // "- _Color: {r: 1, g: 0.5, b: 0.2, a: 1}"
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
