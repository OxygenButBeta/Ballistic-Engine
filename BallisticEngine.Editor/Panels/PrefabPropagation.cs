using BallisticEngine.Serialization;

namespace BallisticEngine.Editor;

internal static class PrefabPropagation {
    static bool reentryGuard;

    public static void PropagateAll() {
        if (reentryGuard) return;
        Scene scene = SceneManager.GetCurrentScene();
        if (scene is null) return;

        reentryGuard = true;
        try {
            foreach (Entity entity in scene.Entities.ToArray()) {
                if (entity is null || entity.IsDestroyed || !entity.IsPrefabInstance) continue;
                PropagateOne(entity);
            }
        }
        finally {
            reentryGuard = false;
            PrefabOverrides.Invalidate();
        }
    }

    static void PropagateOne(Entity entity) {
        PrefabAsset prefab = AssetDatabase.Load<PrefabAsset>(entity.PrefabSource);
        if (prefab is null || prefab.Entities.Count == 0) return;

        EntityDocument prefabRoot = prefab.Entities[0];
        EntityDocument current = SceneSerializer.CaptureEntity(entity);
        if (current is null) return;

        EntityDocument target = BuildMerged(prefabRoot, current);

        if (DocumentsEqual(target, current)) return;

        Guid link = entity.PrefabSource;
        if (SceneSerializer.RestoreEntityInPlace(entity, target))
            entity.PrefabSource = link;
    }

    static EntityDocument BuildMerged(EntityDocument prefabRoot, EntityDocument current) {
        var merged = new EntityDocument {
            Id = current.Id,
            Name = current.Name,
            IsActive = current.IsActive,
            Tag = current.Tag,
            Layer = current.Layer,
            PrefabSource = null,
            Transform = new TransformDocument {
                Position = Differs(current.Transform.Position, prefabRoot.Transform.Position)
                    ? current.Transform.Position : prefabRoot.Transform.Position,
                Rotation = Differs(current.Transform.Rotation, prefabRoot.Transform.Rotation)
                    ? current.Transform.Rotation : prefabRoot.Transform.Rotation,
                Scale = Differs(current.Transform.Scale, prefabRoot.Transform.Scale)
                    ? current.Transform.Scale : prefabRoot.Transform.Scale,
                Parent = current.Transform.Parent,
            },
            Components = MergeComponents(prefabRoot.Components, current.Components),
        };
        return merged;
    }

    static List<ComponentDocument> MergeComponents(List<ComponentDocument> prefab, List<ComponentDocument> current) {
        var result = new List<ComponentDocument>();
        var curByType = GroupByType(current);
        var prefabByType = GroupByType(prefab);
        var emittedCur = new HashSet<ComponentDocument>();

        var prefabIndex = new Dictionary<string, int>();
        foreach (ComponentDocument p in prefab) {
            int idx = prefabIndex.TryGetValue(p.Type, out int i) ? i : 0;
            prefabIndex[p.Type] = idx + 1;
            ComponentDocument cur = curByType.TryGetValue(p.Type, out var cl) && idx < cl.Count ? cl[idx] : null;
            if (cur is null) { result.Add(Clone(p)); continue; }

            emittedCur.Add(cur);
            result.Add(MergeComponent(p, cur));
        }

        foreach (ComponentDocument c in current)
            if (!emittedCur.Contains(c)) result.Add(Clone(c));

        return result;
    }

    static ComponentDocument MergeComponent(ComponentDocument prefab, ComponentDocument cur) {
        var doc = new ComponentDocument {
            Type = prefab.Type,
            Id = cur.Id,
            Enabled = cur.Enabled,
            Members = new Dictionary<string, object>(),
        };
        var keys = new HashSet<string>(prefab.Members.Keys);
        foreach (string k in cur.Members.Keys) keys.Add(k);
        foreach (string k in keys) {
            object pv = prefab.Members.GetValueOrDefault(k);
            object cv = cur.Members.GetValueOrDefault(k);
            bool curHas = cur.Members.ContainsKey(k);
            bool prefabHas = prefab.Members.ContainsKey(k);
            doc.Members[k] = (curHas && (!prefabHas || !ValueEqual(cv, pv))) ? cv : pv;
        }
        return doc;
    }

    static Dictionary<string, List<ComponentDocument>> GroupByType(List<ComponentDocument> list) {
        var map = new Dictionary<string, List<ComponentDocument>>();
        foreach (ComponentDocument c in list) {
            if (!map.TryGetValue(c.Type, out var l)) map[c.Type] = l = new();
            l.Add(c);
        }
        return map;
    }

    static ComponentDocument Clone(ComponentDocument c) => new() {
        Type = c.Type, Id = c.Id, Enabled = c.Enabled,
        Members = new Dictionary<string, object>(c.Members),
    };

    static bool Differs(Vector3 a, Vector3 b) =>
        (a - b).LengthSquared() >= 1e-10f;
    static bool Differs(Quaternion a, Quaternion b) =>
        MathF.Abs(a.X - b.X) >= 1e-5f || MathF.Abs(a.Y - b.Y) >= 1e-5f ||
        MathF.Abs(a.Z - b.Z) >= 1e-5f || MathF.Abs(a.W - b.W) >= 1e-5f;

    static bool ValueEqual(object a, object b) {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        try { return SceneYaml.Serializer.Serialize(a) == SceneYaml.Serializer.Serialize(b); }
        catch { return Equals(a, b); }
    }

    static bool DocumentsEqual(EntityDocument a, EntityDocument b) {
        try { return SceneYaml.Serializer.Serialize(a) == SceneYaml.Serializer.Serialize(b); }
        catch { return false; }
    }
}
