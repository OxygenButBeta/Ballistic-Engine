using BallisticEngine.Serialization;

namespace BallisticEngine.Editor;

internal static class PrefabInstanceOps {
    public static void RevertAll(Entity entity) {
        if (entity is null || !entity.IsPrefabInstance) return;
        PrefabAsset prefab = AssetDatabase.Load<PrefabAsset>(entity.PrefabSource);
        if (prefab is null || prefab.Entities.Count == 0) {
            Debugging.LogWarning("Revert: source prefab is missing.");
            return;
        }

        EditorUndo.PushEntity("Revert Prefab Instance", entity);

        Guid link = entity.PrefabSource;
        EntityDocument prefabRoot = CloneRootForInstance(prefab.Entities[0]);
        if (!SceneSerializer.RestoreEntityInPlace(entity, prefabRoot)) {
            Debugging.LogWarning("Revert: entity no longer exists.");
            return;
        }
        entity.PrefabSource = link;
        PrefabOverrides.Invalidate();
    }

    public static void ApplyAll(Entity entity) {
        if (entity is null || !entity.IsPrefabInstance) return;
        string path = AssetDatabase.GuidToAssetPath(entity.PrefabSource);
        if (path is null) { Debugging.LogWarning("Apply: source prefab is missing."); return; }

        try {
            PrefabAsset updated = PrefabAsset.FromEntity(entity);
            string abs = AssetDatabase.Project.ResolveAbsolute(path);
            File.WriteAllText(abs, updated.ToYaml());
            AsyncAssetImport.Request("Applying to prefab...", onFinished: PrefabOverrides.Invalidate);
        }
        catch (Exception e) {
            Debugging.LogError($"Apply to prefab failed: {e.Message}");
        }
    }

    static EntityDocument CloneRootForInstance(EntityDocument prefabRoot) {
        return new EntityDocument {
            Id = prefabRoot.Id,
            Name = prefabRoot.Name,
            IsActive = prefabRoot.IsActive,
            Tag = prefabRoot.Tag,
            Layer = prefabRoot.Layer,
            PrefabSource = null,
            Transform = prefabRoot.Transform,
            Components = prefabRoot.Components,
        };
    }
}
