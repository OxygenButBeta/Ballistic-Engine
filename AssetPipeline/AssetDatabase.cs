using BallisticEngine.AssetPipeline;
using BallisticEngine.AssetPipeline.Loaders;

namespace BallisticEngine;

// Runtime entry point of the asset system. Initialize + Refresh once at startup,
// then Load assets by "Assets/..." path or GUID. Loaded assets are cached by GUID,
// so loading the same asset twice returns the same instance.
//
// Load never throws on missing or broken assets: it logs and returns null
// (materials substitute flat fallback textures for broken texture refs).
public static class AssetDatabase {
    static AssetImportPipeline pipeline;
    static readonly Dictionary<Guid, BObject> loadedAssets = new();

    // Reverse of loadedAssets: lets scene serialization recover an asset's GUID from a loaded
    // Mesh/Material instance. Reference identity (two distinct assets never compare equal by value).
    static readonly Dictionary<BObject, Guid> assetToGuid = new(ReferenceEqualityComparer.Instance);

    public static BallisticProject Project { get; private set; }

    public static void Initialize(BallisticProject project) {
        Project = project;
        pipeline = new AssetImportPipeline(project);
        loadedAssets.Clear();
        assetToGuid.Clear();

        // The engine layer can't know the project layout; hand it the probe-bake cache homes.
        IrradianceVolume.CacheDirectory = Path.Combine(project.LibraryPath, "ProbeVolumes");
        ReflectionVolume.CacheDirectory = Path.Combine(project.LibraryPath, "ReflectionProbes");
    }

    // forceAll = true rebuilds every Library artifact from source (Unity's "Reimport All").
    // Already-loaded asset instances are NOT invalidated — they keep serving the open scene
    // (and its save-time guid lookups); rebuilt artifacts apply on the next load.
    public static RefreshResult Refresh(bool forceAll = false) => pipeline.Refresh(forceAll);

    // Shipped-player equivalent of Refresh: registers assets from the baked .meta + ArtifactDB.json
    // on disk WITHOUT re-importing from source (no Assimp/Stb/Magick, no SDK, no writes). Load then
    // resolves "Assets/..." -> guid -> artifact exactly as in the editor. Call instead of Refresh in
    // player mode — without it the GUID maps stay empty and every scene asset ref fails to resolve.
    public static void LoadFromArtifacts() => pipeline.LoadFromArtifacts();

    // Bakes the complete asset-path -> GUID table to Library\guidmap.json (build time) so a shipped
    // player can resolve references without the .meta sidecars. Call after Refresh. See GuidMap.
    public static void WriteGuidMap() => pipeline.WriteGuidMap();

    // Decodes (on worker threads) the CPU data for every mesh and texture a scene references, so the
    // subsequent main-thread scene load only does the cheap GL upload instead of reading + inflating
    // artifacts on the render thread. Safe to call off the render thread (no GL here). The scene load
    // that follows must run on the render thread as usual; warm data is consumed from AssetDataCache.
    //
    // progress (optional) is invoked from worker threads with a "(done/total)" style string.
    public static void PrefetchSceneData(string sceneYaml, Action<int, int> progress = null) {
        if (string.IsNullOrEmpty(sceneYaml) || pipeline is null)
            return;

        ScenePrefetcher.Run(pipeline, sceneYaml, IsLoaded, progress);
    }

    // True when the GUID's asset instance is already cached (prefetching it would be wasted work —
    // Load returns the cached instance without touching AssetDataCache).
    static bool IsLoaded(Guid guid) => loadedAssets.ContainsKey(guid);

    // Fires with the file name as each asset is processed during Refresh (for a progress UI).
    // Runs on the thread Refresh runs on. Setting it replaces any previous handler.
    public static Action<string> ImportProgress {
        get => pipeline?.Progress;
        set { if (pipeline is not null) pipeline.Progress = value; }
    }

    public static bool TryGetGuid(string assetPath, out Guid guid) =>
        pipeline.PathToGuid.TryGetValue(Normalize(assetPath), out guid);

    public static string GuidToAssetPath(Guid guid) => pipeline.GuidToPath.GetValueOrDefault(guid);

    // The GUID a loaded asset came from (for serializing component asset references).
    public static bool TryGetAssetGuid(BObject asset, out Guid guid) {
        if (asset is not null)
            return assetToGuid.TryGetValue(asset, out guid);
        guid = Guid.Empty;
        return false;
    }

    // All asset (path, guid) pairs known to the project — for the asset browser.
    public static IEnumerable<KeyValuePair<string, Guid>> EnumerateAssets() => pipeline.PathToGuid;

    // The asset's .meta (importer + settings) — for the editor's asset inspector.
    public static bool TryGetMeta(Guid guid, out MetaFile meta) => pipeline.TryGetMeta(guid, out meta);

    // Absolute path of the asset's Library artifact (e.g. for editor thumbnails).
    public static bool TryGetArtifactPath(Guid guid, out string absolutePath) =>
        pipeline.TryGetArtifactPath(guid, out absolutePath);

    // Drops a loaded asset from the cache so the next Load re-reads it (e.g. after a reimport).
    // Objects already holding the old instance keep it.
    public static void Invalidate(Guid guid) {
        if (loadedAssets.Remove(guid, out BObject asset))
            assetToGuid.Remove(asset);
    }

    // Decodes the CPU pixel data of an image asset WITHOUT uploading it to the GPU — the engine layer
    // can't reach AssetPipeline loaders directly, so this is the supported way to read a heightmap's
    // raw texels (e.g. to seed a terrain from an image). Returns false (and logs nothing extra) when
    // the asset is missing or has no artifact. See Terrain image-seeding.
    public static bool TryLoadTextureData(string assetPath, out TextureData data) {
        data = default;
        return TryGetGuid(assetPath, out Guid guid) && TextureLoader.TryDecode(pipeline, guid, out data);
    }

    // Persists a sculpted TerrainAsset back to disk: rewrites the .terrain source (so the edit
    // survives a reimport) AND the .bterrain artifact (so the live, already-loaded instance's data
    // matches without forcing a full reimport). Mirrors the VolumeProfile edit-write-back pattern —
    // the editor mutates the cached instance and calls this in one step. No-op if the asset has no
    // known GUID/path. Returns false on any I/O failure (logged).
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

    // Accepts both reference forms: "Assets/...path" and "guid:<32 hex>".
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

        // An image asset requested as a cubemap (Texture3D) is treated as an equirect
        // panorama — lets a .hdr/.exr drop straight into a Skybox slot.
        var isImage = extension is ".png" or ".jpg" or ".jpeg" or ".tga" or ".bmp" or ".hdr" or ".exr" or ".dds";
        if (isImage && typeof(Texture3D).IsAssignableFrom(requestedType))
            return EquirectCubemapLoader.Load(pipeline, guid, assetPath);

        return extension switch {
            ".fbx" or ".obj" or ".gltf" or ".glb" or ".dae" => MeshLoader.Load(pipeline, guid, assetPath),
            ".wav" or ".wave" => AudioClipLoader.Load(pipeline, guid, assetPath),
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
