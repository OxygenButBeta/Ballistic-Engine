using System.Text.Json.Nodes;
using BallisticEngine.AssetPipeline.Loaders;

namespace BallisticEngine.AssetPipeline;

public sealed class ModelImporter : IAssetImporter {
    static readonly string[] Extensions = [".fbx", ".obj", ".gltf", ".glb", ".dae"];

    public const string DefaultShaderRef = "Assets/Default/Shaders/Standard.shader";
    public const string SkinnedShaderRef = "Assets/Default/Shaders/SkinnedStandard.shader";

    public string Name => "ModelImporter";

    public int Version => 13;
    public string ArtifactExtension => ".bmesh";

    public bool GeneratesSourceAssets => true;

    public bool CanImport(string extension) => Extensions.Contains(extension);

    public JsonObject CreateDefaultSettings(string assetPath) => new() {
        ["flipUVs"] = true,
        ["meshIndex"] = -1,
        ["generateMaterials"] = true,
        ["shader"] = DefaultShaderRef,
        ["splitByNodes"] = true,
        ["scaleFactor"] = 0.0,
        ["generateLODs"] = true,
        ["lodCount"] = 4,
        ["lodReduction"] = 0.5,
        ["lodMinTris"] = 64,
        ["generateSdf"] = true,
        ["sdfResolution"] = 64,
        ["generateCards"] = true,
        ["maxCards"] = 12,
        // Lumen FAZ 8.6 — per-submesh cards for whole-mesh-merge / split-by-nodes meshes (Bistro). Each
        // submesh gets its own tight submesh-local SDF (low res) + cards; tiny submeshes are skipped.
        ["subMeshCardSdfResolution"] = 32,
        ["subMeshMaxCards"] = 8,
        ["subMeshMinTris"] = 32,
    };

    public void Import(AssetImportContext context) {
        var flipUVs = context.Settings?["flipUVs"]?.GetValue<bool>() ?? true;
        var meshIndex = context.Settings?["meshIndex"]?.GetValue<int>() ?? -1;
        var splitByNodes = context.Settings?["splitByNodes"]?.GetValue<bool>() ?? true;
        var scaleFactor = (float)(context.Settings?["scaleFactor"]?.GetValue<double>() ?? 0.0);

        if (meshIndex >= 0) {
            MeshData single = AssimpMeshDecoder.Decode(context.SourceAbsolutePath, flipUVs, meshIndex);
            single = GenerateSdf(context, single);
            single = GenerateCards(context, single);
            MeshArtifact.Write(context.ArtifactAbsolutePath, in single);
            return;
        }

        var importSkin = context.Settings?["importSkin"]?.GetValue<bool>() ?? true;
        var generateMaterials = context.Settings?["generateMaterials"]?.GetValue<bool>() ?? true;

        if (importSkin) {
            var ext = Path.GetExtension(context.SourceAbsolutePath).ToLowerInvariant();
            if (GltfSkinDecoder.SupportsExtension(ext)) {
                if (GltfSkinDecoder.HasSkin(context.SourceAbsolutePath)) {
                    ImportSkinned(context, generateMaterials, GltfSkinDecoder.Decode(context.SourceAbsolutePath, flipUVs));
                    return;
                }
            }
            else if (AssimpSkinDecoder.SceneHasSkin(context.SourceAbsolutePath, flipUVs)) {
                ImportSkinned(context, generateMaterials, AssimpSkinDecoder.Decode(context.SourceAbsolutePath, flipUVs));
                return;
            }
        }

        DecodedModel model = AssimpMeshDecoder.DecodeScene(
            context.SourceAbsolutePath, flipUVs, splitByNodes, scaleFactor);
        MeshData data = generateMaterials ? GenerateMaterials(context, model) : model.Mesh;
        data = BuildLods(context, data);
        data = GenerateSdf(context, data);
        data = GenerateCards(context, data);
        data = GenerateSubMeshCards(context, data);
        MeshArtifact.Write(context.ArtifactAbsolutePath, in data);
    }

    /// <summary>
    /// Generates the offline mesh SDF (Lumen FAZ 1) from the FINAL LOD0 geometry and attaches it.
    /// Gated by the importer "generateSdf" setting (default true) AND the BALLISTIC_SDF env var
    /// (set to "0" to disable globally). Skinned meshes are skipped — Lumen uses runtime mesh cards
    /// for those, not a static SDF.
    /// </summary>
    static MeshData GenerateSdf(AssetImportContext context, in MeshData data) {
        if (Environment.GetEnvironmentVariable("BALLISTIC_SDF") == "0")
            return data;
        bool enabled = context.Settings?["generateSdf"]?.GetValue<bool>() ?? true;
        if (!enabled)
            return data;
        if (data.IsSkinned) {
            Debugging.Log($"[SDF] '{context.AssetPath}': skinned mesh — SDF skipped (runtime cards).");
            return data;
        }
        if (!data.IsValid || data.Indices.Length < 3)
            return data;

        int maxRes = context.Settings?["sdfResolution"]?.GetValue<int>() ?? 64;
        maxRes = Math.Clamp(maxRes, 8, 256);

        try {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            MeshSdf sdf = Sdf.MeshSdfBuilder.Generate(in data, maxRes);
            sw.Stop();
            if (sdf is null || !sdf.IsValid) {
                Debugging.LogWarning($"[SDF] '{context.AssetPath}': generation produced no field (degenerate mesh).");
                return data;
            }
            Debugging.Log(
                $"[SDF] '{context.AssetPath}': {sdf.ResX}x{sdf.ResY}x{sdf.ResZ} grid, " +
                $"{data.Indices.Length / 3} tris, {sw.ElapsedMilliseconds} ms");
            return data.WithSdf(sdf);
        }
        catch (Exception exception) {
            Debugging.LogWarning($"[SDF] '{context.AssetPath}': generation failed: {exception.Message}");
            return data;
        }
    }

    /// <summary>
    /// Generates the offline mesh-card representation (Lumen FAZ 3a) from the per-mesh SDF and attaches
    /// it. Gated by the importer "generateCards" setting (default true) AND the BALLISTIC_CARDS env var
    /// (set to "0" to disable globally). REQUIRES a valid SDF (cards are built from its surfels) — skips
    /// when absent. Skinned meshes are skipped (no SDF/cards, like GenerateSdf).
    /// </summary>
    static MeshData GenerateCards(AssetImportContext context, in MeshData data) {
        if (Environment.GetEnvironmentVariable("BALLISTIC_CARDS") == "0")
            return data;
        bool enabled = context.Settings?["generateCards"]?.GetValue<bool>() ?? true;
        if (!enabled)
            return data;
        if (data.IsSkinned)
            return data;
        if (data.Sdf is not { IsValid: true }) {
            Debugging.Log($"[Cards] '{context.AssetPath}': no SDF — cards skipped (cards require an SDF).");
            return data;
        }

        int maxCards = context.Settings?["maxCards"]?.GetValue<int>() ?? 12;
        maxCards = Math.Clamp(maxCards, 1, Sdf.MeshCardBuilder.MaxCardsPerMesh);

        try {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            MeshCards cards = Sdf.MeshCardBuilder.Generate(in data, maxCards);
            sw.Stop();
            if (cards is null || !cards.IsValid) {
                Debugging.Log($"[Cards] '{context.AssetPath}': no valid cards generated.");
                return data;
            }
            Debugging.Log($"[Cards] '{context.AssetPath}': {cards.Count} cards, {sw.ElapsedMilliseconds} ms");
            return data.WithCards(cards);
        }
        catch (Exception exception) {
            Debugging.LogWarning($"[Cards] '{context.AssetPath}': generation failed: {exception.Message}");
            return data;
        }
    }

    /// <summary>
    /// Generates PER-SUBMESH cards (Lumen FAZ 8.6) for whole-mesh-merge / split-by-nodes meshes. DECISION
    /// RULE: only meshes with &gt;1 submesh trigger this — a single-submesh mesh (e.g. CornellBox) keeps the
    /// whole-mesh <see cref="MeshData.Cards"/> path and is byte-identical. For each submesh we build a tight
    /// SDF in that submesh's LOCAL space (a SMALL grid per component — no 512k-cap blowout) and cards from it;
    /// the SDF is discarded (only the cards are stored). Tiny submeshes (&lt; subMeshMinTris) are skipped. Same
    /// env gate (BALLISTIC_CARDS=0) and importer "generateCards" flag as the whole-mesh path. Skinned skipped.
    /// </summary>
    static MeshData GenerateSubMeshCards(AssetImportContext context, in MeshData data) {
        if (Environment.GetEnvironmentVariable("BALLISTIC_CARDS") == "0")
            return data;
        bool enabled = context.Settings?["generateCards"]?.GetValue<bool>() ?? true;
        if (!enabled || data.IsSkinned || !data.IsValid)
            return data;

        SubMeshData[] subs = data.SubMeshes;
        if (subs is not { Length: > 1 })   // single submesh keeps the whole-mesh card path (no regression)
            return data;

        // DECISION RULE (FAZ 8.6): per-submesh cards are ONLY for genuine split-by-nodes meshes — components
        // placed in their own LOCAL spaces via distinct NodeTransforms (Bistro, SunTemple). A mesh whose
        // submeshes are merely MATERIAL splits of one node (e.g. CornellBox: 5 material groups, all identity /
        // shared NodeTransform) is NOT split-by-nodes: its submeshes share mesh-local space, so a per-submesh SDF
        // adds nothing over the whole-mesh SDF — keep the whole-mesh Cards path (no regression, cards unchanged).
        if (!HasDistinctNodeTransforms(subs))
            return data;

        int sdfRes = context.Settings?["subMeshCardSdfResolution"]?.GetValue<int>() ?? 32;
        sdfRes = Math.Clamp(sdfRes, 8, 128);
        int maxCards = context.Settings?["subMeshMaxCards"]?.GetValue<int>() ?? 8;
        maxCards = Math.Clamp(maxCards, 1, Sdf.MeshCardBuilder.MaxCardsPerMesh);
        int minTris = Math.Max(1, context.Settings?["subMeshMinTris"]?.GetValue<int>() ?? 32);

        var perSub = new MeshCards[subs.Length];
        int generated = 0, skippedTiny = 0, skippedEmpty = 0, totalCards = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try {
            for (int i = 0; i < subs.Length; i++) {
                LodRange lod0Range = subs[i].LodAt(0);
                int tris = Math.Max(0, lod0Range.IndexCount) / 3;
                if (tris < minTris) { skippedTiny++; continue; }
                MeshSdf sdf = Sdf.MeshSdfBuilder.GenerateForSubMesh(in data, in subs[i], sdfRes);
                if (sdf is null || !sdf.IsValid) { skippedEmpty++; continue; }
                MeshCards cards = Sdf.MeshCardBuilder.Generate(sdf, maxCards, quiet: true);
                if (cards is { IsValid: true }) { perSub[i] = cards; generated++; totalCards += cards.Count; }
                else skippedEmpty++;
            }
        }
        catch (Exception exception) {
            Debugging.LogWarning($"[SubCards] '{context.AssetPath}': generation failed: {exception.Message}");
            return data;
        }
        sw.Stop();

        if (generated == 0) {
            Debugging.Log($"[SubCards] '{context.AssetPath}': {subs.Length} submeshes — no per-submesh cards " +
                          $"(skippedTiny={skippedTiny} skippedEmpty={skippedEmpty}), {sw.ElapsedMilliseconds} ms.");
            return data;
        }
        Debugging.Log($"[SubCards] '{context.AssetPath}': {totalCards} cards across {generated}/{subs.Length} submeshes " +
                      $"(skippedTiny={skippedTiny} skippedEmpty={skippedEmpty}), {sw.ElapsedMilliseconds} ms.");
        return data.WithSubMeshCards(perSub);
    }

    /// <summary>
    /// True when the submeshes are placed by GENUINE split-by-nodes transforms (Bistro/SunTemple) rather than
    /// being material splits of a single node (CornellBox). Heuristic: at least one submesh has a non-identity
    /// NodeTransform, OR two submeshes have differing NodeTransforms — either means components live in distinct
    /// local spaces and benefit from a per-submesh SDF. All-identical (typically all-identity) → material splits.
    /// </summary>
    static bool HasDistinctNodeTransforms(SubMeshData[] subs) {
        Matrix4 first = subs[0].NodeTransform;
        bool anyNonIdentity = !NearlyIdentity(first);
        for (int i = 1; i < subs.Length; i++) {
            Matrix4 m = subs[i].NodeTransform;
            if (!NearlyIdentity(m)) anyNonIdentity = true;
            if (!NearlyEqual(m, first)) return true;   // distinct placements → split-by-nodes
        }
        return anyNonIdentity;
    }

    static bool NearlyIdentity(in Matrix4 m) => NearlyEqual(m, Matrix4.Identity);

    static bool NearlyEqual(in Matrix4 a, in Matrix4 b) {
        const float eps = 1e-5f;
        return MathF.Abs(a.M11 - b.M11) < eps && MathF.Abs(a.M12 - b.M12) < eps && MathF.Abs(a.M13 - b.M13) < eps && MathF.Abs(a.M14 - b.M14) < eps
            && MathF.Abs(a.M21 - b.M21) < eps && MathF.Abs(a.M22 - b.M22) < eps && MathF.Abs(a.M23 - b.M23) < eps && MathF.Abs(a.M24 - b.M24) < eps
            && MathF.Abs(a.M31 - b.M31) < eps && MathF.Abs(a.M32 - b.M32) < eps && MathF.Abs(a.M33 - b.M33) < eps && MathF.Abs(a.M34 - b.M34) < eps
            && MathF.Abs(a.M41 - b.M41) < eps && MathF.Abs(a.M42 - b.M42) < eps && MathF.Abs(a.M43 - b.M43) < eps && MathF.Abs(a.M44 - b.M44) < eps;
    }

    static MeshData BuildLods(AssetImportContext context, in MeshData data) {
        bool gen = context.Settings?["generateLODs"]?.GetValue<bool>() ?? false;
        if (!gen || data.IsSkinned) return data;
        int lodCount = context.Settings?["lodCount"]?.GetValue<int>() ?? 4;
        float reduction = (float)(context.Settings?["lodReduction"]?.GetValue<double>() ?? 0.5);
        int minTris = context.Settings?["lodMinTris"]?.GetValue<int>() ?? 64;
        return Importing.Decimation.LodChainBuilder.Build(data,
            new Importing.Decimation.LodChainBuilder.Settings(lodCount, reduction, minTris));
    }

    void ImportSkinned(AssetImportContext context, bool generateMaterials,
        AssimpSkinDecoder.DecodedSkinnedModel model) {
        var wrapped = new DecodedModel { Mesh = model.Mesh, SubMeshMaterials = model.SubMeshMaterials };
        MeshData data = generateMaterials ? GenerateMaterials(context, wrapped, SkinnedShaderRef) : model.Mesh;

        if (data.Skeleton.BoneCount == 0 && model.Mesh.IsSkinned)
            data = new MeshData(data.Vertices, data.Indices, data.UVs, data.Normals, data.Tangents,
                data.SubMeshes, data.Nodes, model.Mesh.BoneIndices, model.Mesh.BoneWeights, model.Mesh.Skeleton);

        MeshArtifact.Write(context.ArtifactAbsolutePath, in data);

        WriteAnimationAssets(context, model.Animations);
    }

    void WriteAnimationAssets(AssetImportContext context, AnimationClipData[] animations) {
        if (animations is null || animations.Length == 0)
            return;
        if (!TryGetProjectRoot(context, out var projectRoot)) {
            Debugging.LogWarning($"'{context.AssetPath}': cannot determine project root; animations skipped.");
            return;
        }

        var modelDirAbsolute = Path.GetDirectoryName(context.SourceAbsolutePath)!;
        var modelStem = Path.GetFileNameWithoutExtension(context.AssetPath);
        var animationsDirAbsolute = Path.Combine(modelDirAbsolute, $"{modelStem}_Animations");

        try {
            Directory.CreateDirectory(animationsDirAbsolute);
        }
        catch (Exception exception) {
            Debugging.LogWarning($"'{context.AssetPath}': cannot create animations folder: {exception.Message}");
            return;
        }

        var usedNames = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (AnimationClipData clip in animations) {
            var baseName = SanitizeFileName(clip.Name);
            var fileName = baseName;
            if (usedNames.TryGetValue(baseName, out int count)) {
                fileName = $"{baseName}_{count + 1}";
                usedNames[baseName] = count + 1;
            }
            else {
                usedNames[baseName] = 1;
            }

            try {
                var clipAbsolute = Path.Combine(animationsDirAbsolute, fileName + ".banim");
                AnimationArtifact.Write(clipAbsolute, in clip);

                var metaPath = MetaFile.PathFor(clipAbsolute);
                if (!File.Exists(metaPath))
                    new MetaFile { Guid = Guid.NewGuid(), Importer = "AnimationImporter" }.Save(metaPath);
            }
            catch (Exception exception) {
                Debugging.LogWarning($"'{context.AssetPath}': failed to write animation '{clip.Name}': {exception.Message}");
            }
        }
    }

    static MeshData GenerateMaterials(AssetImportContext context, DecodedModel model, string shaderOverride = null) {
        SubMeshData[] subMeshes = (SubMeshData[])model.Mesh.SubMeshes.Clone();
        DecodedMaterial[] materials = model.SubMeshMaterials ?? [];

        if (!TryGetProjectRoot(context, out var projectRoot)) {
            Debugging.LogWarning($"'{context.AssetPath}': cannot determine project root; materials skipped.");
            return model.Mesh;
        }

        var modelDirAbsolute = Path.GetDirectoryName(context.SourceAbsolutePath)!;
        var modelStem = Path.GetFileNameWithoutExtension(context.AssetPath);
        var materialsDirAbsolute = Path.Combine(modelDirAbsolute, $"{modelStem}_Materials");

        var shaderRef = shaderOverride;
        if (string.IsNullOrWhiteSpace(shaderRef))
            shaderRef = context.Settings?["shader"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(shaderRef))
            shaderRef = DefaultShaderRef;

        var resolver = new ModelTextureResolver(modelDirAbsolute, projectRoot);
        var refByMaterial = new Dictionary<DecodedMaterial, string>();
        var fileNameOwners = new Dictionary<string, DecodedMaterial>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < subMeshes.Length && i < materials.Length; i++) {
            DecodedMaterial material = materials[i];
            if (material is null)
                continue;

            if (!refByMaterial.TryGetValue(material, out var materialRef)) {
                materialRef = WriteMaterialAsset(
                    context, material, materialsDirAbsolute, modelDirAbsolute, projectRoot,
                    shaderRef, resolver, fileNameOwners);
                refByMaterial[material] = materialRef;
            }

            if (materialRef is not null)
                subMeshes[i] = subMeshes[i].WithMaterialRef(materialRef);
        }

        return new MeshData(model.Mesh.Vertices, model.Mesh.Indices, model.Mesh.UVs,
            model.Mesh.Normals, model.Mesh.Tangents, subMeshes, model.Mesh.Nodes);
    }

    static string WriteMaterialAsset(AssetImportContext context, DecodedMaterial material,
        string materialsDirAbsolute, string modelDirAbsolute, string projectRoot, string shaderRef,
        ModelTextureResolver resolver, Dictionary<string, DecodedMaterial> fileNameOwners) {
        var definition = new MaterialDefinition { Shader = shaderRef };

        if (material.BaseColor is { } baseColor)
            definition.BaseColor = [baseColor.X, baseColor.Y, baseColor.Z, baseColor.W];
        if (material.Metallic is { } metallic)
            definition.Metallic = metallic;
        if (material.Roughness is { } roughness)
            definition.Roughness = roughness;
        if (material.EmissiveColor is { } emissiveColor)
            definition.EmissiveColor = [emissiveColor.X, emissiveColor.Y, emissiveColor.Z];
        if (material.Opacity is { } opacity && opacity < 0.999f) {
            definition.Opacity = opacity;
            definition.Transparent = true;
        }

        foreach ((TextureType slot, var rawPath) in material.TexturePaths) {
            var absolute = resolver.Resolve(rawPath);
            if (absolute is null) {
                Debugging.LogWarning(
                    $"'{context.AssetPath}': material '{material.Name}' references missing texture '{rawPath}'.");
                continue;
            }

            var textureRef = ToAssetRef(absolute, projectRoot);
            if (textureRef is null) {
                Debugging.LogWarning(
                    $"'{context.AssetPath}': texture '{absolute}' is outside the project; copy it under Assets.");
                continue;
            }

            if (!TextureImporter.SupportsExtension(Path.GetExtension(absolute).ToLowerInvariant())) {
                Debugging.LogWarning(
                    $"'{context.AssetPath}': texture '{textureRef}' has an unsupported format; slot {slot} skipped.");
                continue;
            }

            EnsureTextureMeta(absolute, slot);
            definition.Textures[slot.ToString()] = textureRef;
        }

        if (definition.Textures.Count == 0)
            BindByConvention(context, definition, modelDirAbsolute, projectRoot);

        try {
            Directory.CreateDirectory(materialsDirAbsolute);

            var fileName = UniqueFileName(SanitizeFileName(material.Name), material, fileNameOwners);
            var materialAbsolute = Path.Combine(materialsDirAbsolute, fileName + ".mat");
            PipelineJson.Write(materialAbsolute, definition);

            return ToAssetRef(materialAbsolute, projectRoot);
        }
        catch (Exception exception) {
            Debugging.LogWarning(
                $"'{context.AssetPath}': failed to write material '{material.Name}': {exception.Message}");
            return null;
        }
    }

    static void BindByConvention(AssetImportContext context, MaterialDefinition definition,
        string modelDirAbsolute, string projectRoot) {
        var modelStem = Path.GetFileNameWithoutExtension(context.AssetPath);

        TextureConventionMatcher.Match match = TextureConventionMatcher.Find(
            modelDirAbsolute, modelStem,
            ext => TextureImporter.SupportsExtension(ext));

        if (match.Textures.Count == 0)
            return;

        foreach ((TextureType slot, var absolute) in match.Textures) {
            var textureRef = ToAssetRef(absolute, projectRoot);
            if (textureRef is null) {
                Debugging.LogWarning(
                    $"'{context.AssetPath}': convention texture '{absolute}' is outside the project; skipped.");
                continue;
            }
            EnsureTextureMeta(absolute, slot);
            definition.Textures[slot.ToString()] = textureRef;
        }

        if (match.GlossOnly)
            Debugging.LogWarning(
                $"'{context.AssetPath}': only a gloss map was found (no roughness); roughness left unbound.");

        if (match.HasOpacity)
            definition.Cutout = true;

        Debugging.Log(
            $"'{context.AssetPath}': source material had no textures; auto-bound {definition.Textures.Count} " +
            "map(s) by filename convention.");
    }

    static readonly object textureMetaLock = new();

    static void EnsureTextureMeta(string textureAbsolute, TextureType slot) {
        var metaPath = MetaFile.PathFor(textureAbsolute);
        try {
            lock (textureMetaLock) {
                if (!File.Exists(metaPath)) {
                    new MetaFile {
                        Guid = Guid.NewGuid(),
                        Importer = "TextureImporter",
                        Settings = new JsonObject { ["textureType"] = slot.ToString() },
                    }.Save(metaPath);
                    return;
                }

                MetaFile meta = MetaFile.Load(metaPath);
                var current = meta.Settings?["textureType"]?.GetValue<string>();
                if (string.Equals(current, slot.ToString(), StringComparison.OrdinalIgnoreCase))
                    return;

                meta.Settings ??= new JsonObject();
                meta.Settings["textureType"] = slot.ToString();
                meta.Save(metaPath);
            }
        }
        catch (Exception exception) {
            Debugging.LogWarning($"Could not update meta for '{textureAbsolute}': {exception.Message}");
        }
    }

    static bool TryGetProjectRoot(AssetImportContext context, out string projectRoot) {
        projectRoot = null;
        var source = context.SourceAbsolutePath;
        var assetPath = context.AssetPath;
        if (source is null || assetPath is null || source.Length <= assetPath.Length)
            return false;

        projectRoot = source[..^assetPath.Length].TrimEnd('\\', '/');
        return projectRoot.Length > 0;
    }

    static string ToAssetRef(string absolutePath, string projectRoot) {
        var relative = Path.GetRelativePath(projectRoot, Path.GetFullPath(absolutePath))
            .Replace(Path.DirectorySeparatorChar, '/');
        return relative.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ? relative : null;
    }

    static string SanitizeFileName(string name) {
        if (string.IsNullOrWhiteSpace(name))
            return "Material";

        char[] invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return sanitized.Length > 0 ? sanitized : "Material";
    }

    static string UniqueFileName(string baseName, DecodedMaterial material,
        Dictionary<string, DecodedMaterial> owners) {
        var candidate = baseName;
        var n = 2;
        while (owners.TryGetValue(candidate, out DecodedMaterial owner) && !ReferenceEquals(owner, material))
            candidate = $"{baseName}_{n++}";

        owners[candidate] = material;
        return candidate;
    }
}
