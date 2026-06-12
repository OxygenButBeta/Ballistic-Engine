using BallisticEngine.Serialization;

namespace BallisticEngine.Editor;

// Computes which members of a prefab-instance entity DIFFER from its source .prefab — Unity's
// "override" tracking, the blue-bar markers in the inspector. The instance and the prefab root are
// both captured as EntityDocuments and compared member-by-member; a member whose serialized value
// differs is an override. The result is cached per (entity, prefab) and recomputed when the inspector
// asks for a fresh selection, so the diff cost is paid once per selection, not per frame.
//
// Comparison is by SERIALIZED value (each member re-serialized to a YAML scalar/string), so it's
// type-agnostic: floats, vectors, enums, and asset refs all compare correctly without bespoke logic.
internal static class PrefabOverrides {
    // Keys that are intrinsically per-instance and must NEVER show as overrides (Unity excludes the
    // root transform position/rotation from the "modifications" list conceptually, but we DO track
    // them — they're the most common legit override. We only exclude identity/bookkeeping fields).
    static Entity cachedEntity;
    static Guid cachedPrefab;
    static HashSet<string> overrides = new();   // "Transform.Position", "Type#index.MemberName"
    static bool valid;

    // The override key for a component member. index disambiguates multiple components of one type.
    public static string Key(string componentType, int typeIndex, string member) =>
        $"{componentType}#{typeIndex}.{member}";

    public const string TransformPositionKey = "Transform.Position";
    public const string TransformRotationKey = "Transform.Rotation";
    public const string TransformScaleKey = "Transform.Scale";

    // Recompute the override set for `entity` if it changed selection. Safe to call every inspector
    // build; it no-ops when the cache is still valid for this entity+prefab.
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

    // True if ANY member of the given component (registry type name + type index) is overridden.
    public static bool ComponentHasOverride(string componentType, int typeIndex) {
        string prefix = $"{componentType}#{typeIndex}.";
        foreach (string k in overrides)
            if (k.StartsWith(prefix, StringComparison.Ordinal)) return true;
        return false;
    }

    public static bool HasAnyOverride => overrides.Count > 0;

    // Marks a key as no-longer-overridden after a per-member revert (so the marker clears without a
    // full recompute). The next selection change recomputes from scratch anyway.
    public static void ClearKey(string key) => overrides.Remove(key);

    // Invalidates the cache (e.g. after Apply/Revert mutated the prefab or the instance).
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

    // Pair components by type, in order, so two BoxColliders compare 0-to-0 and 1-to-1. A component
    // present on the instance but not the prefab (added override) marks ALL its members overridden.
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
                // Whole component is an addition — every member counts as an override.
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

    static bool Equal(OpenTK.Mathematics.Vector3 a, OpenTK.Mathematics.Vector3 b) =>
        (a - b).LengthSquared < 1e-10f;
    static bool Equal(OpenTK.Mathematics.Quaternion a, OpenTK.Mathematics.Quaternion b) =>
        MathF.Abs(a.X - b.X) < 1e-5f && MathF.Abs(a.Y - b.Y) < 1e-5f &&
        MathF.Abs(a.Z - b.Z) < 1e-5f && MathF.Abs(a.W - b.W) < 1e-5f;

    // Serialized-value equality: compare the two members' YAML projections. Robust for any member
    // type the scene serializer already handles (scalars, vectors, enums, asset-ref guids, lists).
    static bool ValueEqual(object a, object b) {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        try { return SceneYaml.Serializer.Serialize(a) == SceneYaml.Serializer.Serialize(b); }
        catch { return Equals(a, b); }
    }
}
