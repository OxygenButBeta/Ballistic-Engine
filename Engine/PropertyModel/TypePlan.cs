using System.Reflection;
using System.Runtime.CompilerServices;
using BallisticEngine.Serialization;

namespace BallisticEngine;

public sealed class TypePlan {
    public sealed class Member {
        public required MemberInfo Info { get; init; }
        public required string Name { get; init; }
        public required Type ValueType { get; init; }
        public required PropertyCategory Category { get; init; }
        public required int Order { get; init; }
        public required int Declaration { get; init; }

        public object Get(object target) => ComponentReflection.GetValue(Info, target);
        public void Set(object target, object value) => ComponentReflection.SetValue(Info, target, value);
    }

    public Type Type { get; }
    public IReadOnlyList<Member> Members { get; }

    TypePlan(Type type, IReadOnlyList<Member> members) {
        Type = type;
        Members = members;
    }

    static readonly Dictionary<Type, TypePlan> cache = new();

    public static TypePlan For(Type type) {
        if (cache.TryGetValue(type, out TypePlan cached))
            return cached;
        TypePlan plan = Build(type);
        cache[type] = plan;
        return plan;
    }

    public static void Clear() => cache.Clear();

    [ModuleInitializer]
    internal static void RegisterReloadInvalidation() => ReloadCaches.Register(Clear);

    static TypePlan Build(Type type) {
        var members = new List<Member>();
        int declaration = 0;
        foreach (MemberInfo info in ComponentReflection.InspectorMembers(type)) {
            Type valueType = ComponentReflection.MemberType(info);
            members.Add(new Member {
                Info = info,
                Name = info.Name,
                ValueType = valueType,
                Category = PropertyCategories.Classify(valueType, info),
                Order = PropertyOrdering.OrderOf(info),
                Declaration = declaration++,
            });
        }

        Member[] ordered = members
            .OrderBy(m => m.Order)
            .ThenBy(m => m.Declaration)
            .ToArray();

        return new TypePlan(type, ordered);
    }
}
