using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BallisticEngine.Serialization;

namespace BallisticEngine;

// ARTIFACT 1 of the two-artifact cache boundary (editor-rework P0.2): the compiled, STATIC type plan —
// everything resolvable from a Type ALONE, with no live instance. Reflection runs ONCE here, at first ask,
// and the result is cached forever per Type (cleared only on hot-reload, P0.3). Per the §4 perf rule the
// per-frame draw path NEVER reflects: it walks this pre-baked plan. Odin's "compiled drawer plan" and
// Unity's cached SerializedProperty schema are the same idea.
//
// What is STATIC (here): the ordered member list, each member's declared ValueType, its MemberInfo, and its
// PropertyCategory by declared type. What is DYNAMIC (NOT here — lives in PropertyNode): live N-target
// values, mixed-value detection, the ACTUAL concrete type behind a Polymorphic member, a Collection's
// element count. Putting any of those here would be the cache-boundary bug §4 warns about.
public sealed class TypePlan {
    // One member of the plan: the reflection + classification baked once. The editor layers its drawer
    // stack on top of this (Phase B0); the model itself needs only what serialization + recursion need.
    public sealed class Member {
        public required MemberInfo Info { get; init; }
        public required string Name { get; init; }
        public required Type ValueType { get; init; }      // declared type (FieldType / PropertyType)
        public required PropertyCategory Category { get; init; }
        public required int Order { get; init; }            // [PropertyOrder]; 0 = declaration order
        public required int Declaration { get; init; }      // stable tie-break: original reflection index

        public object Get(object target) => ComponentReflection.GetValue(Info, target);
        public void Set(object target, object value) => ComponentReflection.SetValue(Info, target, value);
    }

    public Type Type { get; }
    public IReadOnlyList<Member> Members { get; }

    TypePlan(Type type, IReadOnlyList<Member> members) {
        Type = type;
        Members = members;
    }

    // STATIC cache keyed by Type. Joins the P0.3 ClearForReload contract (Chunk 3) so a hot-reload drops
    // stale plans built over the old game-script ALC — until then Clear() is exposed for the harness.
    static readonly Dictionary<Type, TypePlan> cache = new();

    public static TypePlan For(Type type) {
        if (cache.TryGetValue(type, out TypePlan cached))
            return cached;
        TypePlan plan = Build(type);
        cache[type] = plan;
        return plan;
    }

    // Drops every compiled plan. Called by the P0.3 hot-reload invalidation (Chunk 3) and the harness.
    public static void Clear() => cache.Clear();

    static TypePlan Build(Type type) {
        // The member set is the engine's single source of truth (ComponentReflection.InspectorMembers =
        // serializable members minus [HideInInspector]) so the model, the serializer, and the inspector
        // agree on WHICH members exist. The DECLARATION index is captured in enumeration order for the
        // stable tie-break (P0.4 determinism: equal [PropertyOrder] → original order, never reflection's
        // unspecified ordering re-sorted nondeterministically).
        var members = new List<Member>();
        int declaration = 0;
        foreach (MemberInfo info in ComponentReflection.InspectorMembers(type)) {
            Type valueType = ComponentReflection.MemberType(info);
            members.Add(new Member {
                Info = info,
                Name = info.Name,
                ValueType = valueType,
                Category = PropertyCategories.Classify(valueType, info),
                Order = info.GetCustomAttribute<PropertyOrderAttribute>()?.Order ?? 0,
                Declaration = declaration++,
            });
        }

        // Deterministic order: [PropertyOrder] ascending, then declaration order. Stable + total — the SAME
        // plan on every machine/build regardless of reflection's enumeration quirks (the P0.4 contract
        // applied to member ordering, mirroring the drawer-resolution tie-break).
        Member[] ordered = members
            .OrderBy(m => m.Order)
            .ThenBy(m => m.Declaration)
            .ToArray();

        return new TypePlan(type, ordered);
    }
}
