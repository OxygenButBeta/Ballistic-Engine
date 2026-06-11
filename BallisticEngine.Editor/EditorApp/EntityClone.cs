using BallisticEngine.Serialization;

namespace BallisticEngine.Editor;

// Duplicates an entity AND its transform descendants (the engine has no clone path of its
// own). Copies the transform and every component, copying each component's serializable
// members via ComponentReflection — the same member set the inspector and serializer agree on,
// so what you see is what gets duplicated. Asset references are shared (shallow), which is
// correct: a duplicate should point at the same meshes and materials, not deep-copy them.
internal static class EntityClone {
    public static Entity Duplicate(Scene scene, Entity source) {
        // Snapshot BEFORE cloning starts so freshly created copies are never re-visited.
        var all = scene.Entities.ToArray();

        Entity copy = CloneSingle(scene, source);
        copy.transform.SetParent(source.transform.Parent); // sibling of the original
        CloneChildren(scene, source, copy, all);
        return copy;
    }

    static void CloneChildren(Scene scene, Entity source, Entity copyParent, Entity[] all) {
        foreach (Entity child in all) {
            if (!ReferenceEquals(child.transform.Parent, source.transform))
                continue;
            Entity childCopy = CloneSingle(scene, child);
            childCopy.transform.SetParent(copyParent.transform);
            CloneChildren(scene, child, childCopy, all);
        }
    }

    static Entity CloneSingle(Scene scene, Entity source) {
        Entity copy = scene.CreateEntity(source.Name);

        copy.transform.Position = source.transform.Position;
        copy.transform.Rotation = source.transform.Rotation;
        copy.transform.Scale = source.transform.Scale;
        if (!source.IsActive)
            copy.SetActive(false);

        foreach (Behaviour behaviour in source.Behaviours) {
            Behaviour added = copy.AddComponent(behaviour.GetType());
            added.IsEnabled = behaviour.IsEnabled;
            foreach (var member in ComponentReflection.SerializableMembers(behaviour.GetType()))
                ComponentReflection.SetValue(member, added, ComponentReflection.GetValue(member, behaviour));
        }

        return copy;
    }
}
