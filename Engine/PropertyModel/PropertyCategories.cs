using System.Collections;
using System.Reflection;

namespace BallisticEngine;

public static class PropertyCategories {
    public static PropertyCategory Classify(Type declaredType) => Classify(declaredType, member: null);

    public static PropertyCategory Classify(Type declaredType, MemberInfo member) {
        if (declaredType is null)
            return PropertyCategory.Unsupported;

        Type underlying = Nullable.GetUnderlyingType(declaredType);
        if (underlying is not null)
            declaredType = underlying;

        if (declaredType.IsEnum)
            return PropertyCategory.Enum;

        if (IsPrimitiveLeaf(declaredType))
            return PropertyCategory.Primitive;

        if (IsMathStruct(declaredType))
            return PropertyCategory.MathStruct;

        if (typeof(BObject).IsAssignableFrom(declaredType))
            return IsSceneObjectType(declaredType) ? PropertyCategory.SceneObjectRef : PropertyCategory.AssetRef;

        if (declaredType == typeof(EntityRef) || declaredType == typeof(ComponentRef))
            return PropertyCategory.SceneObjectRef;

        if (IsCollection(declaredType))
            return PropertyCategory.Collection;

        if (declaredType.IsAbstract || declaredType.IsInterface) {
            bool serializeRef = member is not null &&
                                member.GetCustomAttribute<SerializeReferenceAttribute>() is not null;
            return serializeRef ? PropertyCategory.Polymorphic : PropertyCategory.Unsupported;
        }

        if (member is not null && member.GetCustomAttribute<SerializeReferenceAttribute>() is not null)
            return PropertyCategory.Polymorphic;

        if (declaredType.IsClass || (declaredType.IsValueType && !declaredType.IsPrimitive)) {
            if (typeof(Delegate).IsAssignableFrom(declaredType) ||
                declaredType.IsPointer || declaredType.ContainsGenericParameters)
                return PropertyCategory.Unsupported;
            return PropertyCategory.Nested;
        }

        return PropertyCategory.Unsupported;
    }

    static bool IsPrimitiveLeaf(Type t) =>
        t.IsPrimitive || t == typeof(string) ||
        t == typeof(decimal) ||
        t == typeof(DateTime) ||
        t == typeof(TimeSpan) ||
        t == typeof(Guid);

    static bool IsMathStruct(Type t) {
        if (!t.IsValueType) return false;
        string ns = t.Namespace;
        if (ns != "OpenTK.Mathematics" && ns != "System.Numerics") return false;
        return t.Name is "Vector2" or "Vector3" or "Vector4" or "Vector2i" or "Vector3i" or "Vector4i"
            or "Quaternion" or "Matrix2" or "Matrix3" or "Matrix4"
            or "Color4" or "Color3";
    }

    static bool IsSceneObjectType(Type t) {
        for (Type b = t; b is not null; b = b.BaseType)
            if (b.Name is "Entity" or "Component")
                return true;
        return false;
    }

    static bool IsCollection(Type t) {
        if (t == typeof(string)) return false;
        if (t.IsArray) return true;
        if (typeof(IEnumerable).IsAssignableFrom(t)) return true;
        return false;
    }
}
