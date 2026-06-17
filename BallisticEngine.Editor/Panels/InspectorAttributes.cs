using System.Collections.Generic;
using System.Reflection;

namespace BallisticEngine.Editor;

// Resolves and caches the inspector-relevant attributes on a component member. The inspector redraws
// every frame, so reflecting GetCustomAttribute per member per frame would be wasteful — each MemberInfo's
// attributes are read once and cached for the lifetime of the editor. Extended (2026-06) with the Odin-
// style conditional/ordering attributes consumed by the shared drawer pipeline (BallisticEngine.Editor.Inspector).
// Public (was internal) so the public IProperty pipeline interfaces can expose it.
public sealed class MemberAttributes {
    public RangeAttribute Range { get; private init; }
    public HeaderAttribute Header { get; private init; }
    public TooltipAttribute Tooltip { get; private init; }
    public SpaceAttribute Space { get; private init; }
    public ColorUsageAttribute ColorUsage { get; private init; }
    public FoldoutGroupAttribute Foldout { get; private init; }
    public bool ReadOnly { get; private init; }

    // --- drawer-pipeline additions ---
    public LabelTextAttribute LabelText { get; private init; }
    public IReadOnlyList<ConditionalAttribute> Conditionals { get; private init; }

    static readonly Dictionary<MemberInfo, MemberAttributes> cache = new();
    static readonly ConditionalAttribute[] NoConditions = System.Array.Empty<ConditionalAttribute>();

    // The attribute-less default for an IProperty with no backing MemberInfo (a collection element slot,
    // editor-rework G2-editor). All attributes null / empty so the drawer stack's Visibility (no
    // conditionals -> always visible) and Enable (no ReadOnly -> always enabled) steps are no-ops and the
    // element draws as a bare value. Shared singleton so a per-element property allocates no attribute set.
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
