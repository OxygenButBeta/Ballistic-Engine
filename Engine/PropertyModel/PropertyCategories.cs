using System.Collections;
using System.Reflection;

namespace BallisticEngine;

// Classifies a (declaredType, member) pair into the single PropertyCategory that drives the recursive
// traversal (editor-rework P0.2). Computed ONCE per member inside the compiled TypePlan and cached — this
// is reflection-heavy and MUST NOT run per frame (§4 perf rule). Returns the DECLARED-type classification;
// the one runtime-dependent case (Polymorphic) is resolved per-instance against the live value's actual
// type by the PropertyNode, never here.
//
// The rules mirror what SceneSerializer.SerializeValue/DeserializeValue already do, so the model and the
// existing codec agree, plus the three gaps the codec drops today (collections, scene-object refs, nested
// structs) which the model now names explicitly.
public static class PropertyCategories {
    // Classify by declared type alone, for a member that carries no [SerializeReference] (the common case).
    public static PropertyCategory Classify(Type declaredType) => Classify(declaredType, member: null);

    // Classify a member: the [SerializeReference] marker promotes an abstract/interface declared type to
    // Polymorphic; without it such a type stays Unsupported (the model never recurses a base it can't `new`).
    public static PropertyCategory Classify(Type declaredType, MemberInfo member) {
        if (declaredType is null)
            return PropertyCategory.Unsupported;

        // Nullable<T> classifies as its underlying type (a `float?` is still a numeric leaf).
        Type underlying = Nullable.GetUnderlyingType(declaredType);
        if (underlying is not null)
            declaredType = underlying;

        if (declaredType.IsEnum)
            return PropertyCategory.Enum;

        if (IsPrimitiveLeaf(declaredType))
            return PropertyCategory.Primitive;

        if (IsMathStruct(declaredType))
            return PropertyCategory.MathStruct;

        // BObject split: an asset (has an AssetDatabase GUID at runtime) vs a scene object (Entity/
        // Component/Behaviour, InstanceId-only). The split is by TYPE here (Entity/Component are scene
        // objects by definition; every other BObject subtype is an asset-class type). The runtime
        // guid-vs-no-guid check the serializer does is an INSTANCE concern, not a classification one.
        if (typeof(BObject).IsAssignableFrom(declaredType))
            return IsSceneObjectType(declaredType) ? PropertyCategory.SceneObjectRef : PropertyCategory.AssetRef;

        // EntityRef/ComponentRef are the SERIALIZABLE value-type form of a scene-object reference
        // (InstanceId-backed, resolved lazily). They are NOT BObjects, so they fall through the split
        // above; classify them as SceneObjectRef too so the editor gives them the scene-object picker
        // and the serializer/drawer agree on a leaf (the traversal does not recurse into the struct).
        if (declaredType == typeof(EntityRef) || declaredType == typeof(ComponentRef))
            return PropertyCategory.SceneObjectRef;

        if (IsCollection(declaredType))
            return PropertyCategory.Collection;

        // Abstract/interface declared type: only meaningful if marked [SerializeReference] (then a concrete
        // type is picked + instantiated). Unmarked → Unsupported (can't instantiate, won't silently recurse).
        if (declaredType.IsAbstract || declaredType.IsInterface) {
            bool serializeRef = member is not null &&
                                member.GetCustomAttribute<SerializeReferenceAttribute>() is not null;
            return serializeRef ? PropertyCategory.Polymorphic : PropertyCategory.Unsupported;
        }

        // A [SerializeReference] on a CONCRETE base still means "store the concrete type" (a subclass may be
        // assigned) — treat as Polymorphic so the $type tag is written.
        if (member is not null && member.GetCustomAttribute<SerializeReferenceAttribute>() is not null)
            return PropertyCategory.Polymorphic;

        // A concrete struct/class with no special codec: recurse into its serializable members (Rule 2).
        // Delegates / pointers / open generics have no meaningful member recursion → Unsupported.
        if (declaredType.IsClass || (declaredType.IsValueType && !declaredType.IsPrimitive)) {
            if (typeof(Delegate).IsAssignableFrom(declaredType) ||
                declaredType.IsPointer || declaredType.ContainsGenericParameters)
                return PropertyCategory.Unsupported;
            return PropertyCategory.Nested;
        }

        return PropertyCategory.Unsupported;
    }

    // The scalar leaf set the codec writes directly: the CLR primitives plus the common value types that
    // round-trip as a single scalar (string, decimal, DateTime, TimeSpan, Guid). char counts (a scalar).
    static bool IsPrimitiveLeaf(Type t) =>
        t.IsPrimitive ||                       // bool, byte/sbyte, short/ushort, int/uint, long/ulong, float, double, char, IntPtr*
        t == typeof(string) ||
        t == typeof(decimal) ||
        t == typeof(DateTime) ||
        t == typeof(TimeSpan) ||
        t == typeof(Guid);
    // (*IntPtr/UIntPtr are IsPrimitive but excluded as field types by the Unsupported delegate/pointer
    //  guard above only when they appear as IsPointer; a raw IntPtr field is rare and harmless as a leaf.)

    // OpenTK math values edited as ONE multi-component widget, never recursed. Matched by full name so the
    // model needs no compile reference beyond what the engine already has (OpenTK.Mathematics is allowed in
    // Engine). Color-tagged Vector3 is still a Vector3 here — the IsColor widget switch is an editor concern.
    static bool IsMathStruct(Type t) {
        if (!t.IsValueType) return false;
        string ns = t.Namespace;
        if (ns != "OpenTK.Mathematics" && ns != "System.Numerics") return false;
        return t.Name is "Vector2" or "Vector3" or "Vector4" or "Vector2i" or "Vector3i" or "Vector4i"
            or "Quaternion" or "Matrix2" or "Matrix3" or "Matrix4"
            or "Color4" or "Color3";
    }

    // Entity and Component (and thus Behaviour/Renderer/SceneBehaviour) are runtime scene objects — never
    // file assets. Every OTHER BObject subtype (Material/Texture*/Mesh/Shader/VolumeProfile/DataAsset/...)
    // is an asset-class type. Resolved by walking the base chain by name so the model needs no hard ref.
    static bool IsSceneObjectType(Type t) {
        for (Type b = t; b is not null; b = b.BaseType)
            if (b.Name is "Entity" or "Component")   // Component is the base of Behaviour/Renderer/etc.
                return true;
        return false;
    }

    // List<T> / arrays / dictionaries / any non-string IEnumerable<T>. string is IEnumerable<char> but is a
    // Primitive leaf (excluded above before this is reached). The generic check covers IList/ICollection too.
    static bool IsCollection(Type t) {
        if (t == typeof(string)) return false;
        if (t.IsArray) return true;
        if (typeof(IEnumerable).IsAssignableFrom(t)) return true;
        return false;
    }
}
