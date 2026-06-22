using BallisticEngine.Serialization;

namespace BallisticEngine.Editor;

internal static class PrefabOverrides {
    static Entity cachedEntity;
    static Guid cachedPrefab;
    static HashSet<string> overrides = new();
    static bool valid;

    public static string Key(string componentType, int typeIndex, string member) =>
        $"{componentType}#{typeIndex}.{member}";

    public const string TransformPositionKey = "Transform.Position";
    public const string TransformRotationKey = "Transform.Rotation";
    public const string TransformScaleKey = "Transform.Scale";

    public static void Refresh(Entity entity) {
        if (entity is null || !entity.IsPrefabInstance) { Clear(); return; }
        if (valid && ReferenceEquals(cachedEntity, entity) && cachedPrefab == entity.PrefabSource)
            return;

        Clear();
        cachedEntity = entity;
        cachedPrefab = entity.PrefabSource;

        PrefabAsset prefab = AssetDatabase.Load<PrefabAsset>(entity.PrefabSource);
        if (prefab is null || prefab.Entities.Count == 0) { valid = true; return; }

        EntityDocument prefabRoot = prefab.Entities[0];
        EntityDocument instance = SceneSerializer.CaptureEntity(entity);
        if (instance is null) { valid = true; return; }

        DiffTransform(instance.Transform, prefabRoot.Transform);
        DiffComponents(instance.Components, prefabRoot.Components);
        valid = true;
    }

    public static bool IsOverridden(string key) => overrides.Contains(key);

    public static bool ComponentHasOverride(string componentType, int typeIndex) {
        string prefix = $"{componentType}#{typeIndex}.";
        foreach (string k in overrides)
            if (k.StartsWith(prefix, StringComparison.Ordinal)) return true;
        return false;
    }

    public static bool HasAnyOverride => overrides.Count > 0;

    public static void ClearKey(string key) => overrides.Remove(key);

    public static void Invalidate() => valid = false;

    static void Clear() {
        overrides = new HashSet<string>();
        cachedEntity = null;
        cachedPrefab = Guid.Empty;
        valid = false;
    }

    static void DiffTransform(TransformDocument a, TransformDocument b) {
        if (!Equal(a.Position, b.Position)) overrides.Add(TransformPositionKey);
        if (!Equal(a.Rotation, b.Rotation)) overrides.Add(TransformRotationKey);
        if (!Equal(a.Scale, b.Scale)) overrides.Add(TransformScaleKey);
    }

    static void DiffComponents(List<ComponentDocument> instance, List<ComponentDocument> prefab) {
        var prefabByType = new Dictionary<string, List<ComponentDocument>>();
        foreach (ComponentDocument c in prefab) {
            if (!prefabByType.TryGetValue(c.Type, out var list)) prefabByType[c.Type] = list = new();
            list.Add(c);
        }
        var indexByType = new Dictionary<string, int>();

        foreach (ComponentDocument inst in instance) {
            int idx = indexByType.TryGetValue(inst.Type, out int i) ? i : 0;
            indexByType[inst.Type] = idx + 1;

            ComponentDocument match = prefabByType.TryGetValue(inst.Type, out var list) && idx < list.Count
                ? list[idx] : null;

            if (match is null) {
                foreach (var kv in inst.Members)
                    overrides.Add(Key(inst.Type, idx, kv.Key));
                continue;
            }
            foreach (var kv in inst.Members) {
                object prefabVal = match.Members.TryGetValue(kv.Key, out object pv) ? pv : null;
                if (!ValueEqual(kv.Value, prefabVal))
                    overrides.Add(Key(inst.Type, idx, kv.Key));
            }
        }
    }

    static bool Equal(Vector3 a, Vector3 b) =>
        (a - b).LengthSquared() < 1e-10f;
    static bool Equal(Quaternion a, Quaternion b) =>
        MathF.Abs(a.X - b.X) < 1e-5f && MathF.Abs(a.Y - b.Y) < 1e-5f &&
        MathF.Abs(a.Z - b.Z) < 1e-5f && MathF.Abs(a.W - b.W) < 1e-5f;

    static bool ValueEqual(object a, object b) {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        try { return SceneYaml.Serializer.Serialize(a) == SceneYaml.Serializer.Serialize(b); }
        catch { return Equals(a, b); }
    }
}
