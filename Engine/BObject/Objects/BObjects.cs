namespace BallisticEngine;

// Unity's static `Object` API (here named `BObjects` to avoid colliding with System.Object and the
// engine's BObject base). Scene-wide lookups and the canonical Instantiate/Destroy entry points
// game scripts reach for. All operate on the CURRENT scene.
//
// Game scripts can `using static BallisticEngine.BObjects;` to write FindObjectOfType<T>() and
// Destroy(entity) bare, matching Unity. Entity.Instantiate / Scene.DestroyEntity remain the lower
// layer these forward to.
public static class BObjects {
    // ---- Find -------------------------------------------------------------------------------

    // First active component of type T anywhere in the scene (Unity's FindObjectOfType). T may be
    // a Behaviour subtype OR an interface implemented by behaviours. Linear scan — cache the result
    // for anything hot; this is for setup/wiring, not per-frame use.
    public static T FindObjectOfType<T>(bool includeInactive = false) where T : class {
        foreach (Entity entity in SceneManager.GetCurrentScene().Entities) {
            if (entity.IsDestroyed)
                continue;
            if (!includeInactive && !entity.IsActiveInHierarchy)
                continue;
            foreach (Behaviour behaviour in entity.Behaviours)
                if (behaviour is T t && (includeInactive || behaviour.IsEnabled))
                    return t;
        }
        return null;
    }

    // All components of type T in the scene.
    public static List<T> FindObjectsOfType<T>(bool includeInactive = false) where T : class {
        var result = new List<T>();
        foreach (Entity entity in SceneManager.GetCurrentScene().Entities) {
            if (entity.IsDestroyed)
                continue;
            if (!includeInactive && !entity.IsActiveInHierarchy)
                continue;
            foreach (Behaviour behaviour in entity.Behaviours)
                if (behaviour is T t && (includeInactive || behaviour.IsEnabled))
                    result.Add(t);
        }
        return result;
    }

    // First entity with the given name (Unity's GameObject.Find). Exact, case-sensitive match;
    // returns null if none. Searches active and inactive entities (Unity's Find skips inactive,
    // but our scenes are small and "why is my disabled object not found" is a worse footgun).
    public static Entity Find(string name) {
        if (string.IsNullOrEmpty(name))
            return null;
        foreach (Entity entity in SceneManager.GetCurrentScene().Entities)
            if (!entity.IsDestroyed && entity.Name == name)
                return entity;
        return null;
    }

    // All entities carrying the given tag (see Entity.Tag / TagManager).
    public static List<Entity> FindWithTag(string tag) {
        var result = new List<Entity>();
        if (string.IsNullOrEmpty(tag))
            return result;
        foreach (Entity entity in SceneManager.GetCurrentScene().Entities)
            if (!entity.IsDestroyed && entity.CompareTag(tag))
                result.Add(entity);
        return result;
    }

    // First entity with the given tag, or null.
    public static Entity FindFirstWithTag(string tag) {
        if (string.IsNullOrEmpty(tag))
            return null;
        foreach (Entity entity in SceneManager.GetCurrentScene().Entities)
            if (!entity.IsDestroyed && entity.CompareTag(tag))
                return entity;
        return null;
    }

    // ---- Instantiate / Destroy --------------------------------------------------------------

    // Unity's Instantiate(prefab): deep-clones a prefab asset into the current scene and returns
    // the new root entity. Forwards to the prefab system (PrefabAsset.Instantiate).
    public static Entity Instantiate(PrefabAsset prefab) => prefab?.Instantiate();

    public static Entity Instantiate(PrefabAsset prefab, Vector3 position,
        Quaternion rotation) => prefab?.Instantiate(position, rotation);

    // Destroys an entity and its descendants (Unity's Destroy(gameObject)). In edit mode this is
    // immediate; in play mode the entity is flagged and torn down by the scene.
    public static void Destroy(Entity entity) => SceneManager.GetCurrentScene().DestroyEntity(entity);

    // Destroys a single component (Unity's Destroy(component)).
    public static void Destroy(Behaviour component) => component?.Entity?.RemoveComponent(component);
}
