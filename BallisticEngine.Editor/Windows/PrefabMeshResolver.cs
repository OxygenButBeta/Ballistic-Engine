using BallisticEngine.AssetPipeline;
using BallisticEngine.AssetPipeline.Loaders;
using BallisticEngine.AssetPipeline.Unity;

namespace BallisticEngine.Editor;

internal sealed class PrefabMeshResolver(
    Dictionary<string, string> guidToFile, BallisticProject project, UnityMaterialGenerator materials) {
    readonly Dictionary<string, string> meshCache = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> matCache = new(StringComparer.OrdinalIgnoreCase);

    public string Resolve(string prefabGuid) => Cached(meshCache, prefabGuid, ResolveMeshUncached);
    public string ResolveMaterial(string prefabGuid) => Cached(matCache, prefabGuid, ResolveMaterialUncached);

    static string Cached(Dictionary<string, string> cache, string key, Func<string, string> fn) {
        if (string.IsNullOrEmpty(key)) return null;
        if (cache.TryGetValue(key, out var c)) return c;
        var r = fn(key);
        cache[key] = r;
        return r;
    }

    string ResolveMeshUncached(string prefabGuid) {
        UnityRef mesh = Lod0MeshFilter(prefabGuid)?.Mesh ?? default;
        if (mesh.IsNull || !mesh.IsExternal) return null;
        return UnityImportWindow.GuidToProjectRef(mesh.Guid, guidToFile, project);
    }

    string ResolveMaterialUncached(string prefabGuid) {
        UnityYamlScene prefab = LoadPrefab(prefabGuid);
        if (prefab is null) return null;
        var goName = ToGameObjectName(prefab);
        UnityRef best = default;
        foreach (UnityMeshRenderer mr in prefab.MeshRenderers.Values) {
            if (mr.Materials.Count == 0 || !mr.Materials[0].IsExternal) continue;
            var name = goName.GetValueOrDefault(mr.GameObjectId, "");
            if (name.EndsWith("_LOD0", StringComparison.OrdinalIgnoreCase)) { best = mr.Materials[0]; break; }
            if (best.IsNull) best = mr.Materials[0];
        }
        return best.IsNull ? null : materials.Resolve(best.Guid);
    }

    UnityMeshFilter Lod0MeshFilter(string prefabGuid) {
        UnityYamlScene prefab = LoadPrefab(prefabGuid);
        if (prefab is null) return null;
        var goName = ToGameObjectName(prefab);
        UnityMeshFilter fallback = null;
        foreach (UnityMeshFilter mf in prefab.MeshFilters.Values) {
            if (!mf.Mesh.IsExternal || mf.Mesh.Guid is null) continue;
            var name = goName.GetValueOrDefault(mf.GameObjectId, "");
            if (name.EndsWith("_LOD0", StringComparison.OrdinalIgnoreCase)) return mf;
            fallback ??= mf;
        }
        return fallback;
    }

    readonly Dictionary<string, UnityYamlScene> prefabCache = new(StringComparer.OrdinalIgnoreCase);
    UnityYamlScene LoadPrefab(string prefabGuid) {
        if (prefabCache.TryGetValue(prefabGuid, out var cached)) return cached;
        UnityYamlScene prefab = null;
        if (guidToFile.TryGetValue(prefabGuid, out var path) && File.Exists(path)) {
            try { prefab = UnityYamlParser.Parse(File.ReadAllText(path)); }
            catch { prefab = null; }
        }
        prefabCache[prefabGuid] = prefab;
        return prefab;
    }

    static Dictionary<long, string> ToGameObjectName(UnityYamlScene s) {
        var map = new Dictionary<long, string>();
        foreach (UnityGameObject go in s.GameObjects.Values)
            map[go.FileId] = go.Name ?? "";
        return map;
    }
}
