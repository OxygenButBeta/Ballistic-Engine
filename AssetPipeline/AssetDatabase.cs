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
    }

    public static RefreshResult Refresh() => pipeline.Refresh();

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
        var isImage = extension is ".png" or ".jpg" or ".jpeg" or ".tga" or ".bmp" or ".hdr" or ".exr";
        if (isImage && typeof(Texture3D).IsAssignableFrom(requestedType))
            return EquirectCubemapLoader.Load(pipeline, guid, assetPath);

        return extension switch {
            ".fbx" or ".obj" => MeshLoader.Load(pipeline, guid, assetPath),
            _ when isImage => TextureLoader.Load(pipeline, guid, assetPath),
            ".shader" => ShaderProgramLoader.Load(Project, assetPath),
            ".mat" => MaterialLoader.Load(Project, assetPath),
            ".cubemap" => CubemapLoader.Load(Project, pipeline, assetPath),
            _ => NotLoadable(assetPath, extension),
        };
    }

    static BObject NotLoadable(string assetPath, string extension) {
        Debugging.LogError($"'{assetPath}': no loader for '{extension}' assets.");
        return null;
    }

    static string Normalize(string assetPath) => assetPath?.Replace('\\', '/');
}
