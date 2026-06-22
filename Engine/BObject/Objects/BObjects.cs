namespace BallisticEngine;

public static class BObjects {
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

    public static Entity Find(string name) {
        if (string.IsNullOrEmpty(name))
            return null;
        foreach (Entity entity in SceneManager.GetCurrentScene().Entities)
            if (!entity.IsDestroyed && entity.Name == name)
                return entity;
        return null;
    }

    public static List<Entity> FindWithTag(string tag) {
        var result = new List<Entity>();
        if (string.IsNullOrEmpty(tag))
            return result;
        foreach (Entity entity in SceneManager.GetCurrentScene().Entities)
            if (!entity.IsDestroyed && entity.CompareTag(tag))
                result.Add(entity);
        return result;
    }

    public static Entity FindFirstWithTag(string tag) {
        if (string.IsNullOrEmpty(tag))
            return null;
        foreach (Entity entity in SceneManager.GetCurrentScene().Entities)
            if (!entity.IsDestroyed && entity.CompareTag(tag))
                return entity;
        return null;
    }

    public static Entity Instantiate(PrefabAsset prefab) => prefab?.Instantiate();

    public static Entity Instantiate(PrefabAsset prefab, Vector3 position,
        Quaternion rotation) => prefab?.Instantiate(position, rotation);

    public static void Destroy(Entity entity) => SceneManager.GetCurrentScene().DestroyEntity(entity);

    public static void Destroy(Behaviour component) => component?.Entity?.RemoveComponent(component);
}
