namespace BallisticEngine;

// The value-kind a shader property holds. Drives both GPU packing (which DrawConstants field /
// SRV slot) and the editor drawer (which inspector widget). Mirrors Unity's ShaderLab property
// kinds (Color/Float/Range/Vector/2D), trimmed to what the Standard shader needs today.
public enum ShaderPropertyType {
    Float,      // single scalar
    Range,      // scalar with [min,max] (slider in the editor)
    Color,      // Vector4 RGBA, shown as a colour swatch
    Vector,     // Vector4, shown as 4 fields
    Texture2D,  // texture asset reference
}

// One DECLARED property of a shader (Unity ShaderLab Properties-block entry). The shader owns the
// declaration (name/type/default/semantic); a Material only stores OVERRIDES of these. This is a
// pure data POCO — zero ImGui, zero file I/O — so the renderer, the editor and the CLI all read it.
//
// Defaults here are the byte-identity anchor: for the Standard shader they MUST equal the field
// defaults on `new Material()`, or an un-overridden material would shade differently after the
// property-bag cutover. See StandardShaderProperties.
public sealed class ShaderProperty {
    // Property identifier (e.g. "_Diffuse", "_BaseColor"). Stable key the material bag stores under.
    public string Name { get; }

    // Human label for the inspector (e.g. "Base Color", "Metallic").
    public string DisplayName { get; }

    public ShaderPropertyType Type { get; }

    // Which fixed engine channel this property feeds (see MaterialSemantic). `None` = no Standard
    // mapping (a future custom-shader-only property).
    public MaterialSemantic Semantic { get; }

    // Typed defaults — only the one matching `Type` is meaningful. A texture default is a path/null.
    public float DefaultFloat { get; }
    public Vector4 DefaultVector { get; }
    public string DefaultTexture { get; }

    // For Range: the slider bounds (inclusive). Null for non-Range.
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
