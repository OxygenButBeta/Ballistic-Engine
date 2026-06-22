namespace BallisticEngine.AssetPipeline.Loaders;

public sealed class ShaderPropertyDef {
    public string Name { get; set; }
    public string Display { get; set; }
    public string Type { get; set; }
    public string Semantic { get; set; }
    public System.Text.Json.JsonElement? Default { get; set; }
    public float[] Range { get; set; }

    public ShaderProperty ToShaderProperty(string ownerPath) {
        var name = Name ?? "_Unnamed";
        var display = Display ?? name.TrimStart('_');
        var semantic = System.Enum.TryParse<MaterialSemantic>(Semantic, ignoreCase: true, out var s)
            ? s : MaterialSemantic.None;
        if (!System.Enum.TryParse<ShaderPropertyType>(Type, ignoreCase: true, out var type)) {
            Debugging.LogWarning($"'{ownerPath}': property '{name}' has unknown type '{Type}'; treating as Float.");
            type = ShaderPropertyType.Float;
        }

        switch (type) {
            case ShaderPropertyType.Texture2D:
                return ShaderProperty.Texture(name, display, semantic,
                    Default is { ValueKind: System.Text.Json.JsonValueKind.String } e ? e.GetString() : null);
            case ShaderPropertyType.Color:
                return ShaderProperty.ColorProp(name, display, semantic, ReadVector(Vector4.One));
            case ShaderPropertyType.Vector:
                return ShaderProperty.VectorProp(name, display, semantic, ReadVector(default));
            case ShaderPropertyType.Range: {
                float min = Range is { Length: >= 2 } ? Range[0] : 0f;
                float max = Range is { Length: >= 2 } ? Range[1] : 1f;
                return ShaderProperty.RangeProp(name, display, semantic, ReadFloat(min), min, max);
            }
            default:
                return ShaderProperty.FloatProp(name, display, semantic, ReadFloat(0f));
        }
    }

    float ReadFloat(float fallback) =>
        Default is { ValueKind: System.Text.Json.JsonValueKind.Number } e ? e.GetSingle() : fallback;

    Vector4 ReadVector(Vector4 fallback) {
        if (Default is not { ValueKind: System.Text.Json.JsonValueKind.Array } e) return fallback;
        var v = fallback;
        int i = 0;
        foreach (var el in e.EnumerateArray()) {
            float f = el.GetSingle();
            switch (i++) { case 0: v.X = f; break; case 1: v.Y = f; break; case 2: v.Z = f; break; case 3: v.W = f; break; }
            if (i >= 4) break;
        }
        return v;
    }
}
