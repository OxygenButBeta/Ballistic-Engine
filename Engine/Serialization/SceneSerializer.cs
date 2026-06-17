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
            object serialized = SerializeValue(value);
            if (serialized is not null)
                doc.Members[CamelCase(member.Name)] = serialized;
        }

        return doc;
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

        if (value is BObject asset) {
            return AssetDatabase.TryGetAssetGuid(asset, out Guid guid)
                ? AssetRef.FromGuid(guid)
                : null; // unsaved/asset-less object reference — skip
        }

        return value;
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

            object value = DeserializeValue(raw, memberType);
            if (value is not null)
                SetMemberValue(member, target, value);
        }
    }

    // Inverse of SerializeValue. Asset refs (string -> BObject) load via AssetDatabase.
    static object DeserializeValue(object raw, Type targetType) {
        if (raw is null)
            return null;

        // Polymorphic render-feature list (chunk 21 / design §5 D3) — inverse of SerializeFeatureList.
        // Parses the ordered {type, active, members} list back into a List<RenderFeature>, resolving
        // each type via ResolveFeature; an UNKNOWN type-name WARNS + SKIPS (Volume-loader parity: a
        // scene authored with a since-deleted feature must still load), preserving the order of the rest.
        if (IsRenderFeatureList(targetType))
            return DeserializeFeatureList(raw);

        if (typeof(BObject).IsAssignableFrom(targetType))
            return raw is string reference ? LoadAsset(reference, targetType) : null;

        // AnimationCurve round-trips through its compact string form.
        if (targetType == typeof(AnimationCurve))
            return raw is string curveStr ? AnimationCurve.Parse(curveStr) : null;

        if (targetType == typeof(ColorGradient))
            return raw is string gradientStr ? ColorGradient.Parse(gradientStr) : null;

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

    // Read a float component from a YAML mapping (values arrive as strings from YamlDotNet).
    static float MapF(IDictionary<object, object> map, string key) =>
        map.TryGetValue(key, out object v) && v is not null &&
        float.TryParse(v.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : 0f;

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
