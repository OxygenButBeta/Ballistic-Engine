using BallisticEngine.Serialization;

namespace BallisticEngine.Editor;

internal static class EntityClone {
    public static Entity Duplicate(Scene scene, Entity source) {
        var all = scene.Entities.ToArray();

        Entity copy = CloneSingle(scene, source);
        copy.transform.SetParent(source.transform.Parent);
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
        copy.Tag = source.Tag;
        copy.Layer = source.Layer;
        copy.PrefabSource = source.PrefabSource;
        if (!source.IsActive)
            copy.SetActive(false);

        foreach (Behaviour behaviour in source.Behaviours) {
            Behaviour added = copy.AddComponent(behaviour.GetType());
            added.IsEnabled = behaviour.IsEnabled;
            foreach (var member in ComponentReflection.SerializableMembers(behaviour.GetType()))
                ComponentReflection.SetValue(member, added, CloneMemberValue(ComponentReflection.GetValue(member, behaviour)));
        }

        return copy;
    }

    static object CloneMemberValue(object value) => value switch {
        AnimationCurve c => AnimationCurve.Parse(c.ToCompactString()),
        ColorGradient g => ColorGradient.Parse(g.ToCompactString()),
        _ => value,
    };
}
