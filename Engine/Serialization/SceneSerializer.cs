using System.Globalization;
using System.Reflection;
using BallisticEngine.AssetPipeline;
using OpenTK.Mathematics;

namespace BallisticEngine.Serialization;

// Reflection-based scene <-> YAML serialization.
//
// Per component: the registry name + IsEnabled + every public read/write property and public
// field. Asset references (Mesh/Material and other loaded BObjects) serialize as "guid:..."
// via AssetDatabase.TryGetAssetGuid and load back via AssetDatabase.LoadRef. OpenTK math types
// round-trip through the SceneYaml converters. Transform parents are wired by file-local id.
public static class SceneSerializer {

    // ---- Serialize ---------------------------------------------------------

    public static string Serialize(Scene scene) {
        var doc = new SceneDocument { Name = scene.Name };

        foreach (SceneBehaviour behaviour in scene.SceneBehaviours)
            doc.SceneComponents.Add(
                BuildComponentDocument(ComponentRegistry.SceneNameOf(behaviour), behaviour.IsEnabled, behaviour));

        // Stable file-local ids from the real InstanceIds (for parent wiring).
        foreach (Entity entity in scene.Entities) {
            doc.Entities.Add(BuildEntityDocument(entity));
        }

        return SceneYaml.Serializer.Serialize(doc);
    }

    public static void Save(Scene scene, string absolutePath) {
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        File.WriteAllText(absolutePath, Serialize(scene));
    }

    static EntityDocument BuildEntityDocument(Entity entity) {
        var doc = new EntityDocument {
            Id = entity.InstanceId.ToString("N"),
            Name = entity.Name,
            IsActive = entity.IsActive,
            Transform = new TransformDocument {
                Position = entity.transform.Position,
                Rotation = entity.transform.Rotation,
                Scale = entity.transform.Scale,
                Parent = entity.transform.Parent?.InstanceId.ToString("N"),
            },
        };

        foreach (Behaviour behaviour in entity.Behaviours)
            doc.Components.Add(BuildComponentDocument(behaviour));

        return doc;
    }

    static ComponentDocument BuildComponentDocument(Behaviour behaviour) =>
        BuildComponentDocument(ComponentRegistry.NameOf(behaviour), behaviour.IsEnabled, behaviour);

    static ComponentDocument BuildComponentDocument(string typeName, bool enabled, object target) {
        var doc = new ComponentDocument {
            Type = typeName,
            Enabled = enabled,
        };

        foreach (MemberInfo member in SerializableMembers(target.GetType())) {
            object value = GetMemberValue(member, target);
            object serialized = SerializeValue(value);
            if (serialized is not null)
                doc.Members[CamelCase(member.Name)] = serialized;
        }

        return doc;
    }

    // BObject -> "guid:..."; everything else passes through (converters handle OpenTK types).
    static object SerializeValue(object value) {
        if (value is null)
            return null;

        if (value is BObject asset) {
            return AssetDatabase.TryGetAssetGuid(asset, out Guid guid)
                ? AssetRef.FromGuid(guid)
                : null; // unsaved/asset-less object reference — skip
        }

        return value;
    }

    // ---- Deserialize -------------------------------------------------------

    // Builds entities in the current scene in EDIT mode (no lifecycle). Caller is the editor or
    // the runtime startup path; play is entered separately via SceneManager.StartPlay.
    public static void Deserialize(string yaml) {
        SceneDocument doc = SceneYaml.Deserializer.Deserialize<SceneDocument>(yaml);
        if (doc?.Entities is null)
            return;

        Scene scene = SceneManager.GetCurrentScene();
        if (!string.IsNullOrEmpty(doc.Name))
            scene.Name = doc.Name;

        // Scene-wide components first (skybox etc.).
        foreach (ComponentDocument componentDoc in doc.SceneComponents ?? []) {
            Type type = ComponentRegistry.ResolveScene(componentDoc.Type);
            if (type is null) {
                Debugging.LogWarning($"Unknown scene component '{componentDoc.Type}'; skipped.");
                continue;
            }

            SceneBehaviour behaviour = scene.AddSceneBehaviour(type);
            behaviour.IsEnabled = componentDoc.Enabled;
            ApplyMembers(behaviour, type, componentDoc.Members);
        }

        // id (file-local) -> live entity, for parent resolution in a second pass.
        var byId = new Dictionary<string, Entity>(StringComparer.Ordinal);

        foreach (EntityDocument entityDoc in doc.Entities) {
            Entity entity = Entity.Instantiate(entityDoc.Name ?? "Entity", entityDoc.IsActive);
            entity.transform.Position = entityDoc.Transform.Position;
            entity.transform.Rotation = entityDoc.Transform.Rotation;
            entity.transform.Scale = entityDoc.Transform.Scale;

            if (entityDoc.Id is not null)
                byId[entityDoc.Id] = entity;

            foreach (ComponentDocument componentDoc in entityDoc.Components)
                ApplyComponent(entity, componentDoc);
        }

        // Second pass: parents.
        foreach (EntityDocument entityDoc in doc.Entities) {
            if (entityDoc.Transform.Parent is null || entityDoc.Id is null)
                continue;
            if (byId.TryGetValue(entityDoc.Id, out Entity child) &&
                byId.TryGetValue(entityDoc.Transform.Parent, out Entity parent))
                child.transform.SetParent(parent.transform);
        }
    }

    public static void Load(string absolutePath) {
        if (!File.Exists(absolutePath)) {
            Debugging.LogError($"Scene file not found: '{absolutePath}'.");
            return;
        }

        Deserialize(File.ReadAllText(absolutePath));
    }

    static void ApplyComponent(Entity entity, ComponentDocument doc) {
        Type type = ComponentRegistry.Resolve(doc.Type);
        if (type is null) {
            Debugging.LogWarning($"Unknown component '{doc.Type}' on '{entity.Name}'; skipped.");
            return;
        }

        Behaviour behaviour = entity.AddComponent(type);
        behaviour.IsEnabled = doc.Enabled;
        ApplyMembers(behaviour, type, doc.Members);
    }

    static void ApplyMembers(object target, Type type, Dictionary<string, object> members) {
        if (members is null)
            return;

        var membersByName = SerializableMembers(type)
            .ToDictionary(m => CamelCase(m.Name), StringComparer.OrdinalIgnoreCase);

        foreach ((string name, object raw) in members) {
            if (!membersByName.TryGetValue(name, out MemberInfo member))
                continue;

            Type memberType = MemberType(member);
            object value = DeserializeValue(raw, memberType);
            if (value is not null)
                SetMemberValue(member, target, value);
        }
    }

    // Inverse of SerializeValue. Asset refs (string -> BObject) load via AssetDatabase.
    static object DeserializeValue(object raw, Type targetType) {
        if (raw is null)
            return null;

        if (typeof(BObject).IsAssignableFrom(targetType))
            return raw is string reference ? LoadAsset(reference, targetType) : null;

        // OpenTK types arrive already converted; otherwise coerce the scalar to the member type.
        if (targetType.IsInstanceOfType(raw))
            return raw;

        return Coerce(raw, targetType);
    }

    static object LoadAsset(string reference, Type targetType) {
        MethodInfo loadRef = typeof(AssetDatabase).GetMethod(nameof(AssetDatabase.LoadRef))!
            .MakeGenericMethod(targetType);
        return loadRef.Invoke(null, [reference]);
    }

    static object Coerce(object raw, Type targetType) {
        try {
            if (targetType.IsEnum)
                return Enum.Parse(targetType, raw.ToString()!, ignoreCase: true);

            return Convert.ChangeType(raw, targetType, CultureInfo.InvariantCulture);
        }
        catch {
            return null;
        }
    }

    // ---- Member reflection (shared with the editor inspector) --------------

    static IEnumerable<MemberInfo> SerializableMembers(Type type) =>
        ComponentReflection.SerializableMembers(type);

    static Type MemberType(MemberInfo member) => ComponentReflection.MemberType(member);

    static object GetMemberValue(MemberInfo member, object target) =>
        ComponentReflection.GetValue(member, target);

    static void SetMemberValue(MemberInfo member, object target, object value) =>
        ComponentReflection.SetValue(member, target, value);

    static string CamelCase(string name) =>
        string.IsNullOrEmpty(name) || char.IsLower(name[0])
            ? name
            : char.ToLowerInvariant(name[0]) + name[1..];
}
