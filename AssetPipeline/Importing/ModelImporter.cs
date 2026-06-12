using System.Text.Json.Nodes;
using BallisticEngine.AssetPipeline.Loaders;

namespace BallisticEngine.AssetPipeline;

// Imports model files into a single .bmesh artifact (node transforms baked in) with one
// submesh per source node mesh (splitByNodes, the default for new imports — the editor can
// then instantiate one entity per source object) or per source material (legacy merged
// mode). When the source carries materials, generates a sibling
// "<Model>_Materials" folder of .mat assets and bakes their refs into the submeshes; the
// generated .mat files are owned by the importer and rewritten on every reimport.
// Texture .meta files referenced by those materials get their textureType set from the slot
// the model actually binds them to (authoritative over filename-suffix inference).
public sealed class ModelImporter : IAssetImporter {
    static readonly string[] Extensions = [".fbx", ".obj", ".gltf", ".glb", ".dae"];

    public const string DefaultShaderRef = "Assets/Default/Shaders/Standard.shader";
    // Skinned meshes need the GPU-skinning vertex stage; their generated materials use this shader.
    public const string SkinnedShaderRef = "Assets/Default/Shaders/SkinnedStandard.shader";

    public string Name => "ModelImporter";
    // v3: vec4 tangents (handedness) + scalar PBR factors in .mat
    // v4: split-by-nodes submeshes + node hierarchy table (BMSH v5), default ON
    // v5: skinned-mesh import (bones/weights/skeleton in BMSH v6) + sibling .banim animation assets
    // v6: glTF skinned materials carry PBR textures (extracted from embedded/external images) + factors
    public int Version => 6;
    public string ArtifactExtension => ".bmesh";

    // Generates a sibling "<Model>_Materials/" folder of .mat assets.
    public bool GeneratesSourceAssets => true;

    public bool CanImport(string extension) => Extensions.Contains(extension);

    public JsonObject CreateDefaultSettings(string assetPath) => new() {
        ["flipUVs"] = true,
        ["meshIndex"] = -1, // -1 = whole scene merged by material; >= 0 = that one mesh, no materials
        ["generateMaterials"] = true,
        ["shader"] = DefaultShaderRef,
        // One submesh per source node mesh (named, with the node's transform + hierarchy
        // table), so the editor can instantiate the model as an entity tree. ON by default —
        // set false per asset to merge submeshes by material instead (far fewer draw calls;
        // the right call for huge static set dressing).
        ["splitByNodes"] = true,
    };

    public void Import(AssetImportContext context) {
        var flipUVs = context.Settings?["flipUVs"]?.GetValue<bool>() ?? true;
        var meshIndex = context.Settings?["meshIndex"]?.GetValue<int>() ?? -1;
        var splitByNodes = context.Settings?["splitByNodes"]?.GetValue<bool>() ?? true;

        if (meshIndex >= 0) {
            // Legacy single-mesh import: geometry only, mesh-local space, no materials.
            MeshData single = AssimpMeshDecoder.Decode(context.SourceAbsolutePath, flipUVs, meshIndex);
            MeshArtifact.Write(context.ArtifactAbsolutePath, in single);
            return;
        }

        var importSkin = context.Settings?["importSkin"]?.GetValue<bool>() ?? true;
        var generateMaterials = context.Settings?["generateMaterials"]?.GetValue<bool>() ?? true;

        // Skinned models take the bind-space decode path (vertices NOT baked by node transform —
        // the bones place them) and emit sibling .banim animation assets. Falls through to the
        // static path when the model has no bones, so non-skinned imports are byte-identical to v4.
        //
        // glTF/glb skin goes through the NATIVE GltfSkinDecoder: AssimpNet 4.1.0's native build
        // silently drops glTF2 skin data (every rigged glTF reads hasBones=false), so Assimp can't
        // import it. FBX/other formats still use AssimpSkinDecoder, whose FBX skin support works.
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

        DecodedModel model = AssimpMeshDecoder.DecodeScene(context.SourceAbsolutePath, flipUVs, splitByNodes);
        MeshData data = generateMaterials ? GenerateMaterials(context, model) : model.Mesh;
        MeshArtifact.Write(context.ArtifactAbsolutePath, in data);
    }

    // ---- Skinned import -----------------------------------------------------

    // Shared by the glTF (native) and FBX (Assimp) skin decoders — both produce a
    // DecodedSkinnedModel, so material generation + artifact + .banim writing is identical.
    void ImportSkinned(AssetImportContext context, bool generateMaterials,
        AssimpSkinDecoder.DecodedSkinnedModel model) {

        // Wrap the skinned mesh in a DecodedModel so the existing material generator applies
        // unchanged (it only reads SubMeshes + SubMeshMaterials and rewrites MaterialRefs). Skinned
        // materials use the SkinnedStandard shader (GPU skinning vertex stage).
        var wrapped = new DecodedModel { Mesh = model.Mesh, SubMeshMaterials = model.SubMeshMaterials };
        MeshData data = generateMaterials ? GenerateMaterials(context, wrapped, SkinnedShaderRef) : model.Mesh;

        // GenerateMaterials rebuilds MeshData from the static ctor (dropping skin) — re-attach the
        // skin/skeleton that decode produced so the artifact carries them.
        if (data.Skeleton.BoneCount == 0 && model.Mesh.IsSkinned)
            data = new MeshData(data.Vertices, data.Indices, data.UVs, data.Normals, data.Tangents,
                data.SubMeshes, data.Nodes, model.Mesh.BoneIndices, model.Mesh.BoneWeights, model.Mesh.Skeleton);

        MeshArtifact.Write(context.ArtifactAbsolutePath, in data);

        WriteAnimationAssets(context, model.Animations);
    }

    // Writes one sibling "<Model>_Animations/<clip>.banim" per source animation, GUID-stamped via a
    // .meta (NativeAssetImporter-style — the .banim IS the artifact, read straight back). Rewritten
    // on every reimport, like the generated materials folder.
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

                // The .banim is read directly as its own artifact; ensure a stable GUID meta.
                var metaPath = MetaFile.PathFor(clipAbsolute);
                if (!File.Exists(metaPath))
                    new MetaFile { Guid = Guid.NewGuid(), Importer = "AnimationImporter" }.Save(metaPath);
            }
            catch (Exception exception) {
                Debugging.LogWarning($"'{context.AssetPath}': failed to write animation '{clip.Name}': {exception.Message}");
            }
        }
    }

    // ---- Material generation ------------------------------------------------

    // Writes one .mat per used source material and returns the mesh data with submesh
    // MaterialRefs pointing at them. Failures degrade per-material (ref stays null).
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

        // A skinned import forces the skinning shader; otherwise honor the per-asset setting.
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
                    context, material, materialsDirAbsolute, projectRoot, shaderRef, resolver, fileNameOwners);
                refByMaterial[material] = materialRef;
            }

            if (materialRef is not null)
                subMeshes[i] = subMeshes[i].WithMaterialRef(materialRef);
        }

        return new MeshData(model.Mesh.Vertices, model.Mesh.Indices, model.Mesh.UVs,
            model.Mesh.Normals, model.Mesh.Tangents, subMeshes, model.Mesh.Nodes);
    }

    static string WriteMaterialAsset(AssetImportContext context, DecodedMaterial material,
        string materialsDirAbsolute, string projectRoot, string shaderRef,
        ModelTextureResolver resolver, Dictionary<string, DecodedMaterial> fileNameOwners) {
        var definition = new MaterialDefinition { Shader = shaderRef };

        // Scalar PBR factors from the source material (null = unstated, loader defaults apply).
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

    // Serializes texture-.meta writes: models import in parallel, and two models referencing the
    // same shared texture would otherwise race on the same .meta file (torn write / file-in-use).
    static readonly object textureMetaLock = new();

    // Creates the texture's .meta with the slot-derived type, or corrects an existing meta whose
    // type disagrees with how the model binds the texture (the GUID is preserved).
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

    // ---- Path helpers -------------------------------------------------------

    // SourceAbsolutePath always ends with AssetPath (same length, separators aside).
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

    // Distinct source materials that sanitize to the same file name get a numeric suffix.
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

// Resolves the raw texture paths a model file reports (absolute authoring-machine paths,
// relative paths, or bare filenames) to actual files near the model. Falls back to a lazily
// built filename index of the model's directory tree, then its parent's.
sealed class ModelTextureResolver {
    readonly string modelDir;
    readonly string projectRoot;
    readonly Dictionary<string, string> cache = new(StringComparer.OrdinalIgnoreCase);
    Dictionary<string, string> fileIndex; // filename -> absolute path (first hit wins)

    public ModelTextureResolver(string modelDir, string projectRoot) {
        this.modelDir = modelDir;
        this.projectRoot = projectRoot;
    }

    public string Resolve(string rawPath) {
        if (string.IsNullOrWhiteSpace(rawPath))
            return null;

        if (cache.TryGetValue(rawPath, out var cached))
            return cached;

        var resolved = ResolveUncached(rawPath.Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar));
        cache[rawPath] = resolved;
        return resolved;
    }

    string ResolveUncached(string raw) {
        var fileName = Path.GetFileName(raw);

        string[] candidates = [
            Path.Combine(modelDir, raw),
            Path.IsPathRooted(raw) ? raw : null,
            Path.Combine(modelDir, fileName),
            Path.Combine(modelDir, "Textures", fileName),
            Path.Combine(modelDir, "textures", fileName),
        ];

        foreach (var candidate in candidates) {
            if (candidate is not null && File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }

        BuildFileIndex();
        return fileIndex.GetValueOrDefault(fileName);
    }

    void BuildFileIndex() {
        if (fileIndex is not null)
            return;

        fileIndex = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        IndexTree(modelDir);

        // Models often live in a sibling folder of their textures ("Models/" + "Textures/");
        // index the parent too, but never escape the project.
        var parent = Path.GetDirectoryName(modelDir);
        if (parent is not null && parent.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
            IndexTree(parent);
    }

    void IndexTree(string root) {
        try {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                fileIndex.TryAdd(Path.GetFileName(file), file);
        }
        catch (Exception exception) {
            Debugging.LogWarning($"Texture search under '{root}' failed: {exception.Message}");
        }
    }
}
