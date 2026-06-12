using BallisticEngine.Serialization;

namespace BallisticEngine.Editor;

// Phase 4 of the prefab system: when a .prefab asset changes (edited in the browser, or another
// instance Applied to it), push the new definition into every LIVE instance in the open scene WHILE
// PRESERVING that instance's own overrides. Wired to AsyncAssetImport.AfterRefresh, so it runs once
// per asset refresh on the main thread.
//
// The merge is value-based and idempotent: for each instance we build a target document = the prefab
// root with the instance's overridden members spliced back in. If that target already equals the
// instance's current state, nothing changed (prefab untouched, or only overrides differ) and we skip
// it — so a refresh that didn't touch the prefab causes no teardown/flicker.
internal static class PrefabPropagation {
    static bool reentryGuard;

    public static void PropagateAll() {
        if (reentryGuard) return;          // RestoreEntityInPlace must not re-trigger us mid-pass
        Scene scene = SceneManager.GetCurrentScene();
        if (scene is null) return;

        reentryGuard = true;
        try {
            // Snapshot the list — RestoreEntityInPlace rebuilds components but not the entity set.
            foreach (Entity entity in scene.Entities.ToArray()) {
                if (entity is null || entity.IsDestroyed || !entity.IsPrefabInstance) continue;
                PropagateOne(entity);
            }
        }
        finally {
            reentryGuard = false;
            PrefabOverrides.Invalidate();   // the inspector recomputes the diff next selection
        }
    }

    static void PropagateOne(Entity entity) {
        PrefabAsset prefab = AssetDatabase.Load<PrefabAsset>(entity.PrefabSource);
        if (prefab is null || prefab.Entities.Count == 0) return;

        EntityDocument prefabRoot = prefab.Entities[0];
        EntityDocument current = SceneSerializer.CaptureEntity(entity);
        if (current is null) return;

        EntityDocument target = BuildMerged(prefabRoot, current);

        // Idempotency check: if the merged target is identical to the instance's current state, the
        // prefab didn't change anything this instance doesn't already override — skip the rebuild.
        if (DocumentsEqual(target, current)) return;

        Guid link = entity.PrefabSource;
        if (SceneSerializer.RestoreEntityInPlace(entity, target))
            entity.PrefabSource = link;     // restore the link the prefab/target doc doesn't carry
    }

    // The prefab root with the instance's OVERRIDDEN members kept. Transform channels (pos/rot/scale)
    // and component members that differ in `current` vs the prefab are treated as overrides and copied
    // from `current`; everything else takes the prefab's (possibly updated) value.
    static EntityDocument BuildMerged(EntityDocument prefabRoot, EntityDocument current) {
        var merged = new EntityDocument {
            Id = current.Id,                       // keep the instance's file-local id
            Name = current.Name,                   // name is treated as an instance-level override
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
                Parent = current.Transform.Parent, // never overwrite the scene parent link
            },
            Components = MergeComponents(prefabRoot.Components, current.Components),
        };
        return merged;
    }

    // Pair components by type+index. For a matched pair, start from the prefab component and override
    // each member whose value differs in the instance. Components only on the instance (added override)
    // are kept; components only on the prefab (new in the asset) are added fresh.
    static List<ComponentDocument> MergeComponents(List<ComponentDocument> prefab, List<ComponentDocument> current) {
        var result = new List<ComponentDocument>();
        var curByType = GroupByType(current);
        var prefabByType = GroupByType(prefab);
        var emittedCur = new HashSet<ComponentDocument>();

        // Walk the prefab order first (so new prefab components land in their authored order).
        var prefabIndex = new Dictionary<string, int>();
        foreach (ComponentDocument p in prefab) {
            int idx = prefabIndex.TryGetValue(p.Type, out int i) ? i : 0;
            prefabIndex[p.Type] = idx + 1;
            ComponentDocument cur = curByType.TryGetValue(p.Type, out var cl) && idx < cl.Count ? cl[idx] : null;
            if (cur is null) { result.Add(Clone(p)); continue; }   // prefab has it, instance lost it: re-add
            emittedCur.Add(cur);
            result.Add(MergeComponent(p, cur));
        }

        // Append components the instance has beyond the prefab's (pure additions / extra of a type).
        foreach (ComponentDocument c in current)
            if (!emittedCur.Contains(c)) result.Add(Clone(c));

        return result;
    }

    static ComponentDocument MergeComponent(ComponentDocument prefab, ComponentDocument cur) {
        var doc = new ComponentDocument {
            Type = prefab.Type,
            Id = cur.Id,                            // keep the instance's component identity (BEvent refs)
            Enabled = cur.Enabled,                  // enabled state is an instance override
            Members = new Dictionary<string, object>(),
        };
        // Union of member keys: prefab value unless the instance overrides it.
        var keys = new HashSet<string>(prefab.Members.Keys);
        foreach (string k in cur.Members.Keys) keys.Add(k);
        foreach (string k in keys) {
            object pv = prefab.Members.GetValueOrDefault(k);
            object cv = cur.Members.GetValueOrDefault(k);
            bool curHas = cur.Members.ContainsKey(k);
            bool prefabHas = prefab.Members.ContainsKey(k);
            // Override if the instance has the key and its value differs from the prefab's.
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

    static bool Differs(OpenTK.Mathematics.Vector3 a, OpenTK.Mathematics.Vector3 b) =>
        (a - b).LengthSquared >= 1e-10f;
    static bool Differs(OpenTK.Mathematics.Quaternion a, OpenTK.Mathematics.Quaternion b) =>
        MathF.Abs(a.X - b.X) >= 1e-5f || MathF.Abs(a.Y - b.Y) >= 1e-5f ||
        MathF.Abs(a.Z - b.Z) >= 1e-5f || MathF.Abs(a.W - b.W) >= 1e-5f;

    static bool ValueEqual(object a, object b) {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        try { return SceneYaml.Serializer.Serialize(a) == SceneYaml.Serializer.Serialize(b); }
        catch { return Equals(a, b); }
    }

    // Whole-document equality via YAML projection — used for the idempotency skip.
    static bool DocumentsEqual(EntityDocument a, EntityDocument b) {
        try { return SceneYaml.Serializer.Serialize(a) == SceneYaml.Serializer.Serialize(b); }
        catch { return false; }
    }
}
