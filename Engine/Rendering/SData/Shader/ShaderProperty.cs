namespace BallisticEngine;

public sealed class ShaderProperty {
    public string Name { get; }

    public string DisplayName { get; }

    public ShaderPropertyType Type { get; }

    public MaterialSemantic Semantic { get; }

    public float DefaultFloat { get; }
    public Vector4 DefaultVector { get; }
    public string DefaultTexture { get; }

    public (float Min, float Max)? Range { get; }

    ShaderProperty(string name, string displayName, ShaderPropertyType type, MaterialSemantic semantic,
        float defaultFloat, Vector4 defaultVector, string defaultTexture, (float, float)? range) {
        Name = name;
        DisplayName = displayName;
        Type = type;
        Semantic = semantic;
        DefaultFloat = defaultFloat;
        DefaultVector = defaultVector;
        DefaultTexture = defaultTexture;
        Range = range;
    }

    public static ShaderProperty Texture(string name, string displayName, MaterialSemantic semantic,
        string defaultTexture = null) =>
        new(name, displayName, ShaderPropertyType.Texture2D, semantic, 0f, default, defaultTexture, null);

    public static ShaderProperty FloatProp(string name, string displayName, MaterialSemantic semantic,
        float defaultValue) =>
        new(name, displayName, ShaderPropertyType.Float, semantic, defaultValue, default, null, null);

    public static ShaderProperty RangeProp(string name, string displayName, MaterialSemantic semantic,
        float defaultValue, float min, float max) =>
        new(name, displayName, ShaderPropertyType.Range, semantic, defaultValue, default, null, (min, max));

    public static ShaderProperty ColorProp(string name, string displayName, MaterialSemantic semantic,
        Vector4 defaultValue) =>
        new(name, displayName, ShaderPropertyType.Color, semantic, 0f, defaultValue, null, null);

    public static ShaderProperty VectorProp(string name, string displayName, MaterialSemantic semantic,
        Vector4 defaultValue) =>
        new(name, displayName, ShaderPropertyType.Vector, semantic, 0f, defaultValue, null, null);
}
