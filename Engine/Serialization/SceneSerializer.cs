using System.Globalization;
using System.Reflection;
using BallisticEngine.AssetPipeline;

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

    // ---- Subtree capture / rebuild (shared with the prefab system) ---------

    // Captures an entity AND its transform descendants into EntityDocuments (the prefab snapshot).
    // The root's Parent ref is cleared so the subtree is self-contained; internal parent links are
    // preserved by file-local id.
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
            // Root: drop the external parent so the prefab plants at the world origin on instantiate.
            if (ReferenceEquals(e, root))
                doc.Transform.Parent = null;
            // A prefab DEFINITION never stores a prefab link (that would self-reference / nest links).
            // The instance's link is assigned by PrefabAsset.Instantiate, not baked into the .prefab.
            doc.PrefabSource = null;
            docs.Add(doc);
        }
        return docs;
    }

    // Rebuilds a captured subtree into the current scene and returns its ROOT entity. Fresh instance
    // ids are assigned (a prefab instantiated twice must not share identity). Lifecycle fires through
    // the normal AddComponent path unless a deserialize is already suppressing it.
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
            root ??= entity; // first doc is the root (CaptureSubtree emits it first)

            foreach (ComponentDocument componentDoc in entityDoc.Components)
                ApplyComponent(entity, componentDoc);
        }

        // Re-wire internal parent links (root's Parent was cleared at capture).
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
            // Omit defaults so unauthored entities don't churn the YAML.
            Tag = entity.Tag == TagManager.Untagged ? null : entity.Tag,
            Layer = entity.Layer,
            PrefabSource = entity.PrefabSource == Guid.Empty ? null : entity.PrefabSource.ToString("N"),
            Transform = new TransformDocument {
                Position = entity.transform.Position,
                Rotation = entity.transform.Rotation,
                Scale = entity.transform.Scale,
                // The parent ref must be the parent ENTITY's id (byId is keyed by entity id) — NOT the
                // parent Transform's own InstanceId. They differ (Transform is itself a BObject), so
                // serializing the transform id left every parent lookup failing and hierarchy flat.
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
            // Component identity round-trips so BEvent listeners that target this component (by id)
            // rebind after reload/undo. Only BObjects carry an InstanceId.
            Id = target is BObject obj ? obj.InstanceId.ToString("N") : null,
            Enabled = enabled,
        };

        foreach (MemberInfo member in SerializableMembers(target.GetType())) {
            object value = GetMemberValue(member, target);
            object serialized = SerializeMemberValue(value, MemberType(member), member, visited: null);
            if (serialized is not null)
                doc.Members[CamelCase(member.Name)] = serialized;
            else if (value is not null && !IsNoneSceneObjectRef(value))
                // G0 (loud drops): the member HELD a value but serialized to null, so it silently
                // vanishes on save/load. Make the data loss visible instead of dropping it quietly
                // (the §3.45 silent-failure trap). Deduped per (type, member) so the per-frame undo
                // snapshots don't spam — the FIRST drop is enough to flag the hole.
                WarnDroppedMember(target.GetType(), member, value);
        }

        return doc;
    }

    // Members that held a non-null value but produced no serialized form are reported ONCE each, so a
    // forgotten reference (entity/component ref without a guid, an unsupported member type) is loud,
    // not a silent round-trip loss. The dedup key is the declaring type + member name.
    static readonly HashSet<string> _reportedDrops = new(StringComparer.Ordinal);

    static void WarnDroppedMember(Type ownerType, MemberInfo member, object value) {
        string key = $"{ownerType.FullName}.{member.Name}";
        lock (_reportedDrops) {
            if (!_reportedDrops.Add(key))
                return;
        }

        string reason = value is BObject
            // Entity/Behaviour (and unsaved assets) are BObjects with no AssetDatabase guid, so there
            // is no ref form to write yet — G1 (EntityRef) will close this for scene-object refs.
            ? $"a reference of type '{value.GetType().Name}' has no asset guid (scene-object/unsaved refs do not round-trip yet)"
            : $"its type '{value.GetType().Name}' has no serialized form";
        Debugging.LogWarning(
            $"Scene save dropped {ownerType.Name}.{member.Name}: {reason}. The value will be lost on reload.");
    }

    // BObject -> "guid:..."; BEvent -> a listener list; everything else passes through (converters
    // handle OpenTK types).
    static object SerializeValue(object value) {
        if (value is null)
            return null;

        // Polymorphic render-feature list (phase-3 chunk 21 / design §5 D3): the RenderFeatures
        // SceneBehaviour's `List<RenderFeature> Features` round-trips as an ORDERED YAML list of
        // {type, active, members} entries — type-name via FeatureNameOf, members reflected exactly like
        // a Behaviour's. The generic SerializeValue path can't do this (a List<abstractType> has no
        // type discriminator), so it's handled here. Order is preserved (a List, not a set). The same
        // {type, members} shape any other List<RenderFeature> member would use, so this is generic to
        // the element TYPE, not the one member name.
        if (value is System.Collections.IEnumerable features && IsRenderFeatureList(value.GetType()))
            return SerializeFeatureList(features);

        if (value is BEvent evt)
            return BEventYaml.Serialize(evt);

        // AnimationCurve serializes as a single compact string scalar (sidesteps the nested-list
        // serializer); DeserializeValue parses it back.
        if (value is AnimationCurve curve)
            return curve.ToCompactString();

        // ColorGradient — same compact-string scalar approach as AnimationCurve.
        if (value is ColorGradient gradient)
            return gradient.ToCompactString();

        // Scene-object references (EntityRef/ComponentRef) round-trip as the target InstanceId hex,
        // NOT a guid: a scene object has no AssetDatabase guid (it is built at scene load), so it is
        // identified by InstanceId the way BEvent stores its persistent-listener targets. Guid.Empty
        // means "None" and serializes to null (skipped, like an unset asset ref). MUST come BEFORE the
        // BObject case below; an EntityRef/ComponentRef is a value type so it never reaches that case,
        // but the loud-drop path keys off SerializeValue returning null for a SET ref, so be explicit.
        if (value is EntityRef entityRef)
            return entityRef.InstanceId == Guid.Empty ? null : entityRef.InstanceId.ToString("N");
        if (value is ComponentRef componentRef)
            return componentRef.InstanceId == Guid.Empty ? null : componentRef.InstanceId.ToString("N");

        if (value is BObject asset) {
            return AssetDatabase.TryGetAssetGuid(asset, out Guid guid)
                ? AssetRef.FromGuid(guid)
                : null; // unsaved/asset-less object reference -- skip
        }

        // Collections (G2): List<T> / arrays / Dictionary<K,V> round-trip as a YAML sequence (or mapping
        // for a dict) by recursing EACH element through SerializeValue -- so an element can itself be a
        // primitive, a math struct (the converters fire on the boxed runtime type), an asset/scene ref, or
        // a nested struct/class. An EMPTY collection still serializes (an empty sequence) so an authored
        // empty list round-trips as empty, not null. A null collection is the leaf-null case the caller
        // already handles (skipped from the doc). MUST come AFTER BObject so a BObject-derived value never
        // reaches here, and is guarded to collection types so every existing non-collection member is
        // byte-identical (the only behaviour change is that a List<T>/array/dict member -- e.g.
        // LineRenderer.Points -- now ROUND-TRIPS instead of deserializing to null).
        if (value is System.Collections.IDictionary dict)
            return SerializeDictionary(dict);
        if (value is not string && value is System.Collections.IEnumerable seq)
            return SerializeSequence(seq);

        return value;
    }

    // ---- [SerializeReference] polymorphism (G3 engine-half) -----------------
    // A member whose DECLARED type is abstract/interface (or a base marked [SerializeReference]) stores its
    // live CONCRETE type so a derived implementation round-trips (Unity's [SerializeReference]). Only such a
    // member emits a $type tag; every existing non-polymorphic member (primitive/struct/asset/scene-ref/
    // collection/dict/nested) goes straight to SerializeValue UNCHANGED, so all existing scenes are
    // byte-identical (no shipped component carries [SerializeReference], and a concrete-typed member never
    // classifies Polymorphic). The trigger is exactly PropertyCategories.Classify == Polymorphic, so the
    // serializer and the property model agree on what is polymorphic by construction.
    static object SerializeMemberValue(object value, Type declaredType, MemberInfo member, HashSet<object> visited) {
        if (value is null)
            return null;

        PropertyCategory category = PropertyCategories.Classify(declaredType, member);
        if (category == PropertyCategory.Polymorphic)
            return SerializeReferenceInstance(value, visited);
        // Nested struct/class (G4): a plain (non-[SerializeReference]) member whose declared type is a
        // concrete class or non-primitive struct round-trips its serializable members as a YAML mapping --
        // NO $type tag (the declared type IS the concrete type, picked on load via Activator). The trigger is
        // exactly Classify == Nested, so the model and codec agree. Before G4 this rode the SerializeValue
        // pass-through (YamlDotNet reflected the object's PUBLIC members), which serialized but deserialized
        // to null (the round-trip was lost); now the member-map below pairs with TryDeserializeNestedInstance
        // so a nested member ROUND-TRIPS. Recurses through SerializeMemberValue so a nested-in-nested member
        // (or a nested member that is itself polymorphic/a collection/a ref) is handled by the same path.
        if (category == PropertyCategory.Nested)
            return SerializeNestedInstance(value, visited);
        return SerializeValue(value);
    }

    // The $type discriminator key. A leading '$' can't collide with a CamelCase member name (members never
    // start with '$'), and it sorts/reads as a clear out-of-band tag in the YAML mapping.
    const string TypeTag = "$type";

    // Serialize a polymorphic instance as a YAML mapping: a $type tag (the concrete type's namespace-
    // qualified FullName -- NOT the AssemblyQualifiedName, so it survives an assembly rename and stays
    // diff-clean) plus each serializable member recursed through SerializeMemberValue (so a nested member
    // can itself be polymorphic, a collection, a ref, or a leaf). TREE-ONLY with a cycle-guard (Trap 3 /
    // Unity parity): a back-reference to an already-serialized instance stops at the guard and serializes as
    // null (a real object graph would hurt YAML diff-cleanliness and the drawer -- rejected by the plan).
    static Dictionary<object, object> SerializeReferenceInstance(object instance, HashSet<object> visited) {
        visited ??= new HashSet<object>(ReferenceEqualityComparer.Instance);
        if (!visited.Add(instance))
            return null; // cycle / shared ref already emitted -> null (Unity duplicates/nulls back-refs)

        try {
            Type concrete = instance.GetType();
            var map = new Dictionary<object, object> {
                // FullName resolves back via TypeCache.GetTypesDerivedFrom(declaredType) on load -- scoped to
                // the declared base's implementors, deterministic, and assembly-agnostic.
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
            // Pop so a sibling that legitimately reuses the same instance type (different instance) is not
            // mistaken for a cycle -- only an ANCESTOR back-reference is a cycle.
            visited.Remove(instance);
        }
    }

    // ---- Nested struct/class members (G4 engine-half) -----------------------
    // Serialize a plain nested instance (a concrete class or non-primitive struct, NOT [SerializeReference])
    // as a YAML mapping of its serializable members -- WITHOUT a $type tag, because the declared member type
    // is the concrete type (no subclass is stored, so load re-instantiates the declared type directly). The
    // sibling of SerializeReferenceInstance: same member recursion, same cycle guard, but $type-free. A class
    // member can form a reference cycle (A.Child -> B.Parent -> A), so the SAME visited-set guard applies and
    // an ancestor back-reference serializes as null (tree-only + cycle-guard, Trap 3 / Unity parity). A struct
    // is a value type -> never reference-cyclic, and HashSet adds the boxed copy harmlessly. An empty member
    // map is still returned (an all-default nested value round-trips as an empty mapping, not null).
    static Dictionary<object, object> SerializeNestedInstance(object instance, HashSet<object> visited) {
        // Structs are value types: each access boxes a fresh copy, so a HashSet identity-add would never
        // collide and the guard is moot -- only reference types can form a real cycle. Guard classes only.
        bool guard = !instance.GetType().IsValueType;
        if (guard) {
            visited ??= new HashSet<object>(ReferenceEqualityComparer.Instance);
            if (!visited.Add(instance))
                return null; // ancestor back-reference -> null (Unity duplicates/nulls back-refs)
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

    // A list/array member -> a YAML sequence (List<object>); each element recurses through SerializeValue.
    // A null element survives as a null entry (index preserved) so a `List<Material>` with a gap round-trips
    // its shape; the deserialize side reads the null back. Built as List<object> so YamlDotNet emits a
    // block/flow sequence and each boxed element uses its own runtime-type converter (math structs) or scalar.
    static List<object> SerializeSequence(System.Collections.IEnumerable seq) {
        var items = new List<object>();
        foreach (object element in seq)
            items.Add(SerializeValue(element));
        return items;
    }

    // A Dictionary<K,V> member -> a YAML mapping (Dictionary<object,object>); both key and value recurse
    // through SerializeValue. Keys are typically primitives/enums (scalar); a complex key still serializes
    // via the same recursion. A null serialized key is skipped (a YAML mapping has no null key).
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

    // ---- Deserialize -------------------------------------------------------

    // Builds entities in the current scene WITHOUT running play lifecycle — even mid-play (live
    // script reload): OnBegin must not observe default member values, so Attach's play-mode
    // lifecycle is suppressed for the duration and the caller fires Scene.FireBegin afterwards.
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
            entity.Tag = string.IsNullOrEmpty(entityDoc.Tag) ? TagManager.Untagged : entityDoc.Tag;
            entity.Layer = entityDoc.Layer;
            entity.PrefabSource = Guid.TryParseExact(entityDoc.PrefabSource, "N", out Guid pfg) ? pfg : Guid.Empty;
            entity.transform.Position = entityDoc.Transform.Position;
            entity.transform.Rotation = entityDoc.Transform.Rotation;
            entity.transform.Scale = entityDoc.Transform.Scale;

            // Restore the saved instance id so identity round-trips (editor selection survives undo).
            if (entityDoc.Id is not null && Guid.TryParseExact(entityDoc.Id, "N", out Guid restoredId))
                entity.InstanceId = restoredId;

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

    // ---- Targeted (single-entity) capture/restore — for the editor's scoped undo ----------------
    // The editor's undo can snapshot just ONE entity instead of the whole scene, so undoing a value
    // edit doesn't tear down + rebuild every scene-wide component (which re-fired IrradianceVolume
    // bakes, OnAttach side effects, and dropped the selection). These keep the SAME entity instance
    // (its InstanceId and the editor's selection survive) and never touch other entities or scene
    // components.

    // Captures one entity (its transform + components) to a document, WITHOUT its descendants and
    // keeping its real parent ref — so RestoreEntityInPlace can put it back exactly.
    public static EntityDocument CaptureEntity(Entity entity) =>
        entity is null ? null : BuildEntityDocument(entity);

    // Restores a captured entity IN PLACE: same Entity object (identity + selection preserved), its
    // components torn down and rebuilt from the document, transform reapplied. Returns false if the
    // entity no longer exists (caller falls back to a full-scene restore). Parent is NOT re-wired here
    // (a reparent is a structural change that uses full-scene undo).
    public static bool RestoreEntityInPlace(Entity entity, EntityDocument doc) {
        if (entity is null || doc is null || entity.IsDestroyed)
            return false;

        SceneManager.SuppressPlayLifecycle = true;
        try {
            // Tear down current components (OnDetach unregisters renderers/lights/etc.).
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
        // Restore component identity (for BEvent listeners that target this component by id). Done
        // before ApplyMembers so a listener resolving immediately would see the final id.
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

            // BEvents are populated IN PLACE: the component owns the instance (a public field
            // initialized inline, `= new()`), so we fill its listener list rather than reassign it.
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

    // Inverse of SerializeValue. Asset refs (string -> BObject) load via AssetDatabase. The optional member
    // carries [SerializeReference] context for the G3 polymorphic branch (a $type-tagged map). It is null for
    // recursed collection elements -- their polymorphism is detected by the $type tag + abstract element type.
    static object DeserializeValue(object raw, Type targetType, MemberInfo member = null) {
        if (raw is null)
            return null;

        // Polymorphic render-feature list (chunk 21 / design §5 D3) — inverse of SerializeFeatureList.
        // Parses the ordered {type, active, members} list back into a List<RenderFeature>, resolving
        // each type via ResolveFeature; an UNKNOWN type-name WARNS + SKIPS (Volume-loader parity: a
        // scene authored with a since-deleted feature must still load), preserving the order of the rest.
        // Checked before the [SerializeReference] / collection branches: a List<RenderFeature> is its own
        // ordered {type, active, members} shape, not a $type-tagged single instance nor a generic list.
        if (IsRenderFeatureList(targetType))
            return DeserializeFeatureList(raw);

        // [SerializeReference] polymorphism (G3): a $type-tagged mapping is an out-of-band signal ONLY the
        // polymorphic serializer emits (a Vector*/Dictionary map never carries $type), so its presence on a
        // polymorphic-eligible target means "instantiate the named concrete type + fill its members." Checked
        // FIRST so a polymorphic member never falls through to the collection/math-map branches below. A
        // non-polymorphic target (the common case) skips this entirely -> byte-identical.
        if (TryDeserializeReferenceInstance(raw, targetType, member, out object polymorphic))
            return polymorphic;

        // Scene-object references parse the stored InstanceId hex back into the value-type ref.
        // Resolution to the live object is LAZY (EntityRef.Value), so this never depends on entity
        // creation order: a forward ref to an entity not yet built deserializes fine and binds on
        // first access. A non-string / unparsable value yields None (Guid.Empty), like a missing ref.
        if (targetType == typeof(EntityRef))
            return new EntityRef(ParseInstanceId(raw));
        if (targetType == typeof(ComponentRef))
            return new ComponentRef(ParseInstanceId(raw));

        if (typeof(BObject).IsAssignableFrom(targetType))
            return raw is string reference ? LoadAsset(reference, targetType) : null;

        // AnimationCurve round-trips through its compact string form.
        if (targetType == typeof(AnimationCurve))
            return raw is string curveStr ? AnimationCurve.Parse(curveStr) : null;

        if (targetType == typeof(ColorGradient))
            return raw is string gradientStr ? ColorGradient.Parse(gradientStr) : null;

        // Collections (G2): rebuild a List<T> / T[] from a YAML sequence and a Dictionary<K,V> from a YAML
        // mapping, recursing EACH element back through DeserializeValue at the element type -- so an element
        // that is itself a math struct (arrives as a {x,y,z} map), an asset/scene ref (a string), or a
        // nested type rehydrates correctly. This is checked BEFORE the math-struct map conversion below: a
        // Dictionary<K,V> member arrives as the SAME IDictionary<object,object> a Vector3 does, so the
        // targetType (collection vs Vector*) is what disambiguates -- the collection branch must win for a
        // dictionary-typed member. Guarded to actual collection target types, so a Vector*/Quaternion member
        // (not a collection) still falls through to the map-to-vector conversion unchanged.
        if (targetType.IsArray)
            return DeserializeArray(raw, targetType.GetElementType());
        if (TryGetListElementType(targetType, out Type listElem))
            return DeserializeList(raw, targetType, listElem);
        if (TryGetDictionaryTypes(targetType, out Type keyType, out Type valType))
            return DeserializeDictionary(raw, targetType, keyType, valType);

        // Nested struct/class (G4): a plain (non-[SerializeReference]) member whose declared type is a
        // concrete class or non-primitive struct arrives as a member mapping (written by SerializeNestedInstance).
        // Reconstruct by instantiating the DECLARED type (no $type lookup -- the declared type is the concrete
        // type) and refilling its members through ApplyMembers (the SAME recursion a component uses, so a
        // nested-in-nested member rehydrates too). Checked AFTER the collection/asset/ref branches (a Nested
        // member is none of those) and BEFORE the math-struct map conversion below (a Vector*/Quaternion is
        // classified MathStruct, never Nested, so they don't overlap -- but order makes the precedence explicit).
        // A STRUCT comes back as a BOXED instance; ApplyMembers mutates the box, the caller's SetMemberValue
        // unboxes it into the field, so struct write-back round-trips through the codec.
        if (TryDeserializeNestedInstance(raw, targetType, member, out object nested))
            return nested;

        // OpenTK types arrive already converted; otherwise coerce the scalar to the member type.
        if (targetType.IsInstanceOfType(raw))
            return raw;

        // Math types nested inside a component's `Members` dict (e.g. PointLight.Color) arrive as a
        // raw {x,y,z,...} mapping, NOT a typed Vector — the YamlDotNet converters only fire for a
        // strongly-typed target, and Members is Dictionary<string, object>. Convert the mapping here.
        // (Without this, every Vector* / Quaternion COMPONENT member silently kept its default.)
        if (raw is IDictionary<object, object> map) {
            if (targetType == typeof(Vector2)) return new Vector2(MapF(map, "x"), MapF(map, "y"));
            if (targetType == typeof(Vector3)) return new Vector3(MapF(map, "x"), MapF(map, "y"), MapF(map, "z"));
            if (targetType == typeof(Vector4)) return new Vector4(MapF(map, "x"), MapF(map, "y"), MapF(map, "z"), MapF(map, "w"));
            if (targetType == typeof(Quaternion)) return new Quaternion(MapF(map, "x"), MapF(map, "y"), MapF(map, "z"), MapF(map, "w"));
        }

        return Coerce(raw, targetType);
    }

    // ---- [SerializeReference] deserialize (G3 engine-half) ------------------
    // Reconstruct a polymorphic instance from a $type-tagged mapping: resolve the concrete type, instantiate
    // it, and fill its members through ApplyMembers (the SAME recursion as a top-level component, so a nested
    // member can itself be polymorphic/a collection/a ref). Returns false (leaving the existing pipeline to
    // run) UNLESS the target is polymorphic-eligible AND the raw map actually carries a $type tag -- so a
    // non-polymorphic member is byte-identical. A $type that fails to resolve (renamed/removed type) logs and
    // yields null rather than throwing, matching the "missing ref -> null" leniency of the rest of the codec.
    static bool TryDeserializeReferenceInstance(object raw, Type targetType, MemberInfo member, out object result) {
        result = null;

        // Only a [SerializeReference] member opts in (the exact trigger the serializer used). A polymorphic
        // COLLECTION element (a `[SerializeReference] List<IFoo>`) is out of scope for this chunk -- the
        // collection serializer does not write $type per element yet -- so a recursed element (member == null)
        // is never treated as polymorphic here, keeping serialize/deserialize symmetric (no new silent loss).
        if (PropertyCategories.Classify(targetType, member) != PropertyCategory.Polymorphic)
            return false;

        if (raw is not IDictionary<object, object> map ||
            !map.TryGetValue(TypeTag, out object tagObj) || tagObj is not string typeName)
            return false; // eligible target but no $type tag -> not a polymorphic payload (let the caller decide)

        Type concrete = ResolvePolymorphicType(typeName, targetType);
        if (concrete is null) {
            Debugging.LogWarning(
                $"[SerializeReference] could not resolve concrete type '{typeName}' for '{targetType.Name}'; " +
                "the value is dropped (the type was renamed or removed).");
            result = null;
            return true; // handled (consumed the $type payload) -- don't fall through to the math/collection branches
        }

        object instance = Activator.CreateInstance(concrete);
        // Reuse the component member-application path: build the string-keyed member dict (skip the $type tag)
        // and apply through the same recursion, so nested polymorphic/collection/ref members rehydrate.
        var members = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach ((object k, object v) in map)
            if (k is string key && key != TypeTag)
                members[key] = v;

        ApplyMembers(instance, concrete, members);
        result = instance;
        return true;
    }

    // Resolve a $type FullName to a concrete instantiable type ASSIGNABLE to the declared base. Scoped to the
    // declared base's implementors via TypeCache so resolution is deterministic, assembly-agnostic, and can
    // never bind an unrelated type that merely shares a FullName. TypeCache is built at bootstrap over the
    // engine + game-script assemblies (the headless serializer has it too -- it is an ENGINE cache).
    static Type ResolvePolymorphicType(string fullName, Type declaredType) {
        foreach (Type t in TypeCache.GetTypesDerivedFrom(declaredType))
            if (string.Equals(t.FullName, fullName, StringComparison.Ordinal))
                return t;
        return null;
    }

    // ---- Nested struct/class deserialize (G4 engine-half) -------------------
    // Reconstruct a plain nested instance from a member mapping: instantiate the DECLARED type (no $type --
    // unlike the polymorphic path, the declared type IS the concrete type) and fill its members through the
    // same ApplyMembers recursion a top-level component uses, so a nested-in-nested member rehydrates too.
    // Returns false (leaving the existing pipeline to run) UNLESS the target classifies Nested AND the raw is
    // a member mapping -- so a non-nested member is byte-identical. A type with no public parameterless ctor
    // (Activator throws) logs and yields null rather than throwing, matching the codec's "missing -> null"
    // leniency. A STRUCT instantiates to a boxed default; ApplyMembers mutates the box; the caller's
    // SetMemberValue unboxes it back into the field (struct write-back).
    static bool TryDeserializeNestedInstance(object raw, Type targetType, MemberInfo member, out object result) {
        result = null;

        if (PropertyCategories.Classify(targetType, member) != PropertyCategory.Nested)
            return false;

        // A nested value is written as a YAML mapping of its members. Anything else (a scalar from a legacy
        // scene, or a corrupt value) is not a nested payload -> leave it for the caller's coerce/fallthrough.
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
            return true; // handled -- don't fall through to the math/coerce branches
        }

        // Build a string-keyed member dict (YamlDotNet keys arrive as object strings) and apply through the
        // shared recursion, so nested polymorphic/collection/ref/struct members rehydrate the same way.
        var members = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach ((object k, object v) in rawMap)
            if (k is string key)
                members[key] = v;

        ApplyMembers(instance, targetType, members);
        result = instance;
        return true;
    }

    // Read a float component from a YAML mapping (values arrive as strings from YamlDotNet).
    static float MapF(IDictionary<object, object> map, string key) =>
        map.TryGetValue(key, out object v) && v is not null &&
        float.TryParse(v.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : 0f;

    // ---- Collection deserialize (G2) ---------------------------------------
    // A YAML sequence arrives from YamlDotNet as a List<object> (or any IEnumerable when typed loosely);
    // a YAML mapping as an IDictionary<object,object>. Each helper recurses every element back through
    // DeserializeValue at the element/key/value type, so a list of math structs / refs / nested types
    // rehydrates the same way a top-level member would. A raw that is not a sequence (corrupt/legacy)
    // yields an empty collection rather than throwing, matching the "missing ref -> None" leniency.

    // T[] from a sequence raw.
    static object DeserializeArray(object raw, Type elementType) {
        List<object> items = RawItems(raw);
        Array array = Array.CreateInstance(elementType, items.Count);
        for (int i = 0; i < items.Count; i++)
            array.SetValue(DeserializeValue(items[i], elementType), i);
        return array;
    }

    // List<T> (or anything assignable from List<T> via IList) from a sequence raw.
    static object DeserializeList(object raw, Type targetType, Type elementType) {
        List<object> items = RawItems(raw);
        var list = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(elementType))!;
        foreach (object item in items)
            list.Add(DeserializeValue(item, elementType));
        // The member is exactly List<T> in the common case; if it is a wider IList target the List<T> is
        // assignable. SetMemberValue would throw on a true mismatch -- ApplyMembers swallows nothing, but a
        // List<T> assigned to a List<T> member is the only shape this branch is reached for (TryGetListElementType).
        return list;
    }

    // Dictionary<K,V> from a mapping raw. Keys/values both recurse at their declared types.
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

    // Normalize a sequence raw to a List<object>. YamlDotNet yields List<object> for a block/flow sequence;
    // be tolerant of any non-string IEnumerable. Non-sequence (a scalar/map) -> empty (no throw).
    static List<object> RawItems(object raw) {
        if (raw is List<object> direct)
            return direct;
        var items = new List<object>();
        if (raw is not string && raw is System.Collections.IEnumerable e && raw is not IDictionary<object, object>)
            foreach (object item in e)
                items.Add(item);
        return items;
    }

    // True for a List<T> member (the exact closed-generic List<>). Out the element type. Excludes arrays
    // (handled separately) and other IEnumerables we do not reconstruct (e.g. read-only sequences).
    static bool TryGetListElementType(Type t, out Type elementType) {
        elementType = null;
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>)) {
            elementType = t.GetGenericArguments()[0];
            return true;
        }
        return false;
    }

    // True for a Dictionary<K,V> member (the exact closed-generic Dictionary<,>). Out key + value types.
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

    // True when the value is an unset (None) scene-object ref. SerializeValue returns null for these,
    // but a None ref is a legitimate "no target" value, NOT a dropped member, so the G0 loud-drop must
    // skip it (only a SET ref that fails to serialize would be a real loss, and EntityRef/ComponentRef
    // always serialize when set). Boxed value types, so a type check is enough.
    static bool IsNoneSceneObjectRef(object value) =>
        value is EntityRef { HasValue: false } or ComponentRef { HasValue: false };

    // Parse a stored InstanceId hex ("N" form, 32 chars) back to a Guid; Guid.Empty (= None) for a
    // non-string or unparsable value, so a corrupt/missing ref deserializes to "no target" not a throw.
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

    // ---- Render-feature list serialization (phase-3 chunk 21 / design §5 D3) -----------------------
    // The authored render-feature list (RenderFeatures.Features) is a polymorphic ORDERED list of
    // RenderFeature subtypes. ComponentReflection's generic path can't round-trip a List<abstractType>
    // (no type discriminator), so it's handled inline by SerializeValue/DeserializeValue exactly like
    // the BObject/BEvent/AnimationCurve special cases. Mirrors VolumeProfileLoader's type-name + member
    // round-trip, but through the scene YAML (features are scene-local per design §5 D2, not a shared
    // asset). Generic to the ELEMENT type (any List<RenderFeature> member), not the one member name.

    // A `List<RenderFeature>` (or any list whose element type derives from RenderFeature).
    static bool IsRenderFeatureList(Type type) =>
        type.IsGenericType &&
        type.GetGenericTypeDefinition() == typeof(List<>) &&
        typeof(RenderFeature).IsAssignableFrom(type.GetGenericArguments()[0]);

    // -> an ordered List<Dictionary<string,object>> of {type, active, members}. YamlDotNet serializes
    // this nested map/list structure natively (the same shape ComponentDocument.Members already uses),
    // so it lands as a clean YAML list under the member's key. Order preserved.
    static object SerializeFeatureList(System.Collections.IEnumerable features) {
        var list = new List<object>();
        foreach (object item in features) {
            if (item is not RenderFeature feature)
                continue; // a null entry in the list (shouldn't happen) is dropped, not crash-on-write
            var entry = new Dictionary<string, object> {
                // type-name via FeatureNameOf (short name when unambiguous, else FullName) — the same
                // discriminator ComponentDocument.Type uses for a Behaviour.
                ["type"] = ComponentRegistry.FeatureNameOf(feature),
                // Active is the per-feature master switch (mirrors ComponentDocument.Enabled): stored as
                // a top-level key, NOT inside `members`, so it's not duplicated (it's excluded below).
                ["active"] = feature.Active,
            };
            var members = new Dictionary<string, object>();
            foreach (MemberInfo member in SerializableMembers(feature.GetType())) {
                // `Active` is already captured top-level; skip it in the reflected member set so the two
                // never diverge (and the YAML has one `active`, not an `active` AND a member `active`).
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

    // Inverse: the YAML list (a List<object> of maps from YamlDotNet) -> List<RenderFeature>. Resolve
    // each `type` via ResolveFeature; UNKNOWN -> warn + skip (Volume-loader parity), order of the rest
    // preserved. `active` + nested `members` applied through the SAME reflection path Behaviour members
    // use (ApplyMembers), so a feature's params deserialize identically to a component's.
    static object DeserializeFeatureList(object raw) {
        var result = new List<RenderFeature>();
        if (raw is not System.Collections.IEnumerable entries)
            return result;

        foreach (object entryObj in entries) {
            // YamlDotNet yields each mapping as IDictionary<object,object> (keys are strings).
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
                // Normalize to the Dictionary<string,object> shape ApplyMembers expects (same as a
                // ComponentDocument.Members), then reuse the exact Behaviour-member apply path.
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
