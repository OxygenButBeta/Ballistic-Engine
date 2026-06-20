namespace BallisticEngine.AssetPipeline.Loaders;

// .shader asset: { "version": 1, "vertex": "<.glsl ref>", "fragment": "<.glsl ref>",
//   "properties": [ { "name": "_BaseColor", "display": "Base Color", "type": "Color",
//                     "semantic": "BaseColorFactor", "default": [1,1,1,1] }, ... ] }
// `properties` is OPTIONAL: omit it and the shader uses the built-in Standard PBR declaration
// (StandardShaderProperties). A custom shader declares its own block — this is the seam a future
// full-fragment-control shader plugs its authorable inputs into (semantic "None" = no Standard
// channel, fed to a generic CB the custom shader owns).
public sealed class ShaderDefinition {
    public int Version { get; set; } = 1;
    public string Vertex { get; set; }
    public string Fragment { get; set; }
    public ShaderPropertyDef[] Properties { get; set; }

    // OPTIONAL custom surface shader. A ref to a .hlsl/.surface asset containing a Unity-style
    // `SurfaceOutput Surface(SurfaceInput i)` body. When present, materials using this shader render with
    // a per-material PSO built from this body + the engine's G-buffer skeleton (z-prepass/MRT contract
    // stays engine-owned). Omit it → the Standard PBR shader (unchanged, byte-identical).
    public string Surface { get; set; }
}

// JSON-serializable form of one declared property (the .shader Properties-block entry). Converted to
// the engine's immutable ShaderProperty by ToShaderProperty. `default` is a number for Float/Range or
// a [r,g,b(,a)] array for Color/Vector or a texture ref string for Texture2D.
public sealed class ShaderPropertyDef {
    public string Name { get; set; }
    public string Display { get; set; }
    public string Type { get; set; }       // ShaderPropertyType name
    public string Semantic { get; set; }   // MaterialSemantic name (default None)
    public System.Text.Json.JsonElement? Default { get; set; }
    public float[] Range { get; set; }     // [min,max] for Range type

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

public static class ShaderProgramLoader {
    public static StandardShader Load(BallisticProject project, string assetPath) {
        var definition = ContentText.ReadJson<ShaderDefinition>(project, assetPath);
        if (definition is null) {
            Debugging.LogError($"'{assetPath}': shader definition not found.");
            return null;
        }

        var vertexCode = ReadGlsl(project, definition.Vertex, assetPath);
        var fragmentCode = ReadGlsl(project, definition.Fragment, assetPath);
        if (vertexCode is null || fragmentCode is null)
            return null;

        var shader = GraphicAPI.CreateStandardShader(vertexCode, fragmentCode);
        // Custom Properties block overrides the built-in Standard declaration; omit it (the common
        // case, incl. the legacy .shader assets) and the shader keeps StandardShaderProperties.
        if (shader is not null && definition.Properties is { Length: > 0 } defs) {
            var props = new ShaderProperty[defs.Length];
            for (int i = 0; i < defs.Length; i++)
                props[i] = defs[i].ToShaderProperty(assetPath);
            shader.SetProperties(new ShaderProperties(props));
        }

        // Custom surface body: read the referenced .hlsl/.surface text and hand it to the shader. The
        // backend (DX12) compiles a per-material PSO from it lazily. SurfaceKey = the resolved asset path
        // (+ a content hash so hot-reload busts the PSO cache on edit). A broken/missing ref logs + leaves
        // the shader Standard (no surface), NOT a hard failure — the material still renders (as Standard).
        if (shader is not null && !string.IsNullOrWhiteSpace(definition.Surface)) {
            string surfacePath = AssetRef.IsGuidRef(definition.Surface, out Guid sg)
                ? AssetDatabase.GuidToAssetPath(sg) : definition.Surface;
            string body = surfacePath is not null ? ContentText.Read(project, surfacePath) : null;
            if (body is null)
                Debugging.LogError($"'{assetPath}': surface source '{definition.Surface}' did not load; " +
                                   "material renders as Standard.");
            else {
                shader.SurfaceSource = body;
                // Key = the resolved asset path (stable across edits). The PSO cache keys by this path
                // and tracks the source content itself, so hot-reload recompiles the SAME key in place
                // (the material keeps its key; only the cache's PSO + source change).
                shader.SurfaceKey = surfacePath ?? definition.Surface;
                shader.SurfaceSourcePath = surfacePath;
            }
        }
        return shader;
    }

    static string ReadGlsl(BallisticProject project, string reference, string ownerPath) {
        var glslAssetPath = AssetRef.IsGuidRef(reference, out Guid guid)
            ? AssetDatabase.GuidToAssetPath(guid)
            : reference;

        if (glslAssetPath is null) {
            Debugging.LogError($"'{ownerPath}': shader stage reference '{reference}' does not resolve to an asset.");
            return null;
        }

        var code = ContentText.Read(project, glslAssetPath);
        if (code is null) {
            Debugging.LogError($"'{ownerPath}': shader source '{glslAssetPath}' does not exist.");
            return null;
        }

        return code;
    }
}
