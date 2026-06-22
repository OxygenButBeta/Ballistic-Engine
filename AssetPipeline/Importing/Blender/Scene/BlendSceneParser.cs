using System.Text.Json;

namespace BallisticEngine.AssetPipeline;

public static class BlendSceneParser {
    public static float[] Identity4() => [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];

    public static BlendSceneData Parse(string json) {
        var data = new BlendSceneData();
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        data.HasMesh = GetBool(root, "hasMesh", false);

        if (root.TryGetProperty("meshes", out JsonElement meshes) && meshes.ValueKind == JsonValueKind.Array) {
            foreach (JsonElement m in meshes.EnumerateArray())
                data.Meshes.Add(new BlendMesh {
                    Name = GetString(m, "name", "Mesh"),
                    Matrix = GetMatrix(m, "matrix"),
                });
        }

        if (root.TryGetProperty("cameras", out JsonElement cameras) && cameras.ValueKind == JsonValueKind.Array) {
            foreach (JsonElement c in cameras.EnumerateArray())
                data.Cameras.Add(new BlendCamera {
                    Name = GetString(c, "name", "Camera"),
                    Matrix = GetMatrix(c, "matrix"),
                    FovY = GetFloat(c, "fovY", 0.69f),
                    Near = GetFloat(c, "near", 0.1f),
                    Far = GetFloat(c, "far", 1000f),
                    IsActive = GetBool(c, "isActive", true),
                });
        }

        if (root.TryGetProperty("lights", out JsonElement lights) && lights.ValueKind == JsonValueKind.Array) {
            foreach (JsonElement l in lights.EnumerateArray())
                data.Lights.Add(new BlendLight {
                    Name = GetString(l, "name", "Light"),
                    Matrix = GetMatrix(l, "matrix"),
                    LightType = GetString(l, "lightType", "POINT").ToUpperInvariant(),
                    Color = GetColor(l, "color"),
                    Energy = GetFloat(l, "energy", 1000f),
                    Range = GetFloat(l, "range", 0f),
                    SpotSize = GetFloat(l, "spotSize", 1.2f),
                    SpotBlend = GetFloat(l, "spotBlend", 0.15f),
                });
        }

        return data;
    }

    static string GetString(JsonElement e, string name, string fallback) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : fallback;

    static bool GetBool(JsonElement e, string name, bool fallback) =>
        e.TryGetProperty(name, out JsonElement v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)
            ? v.GetBoolean() : fallback;

    static float GetFloat(JsonElement e, string name, float fallback) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetSingle() : fallback;

    static float[] GetColor(JsonElement e, string name) {
        if (!e.TryGetProperty(name, out JsonElement v) || v.ValueKind != JsonValueKind.Array)
            return [1f, 1f, 1f];
        var list = new List<float>(3);
        foreach (JsonElement n in v.EnumerateArray())
            if (n.ValueKind == JsonValueKind.Number)
                list.Add(n.GetSingle());
        return list.Count >= 3 ? [list[0], list[1], list[2]] : [1f, 1f, 1f];
    }

    static float[] GetMatrix(JsonElement e, string name) {
        if (!e.TryGetProperty(name, out JsonElement v) || v.ValueKind != JsonValueKind.Array)
            return Identity4();
        var list = new List<float>(16);
        foreach (JsonElement n in v.EnumerateArray())
            if (n.ValueKind == JsonValueKind.Number)
                list.Add(n.GetSingle());
        return list.Count == 16 ? list.ToArray() : Identity4();
    }
}
