namespace BallisticEngine;

public sealed class PropertyTree {
    public Type TargetType { get; }
    public IReadOnlyList<object> Targets { get; }
    public TypePlan Plan { get; }
    public IReadOnlyList<PropertyNode> Roots { get; }

    PropertyTree(Type targetType, object[] targets, TypePlan plan, PropertyNode[] roots) {
        TargetType = targetType;
        Targets = targets;
        Plan = plan;
        Roots = roots;
    }

    public static PropertyTree For(IReadOnlyList<object> targets, int maxDepth = PropertyNode.DefaultMaxDepth) {
        if (targets is null || targets.Count == 0)
            throw new ArgumentException("PropertyTree needs at least one target.", nameof(targets));

        var owners = new object[targets.Count];
        for (int i = 0; i < targets.Count; i++)
            owners[i] = targets[i];

        Type type = owners[0]?.GetType()
            ?? throw new ArgumentException("PropertyTree's first target is null.", nameof(targets));

        TypePlan plan = TypePlan.For(type);
        var roots = new PropertyNode[plan.Members.Count];
        for (int i = 0; i < plan.Members.Count; i++)
            roots[i] = new PropertyNode(plan.Members[i], owners, parent: null, depth: 0, maxDepth);

        return new PropertyTree(type, owners, plan, roots);
    }

    public static PropertyTree For(object target, int maxDepth = PropertyNode.DefaultMaxDepth) =>
        For(new[] { target }, maxDepth);
}
