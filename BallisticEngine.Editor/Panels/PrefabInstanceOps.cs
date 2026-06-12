using BallisticEngine.Serialization;

namespace BallisticEngine.Editor;

// Apply / Revert for prefab instances (Unity's prefab instance operations). RevertAll discards an
// instance's overrides by restoring it from the source .prefab; ApplyAll pushes the instance's current
// state back into the .prefab asset (so every other instance picks it up on reimport/propagation).
//
// v1 scope: ROOT-level apply/revert on the root entity's own transform + components (not its child
// subtree — the captured prefab may have children, but in-place restore here operates on the root
// document; child propagation rides Phase 4). The prefab link is preserved across both operations.
internal static class PrefabInstanceOps {
    // Restore this instance's root from the prefab definition, discarding all overrides. The Entity
    // object (identity/selection) and its scene parent are preserved; the prefab link is re-stamped
    // (the prefab document itself carries no link). Pushes a scoped undo first.
    public static void RevertAll(Entity entity) {
        if (entity is null || !entity.IsPrefabInstance) return;
        PrefabAsset prefab = AssetDatabase.Load<PrefabAsset>(entity.PrefabSource);
        if (prefab is null || prefab.Entities.Count == 0) {
            Debugging.LogWarning("Revert: source prefab is missing.");
            return;
        }

        // Scoped undo: snapshot the instance before we tear it down (single-entity scope, so Ctrl+Z
        // brings the overrides back without a whole-scene rebuild).
        EditorUndo.PushEntity("Revert Prefab Instance", entity);

        Guid link = entity.PrefabSource;
        EntityDocument prefabRoot = CloneRootForInstance(prefab.Entities[0]);
        if (!SceneSerializer.RestoreEntityInPlace(entity, prefabRoot)) {
            Debugging.LogWarning("Revert: entity no longer exists.");
            return;
        }
        entity.PrefabSource = link;            // restore the link the prefab doc doesn't carry
        PrefabOverrides.Invalidate();
    }

    // Write this instance's current root state back into the .prefab asset, so the asset matches the
    // instance and all overrides clear. Reimports the asset afterward.
    public static void ApplyAll(Entity entity) {
        if (entity is null || !entity.IsPrefabInstance) return;
        string path = AssetDatabase.GuidToAssetPath(entity.PrefabSource);
        if (path is null) { Debugging.LogWarning("Apply: source prefab is missing."); return; }

        try {
            // Capture the WHOLE subtree (root + children) so applying carries structural state too.
            PrefabAsset updated = PrefabAsset.FromEntity(entity);
            string abs = AssetDatabase.Project.ResolveAbsolute(path);
            File.WriteAllText(abs, updated.ToYaml());
            AsyncAssetImport.Request("Applying to prefab...", onFinished: PrefabOverrides.Invalidate);
        }
        catch (Exception e) {
            Debugging.LogError($"Apply to prefab failed: {e.Message}");
        }
    }

    // The prefab root document plants children/parent of the LIVE entity, so strip the document's own
    // parent ref (the instance keeps its real scene parent) before restoring in place. A shallow copy
    // is enough — RestoreEntityInPlace only reads it.
    static EntityDocument CloneRootForInstance(EntityDocument prefabRoot) {
        return new EntityDocument {
            Id = prefabRoot.Id,
            Name = prefabRoot.Name,
            IsActive = prefabRoot.IsActive,
            Tag = prefabRoot.Tag,
            Layer = prefabRoot.Layer,
            PrefabSource = null,
            Transform = prefabRoot.Transform,   // restore prefab's authored local transform
            Components = prefabRoot.Components,
        };
    }
}
