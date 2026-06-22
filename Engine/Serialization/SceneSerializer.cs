using System.Globalization;
using System.Reflection;
using BallisticEngine.AssetPipeline;

namespace BallisticEngine.Serialization;

public static class SceneSerializer {
    public static string Serialize(Scene scene) {
        var doc = new SceneDocument { Name = scene.Name };

        foreach (SceneBehaviour behaviour in scene.SceneBehaviours)
            doc.SceneComponents.Add(
                BuildComponentDocument(ComponentRegistry.SceneNameOf(behaviour), behaviour.IsEnabled, behaviour));

        foreach (Entity entity in scene.Entities) {
            doc.Entities.Add(BuildEntityDocument(entity));
        }

        return SceneYaml.Serializer.Serialize(doc);
    }

    public static void Save(Scene scene, string absolutePath) {
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        File.WriteAllText(absolutePath, Serialize(scene));
    }

    public static List<EntityDocument> CaptureSubtree(Entity root) {
        var docs = new List<EntityDocument>();
        if (root is null)
            return docs;

        var subtree = new List<Entity> { root };
        foreach (Entity e in SceneManager.GetCurrentScene().Entities)
            if (!ReferenceEquals(e, root) && e.transform.IsDescendantOf(root.transform))
                subtree.Add(e);

        foreach (Entity e in subtree) {
            EntityDocument doc = BuildEntityDocument(e);
            if (ReferenceEquals(e, root))
                doc.Transform.Parent = null;
            doc.PrefabSource = null;
            docs.Add(doc);
        }
        return docs;
    }

    public static Entity InstantiateSubtree(IReadOnlyList<EntityDocument> docs) {
        if (docs is null || docs.Count == 0)
            return null;

        var byId = new Dictionary<string, Entity>(StringComparer.Ordinal);
        Entity root = null;

        foreach (EntityDocument entityDoc in docs) {
            Entity entity = Entity.Instantiate(entityDoc.Name ?? "Entity", entityDoc.IsActive);
            entity.Tag = string.IsNullOrEmpty(entityDoc.Tag) ? TagManager.Untagged : entityDoc.Tag;
            entity.Layer = entityDoc.Layer;
            entity.PrefabSource = Guid.TryParseExact(entityDoc.PrefabSource, "N", out Guid pfg) ? pfg : Guid.Empty;
            entity.transform.Position = entityDoc.Transform.Position;
            entity.transform.Rotation = entityDoc.Transform.Rotation;
            entity.transform.Scale = entityDoc.Transform.Scale;

            if (entityDoc.Id is not null)
                byId[entityDoc.Id] = entity;
            root ??= entity;

            foreach (ComponentDocument componentDoc in entityDoc.Components)
                ApplyComponent(entity, componentDoc);
        }

        foreach (EntityDocument entityDoc in docs) {
            if (entityDoc.Transform.Parent is null || entityDoc.Id is null)
                continue;
            if (byId.TryGetValue(entityDoc.Id, out Entity child) &&
                byId.TryGetValue(entityDoc.Transform.Parent, out Entity parent))
                child.transform.SetParent(parent.transform);
        }

        return root;
    }

    static EntityDocument BuildEntityDocument(Entity entity) {
        var doc = new EntityDocument {
            Id = entity.InstanceId.ToString("N"),
            Name = entity.Name,
            IsActive = entity.IsActive,
            Tag = entity.Tag == TagManager.Untagged ? null : entity.Tag,
            Layer = entity.Layer,
            PrefabSource = entity.PrefabSource == Guid.Empty ? null : entity.PrefabSource.ToString("N"),
            Transform = new TransformDocument {
                Position = entity.transform.Position,
                Rotation = entity.transform.Rotation,
                Scale = entity.transform.Scale,
                Parent = entity.transform.Parent?.Entity?.InstanceId.ToString("N"),
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
            Id = target is BObject obj ? obj.InstanceId.ToString("N") : null,
            Enabled = enabled,
        };

        foreach (MemberInfo member in SerializableMembers(target.GetType())) {
            object value = GetMemberValue(member, target);
            object serialized = SerializeMemberValue(value, MemberType(member), member, visited: null);
            if (serialized is not null)
                doc.Members[CamelCase(member.Name)] = serialized;
            else if (value is not null && !IsNoneSceneObjectRef(value)) WarnDroppedMember(target.GetType(), member, value);
        }

        return doc;
    }

    static readonly HashSet<string> _reportedDrops = new(StringComparer.Ordinal);

    static void WarnDroppedMember(Type ownerType, MemberInfo member, object value) {
        string key = $"{ownerType.FullName}.{member.Name}";
        lock (_reportedDrops) {
            if (!_reportedDrops.Add(key))
                return;
        }

        string reason = value is BObject
            ? $"a reference of type '{value.GetType().Name}' has no asset guid (scene-object/unsaved refs do not round-trip yet)"
            : $"its type '{value.GetType().Name}' has no serialized form";
        Debugging.LogWarning(
            $"Scene save dropped {ownerType.Name}.{member.Name}: {reason}. The value will be lost on reload.");
    }

    static object SerializeValue(object value) {
        if (value is null)
            return null;

        if (value is System.Collections.IEnumerable features && IsRenderFeatureList(value.GetType()))
            return SerializeFeatureList(features);

        if (value is BEvent evt)
            return BEventYaml.Serialize(evt);

        if (value is AnimationCurve curve)
            return curve.ToCompactString();

        if (value is ColorGradient gradient)
            return gradient.ToCompactString();

        if (value is EntityRef entityRef)
            return entityRef.InstanceId == Guid.Empty ? null : entityRef.InstanceId.ToString("N");
        if (value is ComponentRef componentRef)
            return componentRef.InstanceId == Guid.Empty ? null : componentRef.InstanceId.ToString("N");

        if (value is BObject asset) {
            return AssetDatabase.TryGetAssetGuid(asset, out Guid guid)
                ? AssetRef.FromGuid(guid)
                : null;
        }

        if (value is System.Collections.IDictionary dict)
            return SerializeDictionary(dict);
        if (value is not string && value is System.Collections.IEnumerable seq)
            return SerializeSequence(seq);

        return value;
    }

    static object SerializeMemberValue(object value, Type declaredType, MemberInfo member, HashSet<object> visited) {
        if (value is null)
            return null;

        PropertyCategory category = PropertyCategories.Classify(declaredType, member);
        if (category == PropertyCategory.Polymorphic)
            return SerializeReferenceInstance(value, visited);
        if (category == PropertyCategory.Collection &&
            value is System.Collections.IEnumerable polySeq &&
            IsPolymorphicElementMember(declaredType, member))
            return SerializeSequencePolymorphic(polySeq, visited);
        if (category == PropertyCategory.Nested)
            return SerializeNestedInstance(value, visited);
        return SerializeValue(value);
    }

    const string TypeTag = "$type";

    static Dictionary<object, object> SerializeReferenceInstance(object instance, HashSet<object> visited) {
        visited ??= new HashSet<object>(ReferenceEqualityComparer.Instance);
        if (!visited.Add(instance))
            return null;

        try {
            Type concrete = instance.GetType();
            var map = new Dictionary<object, object> {
                [TypeTag] = concrete.FullName,
            };

            foreach (MemberInfo member in SerializableMembers(concrete)) {
                object value = GetMemberValue(member, instance);
                object serialized = SerializeMemberValue(value, MemberType(member), member, visited);
                if (serialized is not null)
                    map[CamelCase(member.Name)] = serialized;
            }
            return map;
        }
        finally {
            visited.Remove(instance);
        }
    }

    static Dictionary<object, object> SerializeNestedInstance(object instance, HashSet<object> visited) {
        bool guard = !instance.GetType().IsValueType;
        if (guard) {
            visited ??= new HashSet<object>(ReferenceEqualityComparer.Instance);
            if (!visited.Add(instance))
                return null;
        }

        try {
            var map = new Dictionary<object, object>();
            foreach (MemberInfo member in SerializableMembers(instance.GetType())) {
                object value = GetMemberValue(member, instance);
                object serialized = SerializeMemberValue(value, MemberType(member), member, visited);
                if (serialized is not null)
                    map[CamelCase(member.Name)] = serialized;
            }
            return map;
        }
        finally {
            if (guard)
                visited.Remove(instance);
        }
    }

    static List<object> SerializeSequence(System.Collections.IEnumerable seq) {
        var items = new List<object>();
        foreach (object element in seq)
            items.Add(SerializeValue(element));
        return items;
    }

    static List<object> SerializeSequencePolymorphic(System.Collections.IEnumerable seq, HashSet<object> visited) {
        var items = new List<object>();
        foreach (object element in seq)
            items.Add(element is null ? null : SerializeReferenceInstance(element, visited));
        return items;
    }

    static bool IsPolymorphicElementMember(Type declaredType, MemberInfo member) {
        if (member is null ||
            member.GetCustomAttribute<SerializeReferenceAttribute>() is null)
            return false;
        Type element = SequenceElementType(declaredType);
        if (element is null)
            return false;
        return !IsLeafElementType(element);
    }

    static Type SequenceElementType(Type t) {
        if (t is null) return null;
        if (t.IsArray) return t.GetElementType();
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>))
            return t.GetGenericArguments()[0];
        return null;
    }

    static bool IsLeafElementType(Type element) {
        PropertyCategory c = PropertyCategories.Classify(element);
        return c is PropertyCategory.Primitive or PropertyCategory.Enum or PropertyCategory.MathStruct
            or PropertyCategory.AssetRef or PropertyCategory.SceneObjectRef;
    }

    static Dictionary<object, object> SerializeDictionary(System.Collections.IDictionary dict) {
        var map = new Dictionary<object, object>();
        foreach (System.Collections.DictionaryEntry e in dict) {
            object key = SerializeValue(e.Key);
            if (key is null)
                continue;
            map[key] = SerializeValue(e.Value);
        }
        return map;
    }

    public static void Deserialize(string yaml) {
        SceneManager.SuppressPlayLifecycle = true;
        try {
            DeserializeCore(yaml);
        }
        finally {
            SceneManager.SuppressPlayLifecycle = false;
        }
    }

    static void DeserializeCore(string yaml) {
        SceneDocument doc = SceneYaml.Deserializer.Deserialize<SceneDocument>(yaml);
        if (doc?.Entities is null)
            return;

        Scene scene = SceneManager.GetCurrentScene();
        if (!string.IsNullOrEmpty(doc.Name))
            scene.Name = doc.Name;

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

        var byId = new Dictionary<string, Entity>(StringComparer.Ordinal);

        foreach (EntityDocument entityDoc in doc.Entities) {
            Entity entity = Entity.Instantiate(entityDoc.Name ?? "Entity", entityDoc.IsActive);
            entity.Tag = string.IsNullOrEmpty(entityDoc.Tag) ? TagManager.Untagged : entityDoc.Tag;
            entity.Layer = entityDoc.Layer;
            entity.PrefabSource = Guid.TryParseExact(entityDoc.PrefabSource, "N", out Guid pfg) ? pfg : Guid.Empty;
            entity.transform.Position = entityDoc.Transform.Position;
            entity.transform.Rotation = entityDoc.Transform.Rotation;
            entity.transform.Scale = entityDoc.Transform.Scale;

            if (entityDoc.Id is not null && Guid.TryParseExact(entityDoc.Id, "N", out Guid restoredId))
                entity.InstanceId = restoredId;

            if (entityDoc.Id is not null)
                byId[entityDoc.Id] = entity;

            foreach (ComponentDocument componentDoc in entityDoc.Components)
                ApplyComponent(entity, componentDoc);
        }

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

    public static EntityDocument CaptureEntity(Entity entity) =>
        entity is null ? null : BuildEntityDocument(entity);

    public static bool RestoreEntityInPlace(Entity entity, EntityDocument doc) {
        if (entity is null || doc is null || entity.IsDestroyed)
            return false;

        SceneManager.SuppressPlayLifecycle = true;
        try {
            foreach (Behaviour behaviour in entity.Behaviours.ToArray())
                entity.RemoveComponent(behaviour);

            entity.Name = doc.Name ?? entity.Name;
            entity.Tag = string.IsNullOrEmpty(doc.Tag) ? TagManager.Untagged : doc.Tag;
            entity.Layer = doc.Layer;
            entity.PrefabSource = Guid.TryParseExact(doc.PrefabSource, "N", out Guid pfg) ? pfg : Guid.Empty;
            entity.transform.Position = doc.Transform.Position;
            entity.transform.Rotation = doc.Transform.Rotation;
            entity.transform.Scale = doc.Transform.Scale;
            if (doc.IsActive != entity.IsActive)
                entity.SetActive(doc.IsActive);

            foreach (ComponentDocument componentDoc in doc.Components)
                ApplyComponent(entity, componentDoc);
        }
        finally {
            SceneManager.SuppressPlayLifecycle = false;
        }
        return true;
    }

    static void ApplyComponent(Entity entity, ComponentDocument doc) {
        Type type = ComponentRegistry.Resolve(doc.Type);
        if (type is null) {
            Debugging.LogWarning($"Unknown component '{doc.Type}' on '{entity.Name}'; skipped.");
            return;
        }

        Behaviour behaviour = entity.AddComponent(type);
        behaviour.IsEnabled = doc.Enabled;
        if (doc.Id is not null && Guid.TryParseExact(doc.Id, "N", out Guid restoredId))
            behaviour.InstanceId = restoredId;
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

            if (typeof(BEvent).IsAssignableFrom(memberType)) {
                if (GetMemberValue(member, target) is BEvent evt)
                    BEventYaml.Deserialize(raw, evt);
                continue;
            }

            object value = DeserializeValue(raw, memberType, member);
            if (value is not null)
                SetMemberValue(member, target, value);
        }
    }

    static object DeserializeValue(object raw, Type targetType, MemberInfo member = null) {
        if (raw is null)
            return null;

        if (IsRenderFeatureList(targetType))
            return DeserializeFeatureList(raw);

        if (TryDeserializeReferenceInstance(raw, targetType, member, out object polymorphic))
            return polymorphic;

        if (targetType == typeof(EntityRef))
            return new EntityRef(ParseInstanceId(raw));
        if (targetType == typeof(ComponentRef))
            return new ComponentRef(ParseInstanceId(raw));

        if (typeof(BObject).IsAssignableFrom(targetType))
            return raw is string reference ? LoadAsset(reference, targetType) : null;

        if (targetType == typeof(AnimationCurve))
            return raw is string curveStr ? AnimationCurve.Parse(curveStr) : null;

        if (targetType == typeof(ColorGradient))
            return raw is string gradientStr ? ColorGradient.Parse(gradientStr) : null;

        if (targetType.IsArray)
            return DeserializeArray(raw, targetType.GetElementType());
        if (TryGetListElementType(targetType, out Type listElem))
            return DeserializeList(raw, targetType, listElem);
        if (TryGetDictionaryTypes(targetType, out Type keyType, out Type valType))
            return DeserializeDictionary(raw, targetType, keyType, valType);

        if (TryDeserializeNestedInstance(raw, targetType, member, out object nested))
            return nested;

        if (targetType.IsInstanceOfType(raw))
            return raw;

        if (raw is IDictionary<object, object> map) {
            if (targetType == typeof(Vector2)) return new Vector2(MapF(map, "x"), MapF(map, "y"));
            if (targetType == typeof(Vector3)) return new Vector3(MapF(map, "x"), MapF(map, "y"), MapF(map, "z"));
            if (targetType == typeof(Vector4)) return new Vector4(MapF(map, "x"), MapF(map, "y"), MapF(map, "z"), MapF(map, "w"));
            if (targetType == typeof(Quaternion)) return new Quaternion(MapF(map, "x"), MapF(map, "y"), MapF(map, "z"), MapF(map, "w"));
        }

        return Coerce(raw, targetType);
    }

    static bool TryDeserializeReferenceInstance(object raw, Type targetType, MemberInfo member, out object result) {
        result = null;

        bool scalarPolymorphic = PropertyCategories.Classify(targetType, member) == PropertyCategory.Polymorphic;
        bool tagged = raw is IDictionary<object, object> probe &&
                      probe.TryGetValue(TypeTag, out object t) && t is string;
        bool elementPolymorphic = member is null && tagged && IsPolymorphicBaseTarget(targetType);
        if (!scalarPolymorphic && !elementPolymorphic)
            return false;

        if (raw is not IDictionary<object, object> map ||
            !map.TryGetValue(TypeTag, out object tagObj) || tagObj is not string typeName)
            return false;

        Type concrete = ResolvePolymorphicType(typeName, targetType);
        if (concrete is null) {
            Debugging.LogWarning(
                $"[SerializeReference] could not resolve concrete type '{typeName}' for '{targetType.Name}'; " +
                "the value is dropped (the type was renamed or removed).");
            result = null;
            return true;
        }

        object instance = Activator.CreateInstance(concrete);
        var members = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach ((object k, object v) in map)
            if (k is string key && key != TypeTag)
                members[key] = v;

        ApplyMembers(instance, concrete, members);
        result = instance;
        return true;
    }

    static bool IsPolymorphicBaseTarget(Type targetType) {
        if (targetType is null) return false;
        if (targetType.IsAbstract || targetType.IsInterface) return true;
        return targetType.IsClass && !IsLeafElementType(targetType);
    }

    static Type ResolvePolymorphicType(string fullName, Type declaredType) {
        foreach (Type t in TypeCache.GetTypesDerivedFrom(declaredType))
            if (string.Equals(t.FullName, fullName, StringComparison.Ordinal))
                return t;
        return null;
    }

    static bool TryDeserializeNestedInstance(object raw, Type targetType, MemberInfo member, out object result) {
        result = null;

        if (PropertyCategories.Classify(targetType, member) != PropertyCategory.Nested)
            return false;

        if (raw is not IDictionary<object, object> rawMap)
            return false;

        object instance;
        try {
            instance = Activator.CreateInstance(targetType);
        }
        catch (Exception ex) {
            Debugging.LogWarning(
                $"Nested member type '{targetType.Name}' could not be instantiated ({ex.GetType().Name}); " +
                "the value is dropped (it needs a public parameterless constructor).");
            result = null;
            return true;
        }

        var members = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach ((object k, object v) in rawMap)
            if (k is string key)
                members[key] = v;

        ApplyMembers(instance, targetType, members);
        result = instance;
        return true;
    }

    static float MapF(IDictionary<object, object> map, string key) =>
        map.TryGetValue(key, out object v) && v is not null &&
        float.TryParse(v.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : 0f;

    static object DeserializeArray(object raw, Type elementType) {
        List<object> items = RawItems(raw);
        Array array = Array.CreateInstance(elementType, items.Count);
        for (int i = 0; i < items.Count; i++)
            array.SetValue(DeserializeValue(items[i], elementType), i);
        return array;
    }

    static object DeserializeList(object raw, Type targetType, Type elementType) {
        List<object> items = RawItems(raw);
        var list = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(elementType))!;
        foreach (object item in items)
            list.Add(DeserializeValue(item, elementType));
        return list;
    }

    static object DeserializeDictionary(object raw, Type targetType, Type keyType, Type valType) {
        var dict = (System.Collections.IDictionary)Activator.CreateInstance(
            typeof(Dictionary<,>).MakeGenericType(keyType, valType))!;
        if (raw is IDictionary<object, object> map) {
            foreach ((object k, object v) in map) {
                object key = DeserializeValue(k, keyType);
                if (key is null)
                    continue;
                dict[key] = DeserializeValue(v, valType);
            }
        }
        return dict;
    }

    static List<object> RawItems(object raw) {
        if (raw is List<object> direct)
            return direct;
        var items = new List<object>();
        if (raw is not string && raw is System.Collections.IEnumerable e && raw is not IDictionary<object, object>)
            foreach (object item in e)
                items.Add(item);
        return items;
    }

    static bool TryGetListElementType(Type t, out Type elementType) {
        elementType = null;
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>)) {
            elementType = t.GetGenericArguments()[0];
            return true;
        }
        return false;
    }

    static bool TryGetDictionaryTypes(Type t, out Type keyType, out Type valueType) {
        keyType = valueType = null;
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Dictionary<,>)) {
            Type[] args = t.GetGenericArguments();
            keyType = args[0];
            valueType = args[1];
            return true;
        }
        return false;
    }

    static object LoadAsset(string reference, Type targetType) {
        MethodInfo loadRef = typeof(AssetDatabase).GetMethod(nameof(AssetDatabase.LoadRef))!
            .MakeGenericMethod(targetType);
        return loadRef.Invoke(null, [reference]);
    }

    static bool IsNoneSceneObjectRef(object value) =>
        value is EntityRef { HasValue: false } or ComponentRef { HasValue: false };

    static Guid ParseInstanceId(object raw) =>
        raw is string s && Guid.TryParseExact(s, "N", out Guid id) ? id : Guid.Empty;

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

    static bool IsRenderFeatureList(Type type) =>
        type.IsGenericType &&
        type.GetGenericTypeDefinition() == typeof(List<>) &&
        typeof(RenderFeature).IsAssignableFrom(type.GetGenericArguments()[0]);

    static object SerializeFeatureList(System.Collections.IEnumerable features) {
        var list = new List<object>();
        foreach (object item in features) {
            if (item is not RenderFeature feature)
                continue;
            var entry = new Dictionary<string, object> {
                ["type"] = ComponentRegistry.FeatureNameOf(feature), ["active"] = feature.Active,
            };
            var members = new Dictionary<string, object>();
            foreach (MemberInfo member in SerializableMembers(feature.GetType())) {
                if (member.Name == nameof(RenderFeature.Active))
                    continue;
                object serialized = SerializeValue(GetMemberValue(member, feature));
                if (serialized is not null)
                    members[CamelCase(member.Name)] = serialized;
            }
            if (members.Count > 0)
                entry["members"] = members;
            list.Add(entry);
        }
        return list;
    }

    static object DeserializeFeatureList(object raw) {
        var result = new List<RenderFeature>();
        if (raw is not System.Collections.IEnumerable entries)
            return result;

        foreach (object entryObj in entries) {
            if (entryObj is not IDictionary<object, object> entry)
                continue;

            string typeName = MapStr(entry, "type");
            Type type = ComponentRegistry.ResolveFeature(typeName);
            if (type is null) {
                Debugging.LogWarning($"Unknown render feature '{typeName}'; skipped.");
                continue;
            }

            var feature = (RenderFeature)Activator.CreateInstance(type);
            if (entry.TryGetValue("active", out object activeRaw) && activeRaw is not null &&
                bool.TryParse(activeRaw.ToString(), out bool active))
                feature.Active = active;

            if (entry.TryGetValue("members", out object membersRaw) &&
                membersRaw is IDictionary<object, object> memberMap) {
                var members = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach ((object k, object v) in memberMap)
                    if (k is not null)
                        members[k.ToString()] = v;
                ApplyMembers(feature, type, members);
            }

            result.Add(feature);
        }
        return result;
    }

    static string MapStr(IDictionary<object, object> map, string key) =>
        map.TryGetValue(key, out object v) && v is not null ? v.ToString() : null;

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
