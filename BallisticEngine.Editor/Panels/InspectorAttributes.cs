using System.Reflection;

namespace BallisticEngine.Editor;

public sealed class MemberAttributes {
    public RangeAttribute Range { get; private init; }
    public HeaderAttribute Header { get; private init; }
    public TooltipAttribute Tooltip { get; private init; }
    public SpaceAttribute Space { get; private init; }
    public ColorUsageAttribute ColorUsage { get; private init; }
    public FoldoutGroupAttribute Foldout { get; private init; }
    public bool ReadOnly { get; private init; }

    public LabelTextAttribute LabelText { get; private init; }
    public IReadOnlyList<ConditionalAttribute> Conditionals { get; private init; }

    static readonly Dictionary<MemberInfo, MemberAttributes> cache = new();
    static readonly ConditionalAttribute[] NoConditions = System.Array.Empty<ConditionalAttribute>();

    public static readonly MemberAttributes None = new() { Conditionals = NoConditions };

    public static MemberAttributes For(MemberInfo member) {
        if (cache.TryGetValue(member, out MemberAttributes cached))
            return cached;

        List<ConditionalAttribute> conditionals = null;
        foreach (ConditionalAttribute c in member.GetCustomAttributes<ConditionalAttribute>())
            (conditionals ??= new List<ConditionalAttribute>()).Add(c);

        var resolved = new MemberAttributes {
            Range = member.GetCustomAttribute<RangeAttribute>(),
            Header = member.GetCustomAttribute<HeaderAttribute>(),
            Tooltip = member.GetCustomAttribute<TooltipAttribute>(),
            Space = member.GetCustomAttribute<SpaceAttribute>(),
            ColorUsage = member.GetCustomAttribute<ColorUsageAttribute>(),
            Foldout = member.GetCustomAttribute<FoldoutGroupAttribute>(),
            ReadOnly = member.GetCustomAttribute<ReadOnlyAttribute>() is not null,
            LabelText = member.GetCustomAttribute<LabelTextAttribute>(),
            Conditionals = (IReadOnlyList<ConditionalAttribute>)conditionals ?? NoConditions,
        };
        cache[member] = resolved;
        return resolved;
    }
}
