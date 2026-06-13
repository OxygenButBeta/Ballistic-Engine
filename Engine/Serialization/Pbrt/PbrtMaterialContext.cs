using System.Globalization;
using System.Text;
using BallisticEngine.AssetPipeline;
using BallisticEngine.AssetPipeline.Loaders;
using OpenTK.Mathematics;

namespace BallisticEngine;

// Generates the sibling "<Scene>_Materials/" folder of .mat files for a pbrt import, maps pbrt
// material types onto the engine's PBR .mat model, resolves texture/ply paths to "Assets/..." refs,
// and writes inline trianglemesh data out as .ply files. Owns dedup so each unique pbrt material
// becomes exactly one .mat. Created per-Convert; Flush() ensures the materials dir exists even when
// empty is unnecessary (we create lazily on first write).
sealed class MaterialContext {
    readonly string pbrtDir;
    readonly string sceneName;
    readonly string materialsDir;        // <pbrtDir>\<scene>_Materials
    readonly string inlineDir;           // <pbrtDir>\<scene>_Geometry (inline trianglemesh -> .ply)
    readonly Func<string, string> resolve;

    readonly Dictionary<string, string> materialRefByName = new(StringComparer.Ordinal); // pbrt name -> Assets ref
    string emissiveFallbackRef;

    public MaterialContext(string pbrtAbsolutePath, string sceneName, Func<string, string> resolve) {
        this.pbrtDir = Path.GetDirectoryName(Path.GetFullPath(pbrtAbsolutePath))!;
        this.sceneName = sceneName;
        this.resolve = resolve;
        materialsDir = Path.Combine(pbrtDir, $"{sceneName}_Materials");
        inlineDir = Path.Combine(pbrtDir, $"{sceneName}_Geometry");
    }

    public string Resolve(string absolutePath) => resolve?.Invoke(absolutePath);

    public void Flush() { /* assets are written eagerly; nothing buffered */ }

    // ---- material refs --------------------------------------------------------

    // Returns the "Assets/..." ref of the .mat for this mesh, generating it on first use. Emissive
    // meshes (AreaLightSource) get an emissive variant so they glow.
    public string MaterialRefFor(PbrtMesh mesh, PbrtSceneData data) {
        if (mesh.IsEmissive)
            return EmissiveMaterialRef(mesh.EmissiveRadiance);

        string name = mesh.MaterialName;
        if (name == null) return null;
        if (materialRefByName.TryGetValue(name, out var existing)) return existing;

        if (!data.Materials.TryGetValue(name, out var mat))
            mat = new PbrtMaterial(); // referenced-but-undefined -> gray diffuse

        MaterialDefinition def = MapMaterial(mat, data);
        string fileName = SafeFileName(name);
        string assetRef = WriteMaterial(fileName, def);
        materialRefByName[name] = assetRef;
        return assetRef;
    }

    string EmissiveMaterialRef(Vector3 radiance) {
        // One shared emissive material keyed by colour bucket would be ideal; a single shared
        // light-grey emitter is enough for v1 (area lights are usually one colour per scene).
        if (emissiveFallbackRef != null) return emissiveFallbackRef;
        float intensity = MathF.Max(radiance.X, MathF.Max(radiance.Y, radiance.Z));
        Vector3 colour = intensity > 1e-6f ? radiance / intensity : Vector3.One;
        var def = new MaterialDefinition {
            Shader = ModelImporter.DefaultShaderRef,
            BaseColor = new[] { colour.X, colour.Y, colour.Z, 1f },
            EmissiveColor = new[] { colour.X, colour.Y, colour.Z },
            EmissiveIntensity = Math.Clamp(intensity, 1f, 50f),
            Metallic = 0f,
            Roughness = 1f,
            DoubleSided = true,
        };
        emissiveFallbackRef = WriteMaterial("Emissive", def);
        return emissiveFallbackRef;
    }

    MaterialDefinition MapMaterial(PbrtMaterial mat, PbrtSceneData data) {
        // DoubleSided: pbrt is left-handed, so its .ply triangle winding is opposite the engine's —
        // single-sided culling would hide every inward-facing surface (ceilings, far walls). Render
        // both faces. (A pbrt scene importer can't flip per-mesh winding on referenced .ply geometry.)
        var def = new MaterialDefinition { Shader = ModelImporter.DefaultShaderRef, DoubleSided = true };

        // Albedo: constant reflectance, or a bound imagemap texture.
        if (mat.ReflectanceTexture != null && data.Textures.TryGetValue(mat.ReflectanceTexture, out var tex)) {
            string file = ImageFileOf(tex, data);
            string texRef = file != null ? resolve?.Invoke(file) : null;
            if (texRef != null) def.Textures["Diffuse"] = texRef;
            else if (mat.Reflectance is { } rc) def.BaseColor = new[] { rc.X, rc.Y, rc.Z, 1f };
        }
        else if (mat.Reflectance is { } c) {
            def.BaseColor = new[] { c.X, c.Y, c.Z, 1f };
        }

        // Normal map (v4 "string normalmap" is a direct file path on the material).
        if (mat.NormalMap != null) {
            string nmAbs = Path.GetFullPath(Path.Combine(pbrtDir, mat.NormalMap.Replace('\\', '/')));
            string nmRef = resolve?.Invoke(nmAbs);
            if (nmRef != null) def.Textures["Normal"] = nmRef;
        }

        // PBR factors per material family.
        switch (mat.Type) {
            case "conductor":
            case "metal":
            case "mirror":
            case "coatedconductor":
                def.Metallic = 1f;
                def.Roughness = mat.Roughness ?? 0.2f;
                if (def.BaseColor == null && !def.Textures.ContainsKey("Diffuse"))
                    def.BaseColor = new[] { 0.9f, 0.9f, 0.9f, 1f }; // named-spectrum metal -> light grey
                break;

            case "dielectric":
            case "glass":
            case "thindielectric":
                def.Metallic = 0f;
                def.Roughness = mat.Roughness ?? 0.05f;
                def.Transparent = true;
                def.Opacity = 0.25f;
                if (def.BaseColor == null) def.BaseColor = new[] { 0.95f, 0.97f, 1f, 1f };
                break;

            case "measured":
                // Can't evaluate the BSDF file; approximate as a glossy non-metal.
                def.Metallic = 0f;
                def.Roughness = mat.Roughness ?? 0.3f;
                break;

            case "coateddiffuse":
            case "plastic":
            case "uber":
            case "substrate":
                def.Metallic = 0f;
                def.Roughness = mat.Roughness ?? 0.4f;
                break;

            case "diffuse":
            case "matte":
            default:
                def.Metallic = 0f;
                def.Roughness = mat.Roughness ?? 1f;
                break;
        }

        return def;
    }

    // Resolves a texture (possibly a "scale" wrapper) down to its imagemap file's absolute path.
    string ImageFileOf(PbrtTexture tex, PbrtSceneData data) {
        int guard = 0;
        while (tex != null && guard++ < 8) {
            if (tex.FileName != null)
                return Path.GetFullPath(Path.Combine(pbrtDir, tex.FileName.Replace('\\', '/')));
            if (tex.InnerTexture != null && data.Textures.TryGetValue(tex.InnerTexture, out var inner))
                tex = inner;
            else break;
        }
        return null;
    }

    // ---- writing .mat + .meta -------------------------------------------------

    string WriteMaterial(string fileName, MaterialDefinition def) {
        Directory.CreateDirectory(materialsDir);
        string absolute = Path.Combine(materialsDir, fileName + ".mat");
        PipelineJson.Write(absolute, def);
        EnsureMeta(absolute);
        return resolve?.Invoke(absolute) ?? AssetRefGuess(absolute);
    }

    static void EnsureMeta(string assetAbsolute) {
        string metaPath = MetaFile.PathFor(assetAbsolute);
        if (!File.Exists(metaPath))
            new MetaFile { Guid = Guid.NewGuid(), Importer = "NativeAssetImporter" }.Save(metaPath);
    }

    // ---- inline trianglemesh -> .ply ------------------------------------------

    public string WriteInlinePly(PbrtMesh mesh, int index) {
        Directory.CreateDirectory(inlineDir);
        string absolute = Path.Combine(inlineDir, $"{sceneName}_mesh_{index:D4}.ply");
        WriteAsciiPly(absolute, mesh);
        EnsureMeta(absolute);
        return absolute;
    }

    static void WriteAsciiPly(string path, PbrtMesh mesh) {
        int vertexCount = mesh.Positions.Count / 3;
        int triCount = mesh.Indices.Count / 3;
        bool hasN = mesh.Normals != null && mesh.Normals.Count == mesh.Positions.Count;
        bool hasUv = mesh.Uvs != null && mesh.Uvs.Count == vertexCount * 2;

        var sb = new StringBuilder();
        sb.Append("ply\n").Append("format ascii 1.0\n");
        sb.Append("element vertex ").Append(vertexCount).Append('\n');
        sb.Append("property float x\nproperty float y\nproperty float z\n");
        if (hasN) sb.Append("property float nx\nproperty float ny\nproperty float nz\n");
        if (hasUv) sb.Append("property float s\nproperty float t\n");
        sb.Append("element face ").Append(triCount).Append('\n');
        sb.Append("property list uchar int vertex_indices\n");
        sb.Append("end_header\n");

        var ci = CultureInfo.InvariantCulture;
        for (int i = 0; i < vertexCount; i++) {
            sb.Append(mesh.Positions[i * 3].ToString(ci)).Append(' ')
              .Append(mesh.Positions[i * 3 + 1].ToString(ci)).Append(' ')
              .Append(mesh.Positions[i * 3 + 2].ToString(ci));
            if (hasN)
                sb.Append(' ').Append(mesh.Normals[i * 3].ToString(ci)).Append(' ')
                  .Append(mesh.Normals[i * 3 + 1].ToString(ci)).Append(' ')
                  .Append(mesh.Normals[i * 3 + 2].ToString(ci));
            if (hasUv)
                sb.Append(' ').Append(mesh.Uvs[i * 2].ToString(ci)).Append(' ')
                  .Append(mesh.Uvs[i * 2 + 1].ToString(ci));
            sb.Append('\n');
        }
        for (int i = 0; i < triCount; i++)
            sb.Append("3 ").Append(mesh.Indices[i * 3]).Append(' ')
              .Append(mesh.Indices[i * 3 + 1]).Append(' ')
              .Append(mesh.Indices[i * 3 + 2]).Append('\n');

        File.WriteAllText(path, sb.ToString());
    }

    // ---- helpers --------------------------------------------------------------

    string AssetRefGuess(string absolute) =>
        // Falls back when no resolver was supplied (tests): make a project-relative-looking ref.
        Path.GetRelativePath(pbrtDir, absolute).Replace(Path.DirectorySeparatorChar, '/');

    static string SafeFileName(string name) {
        var sb = new StringBuilder(name.Length);
        foreach (char ch in name)
            sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), ch) >= 0 ? '_' : ch);
        var cleaned = sb.ToString().Trim();
        return cleaned.Length == 0 ? "Material" : cleaned;
    }
}
