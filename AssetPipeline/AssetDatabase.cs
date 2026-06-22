using BallisticEngine.AssetPipeline;
using BallisticEngine.AssetPipeline.Loaders;

namespace BallisticEngine;

public static class AssetDatabase {
    static AssetImportPipeline pipeline;
    static readonly Dictionary<Guid, BObject> loadedAssets = new();

    static readonly Dictionary<BObject, Guid> assetToGuid = new(ReferenceEqualityComparer.Instance);

    public static BallisticProject Project { get; private set; }

    public static void Initialize(BallisticProject project) {
        Project = project;
        pipeline = new AssetImportPipeline(project);
        loadedAssets.Clear();
        assetToGuid.Clear();
    }

    public static RefreshResult Refresh(bool forceAll = false) => pipeline.Refresh(forceAll);

    public static void LoadFromArtifacts() => pipeline.LoadFromArtifacts();

    public static void WriteGuidMap() => pipeline.WriteGuidMap();

    public static void PrefetchSceneData(string sceneYaml, Action<int, int> progress = null) {
        if (string.IsNullOrEmpty(sceneYaml) || pipeline is null)
            return;

        ScenePrefetcher.Run(pipeline, sceneYaml, IsLoaded, progress);
    }

    static bool IsLoaded(Guid guid) => loadedAssets.ContainsKey(guid);

    public static Action<string> ImportProgress {
        get => pipeline?.Progress;
        set { if (pipeline is not null) pipeline.Progress = value; }
    }

    public static Action<int, int> ImportProgressCount {
        get => pipeline?.ProgressCount;
        set { if (pipeline is not null) pipeline.ProgressCount = value; }
    }

    public static bool TryGetGuid(string assetPath, out Guid guid) =>
        pipeline.PathToGuid.TryGetValue(Normalize(assetPath), out guid);

    public static string GuidToAssetPath(Guid guid) => pipeline.GuidToPath.GetValueOrDefault(guid);

    public static bool TryGetAssetGuid(BObject asset, out Guid guid) {
        if (asset is not null)
            return assetToGuid.TryGetValue(asset, out guid);
        guid = Guid.Empty;
        return false;
    }

    public static IEnumerable<KeyValuePair<string, Guid>> EnumerateAssets() => pipeline.PathToGuid;

    public static bool TryGetMeta(Guid guid, out MetaFile meta) => pipeline.TryGetMeta(guid, out meta);

    public static bool TryGetArtifactPath(Guid guid, out string absolutePath) =>
        pipeline.TryGetArtifactPath(guid, out absolutePath);

    public static void Invalidate(Guid guid) {
        if (loadedAssets.Remove(guid, out BObject asset))
            assetToGuid.Remove(asset);
    }

    public static bool TryLoadTextureData(string assetPath, out TextureData data) {
        data = default;
        return TryGetGuid(assetPath, out Guid guid) && TextureLoader.TryDecode(pipeline, guid, out data);
    }

    public static bool SaveTerrain(TerrainAsset terrain) {
        if (terrain is null || !TryGetAssetGuid(terrain, out Guid guid)) {
            Debugging.LogWarning("SaveTerrain: terrain has no known asset GUID; not saved.");
            return false;
        }

        var assetPath = GuidToAssetPath(guid);
        if (assetPath is null)
            return false;

        try {
            TerrainData data = terrain.ToData();
            var sourceAbsolute = Project.ResolveAbsolute(assetPath);

            var definition = new TerrainDefinition {
                Resolution = data.Resolution,
                SizeX = data.Size.X,
                SizeZ = data.Size.Y,
                HeightScale = data.HeightScale,
                Heights = TerrainHeightCodec.Encode(data.Heights),
            };
            PipelineJson.Write(sourceAbsolute, definition);

            if (TryGetArtifactPath(guid, out var artifactAbsolute))
                TerrainArtifact.Write(artifactAbsolute, in data);

            return true;
        }
        catch (Exception exception) {
            Debugging.LogError($"SaveTerrain '{assetPath}': {exception.Message}");
            return false;
        }
    }

    public static T Load<T>(string assetPath) where T : BObject {
        if (TryGetGuid(assetPath, out Guid guid))
            return Load<T>(guid);

        Debugging.LogError($"Asset not found: '{assetPath}'.");
        return null;
    }

    public static T LoadRef<T>(string reference) where T : BObject {
        if (string.IsNullOrEmpty(reference)) {
            Debugging.LogError("Empty asset reference.");
            return null;
        }

        return AssetRef.IsGuidRef(reference, out Guid guid) ? Load<T>(guid) : Load<T>(reference);
    }

    public static T Load<T>(Guid guid) where T : BObject {
        if (loadedAssets.TryGetValue(guid, out BObject cached))
            return Typed<T>(cached, GuidToAssetPath(guid));

        var assetPath = GuidToAssetPath(guid);
        if (assetPath is null) {
            Debugging.LogError($"No asset with GUID {guid:N} exists in the project.");
            return null;
        }

        BObject asset;
        try {
            asset = LoadByExtension(guid, assetPath, typeof(T));
        }
        catch (Exception exception) {
            Debugging.LogError($"Failed to load '{assetPath}': {exception.Message}");
            return null;
        }

        if (asset is null)
            return null;

        loadedAssets[guid] = asset;
        assetToGuid[asset] = guid;
        return Typed<T>(asset, assetPath);
    }

    static T Typed<T>(BObject asset, string assetPath) where T : BObject {
        if (asset is T typed)
            return typed;

        Debugging.LogError(
            $"'{assetPath}' is a {asset.GetType().Name}, but {typeof(T).Name} was requested.");
        return null;
    }

    static BObject LoadByExtension(Guid guid, string assetPath, Type requestedType) {
        var extension = Path.GetExtension(assetPath).ToLowerInvariant();

        var isImage = extension is ".png" or ".jpg" or ".jpeg" or ".tga" or ".bmp" or ".hdr" or ".exr" or ".dds";
        if (isImage && typeof(Texture3D).IsAssignableFrom(requestedType))
            return EquirectCubemapLoader.Load(pipeline, guid, assetPath);

        return extension switch {
            ".fbx" or ".obj" or ".gltf" or ".glb" or ".dae" => MeshLoader.Load(pipeline, guid, assetPath),
            ".wav" or ".wave" or ".ogg" => AudioClipLoader.Load(pipeline, guid, assetPath),
            ".banim" => AnimationClipLoader.Load(Project, assetPath),
            _ when isImage => TextureLoader.Load(pipeline, guid, assetPath),
            ".shader" => ShaderProgramLoader.Load(Project, assetPath),
            ".mat" => MaterialLoader.Load(Project, assetPath),
            ".cubemap" => CubemapLoader.Load(Project, pipeline, assetPath),
            ".volume" => VolumeProfileLoader.Load(Project, assetPath),
            ".prefab" => PrefabLoader.Load(Project, assetPath),
            ".asset" => DataAssetLoader.Load(Project, assetPath, requestedType),
            ".terrain" => TerrainLoader.Load(pipeline, guid, assetPath),
            ".ttf" => FontLoader.Load(Project, assetPath),
            _ => NotLoadable(assetPath, extension),
        };
    }

    static BObject NotLoadable(string assetPath, string extension) {
        Debugging.LogError($"'{assetPath}': no loader for '{extension}' assets.");
        return null;
    }

    static string Normalize(string assetPath) => assetPath?.Replace('\\', '/');
}
