using System.Reflection;

namespace BallisticEngine.Editor;

// Resolves and caches the inspector-relevant attributes on a component member. The inspector
// redraws every frame, so reflecting GetCustomAttribute per member per frame would be wasteful —
// each MemberInfo's attributes are read once and cached for the lifetime of the editor.
internal sealed class MemberAttributes {
    public RangeAttribute Range { get; private init; }
    public HeaderAttribute Header { get; private init; }
    public TooltipAttribute Tooltip { get; private init; }
    public SpaceAttribute Space { get; private init; }
    public ColorUsageAttribute ColorUsage { get; private init; }
    public FoldoutGroupAttribute Foldout { get; private init; }
    public bool ReadOnly { get; private init; }

    static readonly Dictionary<MemberInfo, MemberAttributes> cache = new();

    public static MemberAttributes For(MemberInfo member) {
        if (cache.TryGetValue(member, out MemberAttributes cached))
            return cached;

        var resolved = new MemberAttributes {
            Range = member.GetCustomAttribute<RangeAttribute>(),
            Header = member.GetCustomAttribute<HeaderAttribute>(),
            Tooltip = member.GetCustomAttribute<TooltipAttribute>(),
            Space = member.GetCustomAttribute<SpaceAttribute>(),
            ColorUsage = member.GetCustomAttribute<ColorUsageAttribute>(),
            Foldout = member.GetCustomAttribute<FoldoutGroupAttribute>(),
            ReadOnly = member.GetCustomAttribute<ReadOnlyAttribute>() is not null,
        };
        cache[member] = resolved;
        return resolved;
    }
}
