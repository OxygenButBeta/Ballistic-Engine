using System.Globalization;
using System.Text.RegularExpressions;

namespace BallisticEngine.AssetPipeline;

public sealed class FalcorSceneData {
    public FalcorCamera Camera { get; set; }
    public List<FalcorLight> Lights { get; } = new();
    public List<string> ModelPaths { get; } = new();
    public string EnvMapPath { get; set; }
}

public sealed class FalcorCamera {
    public Vector3 Position = new(0, 1, -5);
    public Vector3 Target = Vector3.Zero;
    public float FovYDegrees = 45f;
}

public sealed class FalcorLight {
    public Vector3 Direction = new(0, -1, 0);
    public Vector3 Color = Vector3.One;
    public float Intensity = 1f;
}

public static class FalcorSceneParser {
    public static FalcorSceneData Parse(string source) {
        var data = new FalcorSceneData();

        source = Regex.Replace(source, @"#.*", "");

        ParseCamera(source, data);
        ParseLights(source, data);
        ParseModels(source, data);
        ParseEnvMap(source, data);

        return data;
    }

    static void ParseCamera(string source, FalcorSceneData data) {
        Vector3? pos = FindFloat3(source, @"\.position\s*=\s*float3\(([^)]*)\)");
        Vector3? target = FindFloat3(source, @"\.target\s*=\s*float3\(([^)]*)\)");
        if (pos is null && target is null && !source.Contains("Camera("))
            return;

        var cam = new FalcorCamera();
        if (pos.HasValue) cam.Position = pos.Value;
        if (target.HasValue) cam.Target = target.Value;

        Match fov = Regex.Match(source, @"\.fov(Y)?\s*=\s*([-\d.eE]+)");
        if (fov.Success && float.TryParse(fov.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var f))
            cam.FovYDegrees = f;

        data.Camera = cam;
    }

    static void ParseLights(string source, FalcorSceneData data) {
        foreach (Match m in Regex.Matches(source, @"DirectionalLight\s*\(", RegexOptions.IgnoreCase)) {
            int start = m.Index;
            int end = Math.Min(source.Length, start + 400);
            string window = source[start..end];

            var light = new FalcorLight();
            Vector3? dir = FindFloat3(window, @"\.direction\s*=\s*float3\(([^)]*)\)");
            if (dir.HasValue) light.Direction = dir.Value;

            Vector3? intensity3 = FindFloat3(window, @"\.intensity\s*=\s*float3\(([^)]*)\)");
            if (intensity3.HasValue) {
                light.Color = Normalize(intensity3.Value, out float mag);
                light.Intensity = mag;
            }
            else {
                Match scalar = Regex.Match(window, @"\.intensity\s*=\s*([-\d.eE]+)");
                if (scalar.Success && float.TryParse(scalar.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var s))
                    light.Intensity = s;
            }

            data.Lights.Add(light);
        }
    }

    static void ParseModels(string source, FalcorSceneData data) {
        foreach (Match m in Regex.Matches(source,
                     @"(?:importScene|importGLTF|importOBJ|addModel|loadMesh|import)\s*\(\s*[""']([^""']+)[""']",
                     RegexOptions.IgnoreCase)) {
            var path = m.Groups[1].Value.Trim();
            if (path.Length > 0 && !data.ModelPaths.Contains(path))
                data.ModelPaths.Add(path);
        }
    }

    static void ParseEnvMap(string source, FalcorSceneData data) {
        Match m = Regex.Match(source, @"(?:EnvMap\.createFromFile|envMap|loadEnvMap)\s*\(\s*[""']([^""']+)[""']",
            RegexOptions.IgnoreCase);
        if (m.Success)
            data.EnvMapPath = m.Groups[1].Value.Trim();
    }

    static Vector3? FindFloat3(string source, string pattern) {
        Match m = Regex.Match(source, pattern);
        if (!m.Success)
            return null;

        var parts = m.Groups[1].Value.Split(',');
        if (parts.Length < 3)
            return null;

        if (TryFloat(parts[0], out var x) && TryFloat(parts[1], out var y) && TryFloat(parts[2], out var z))
            return new Vector3(x, y, z);
        return null;
    }

    static bool TryFloat(string s, out float value) =>
        float.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    static Vector3 Normalize(Vector3 v, out float magnitude) {
        magnitude = v.Length();
        return magnitude > 1e-5f ? v / magnitude : Vector3.One;
    }
}
