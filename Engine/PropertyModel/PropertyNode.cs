namespace BallisticEngine;

public sealed class PropertyNode {
    public const int DefaultMaxDepth = 32;

    public TypePlan.Member Member { get; }

    readonly object[] owners;

    readonly PropertyNode parent;

    public string Name { get; }
    public Type ValueType { get; }
    public PropertyCategory Category { get; }
    public int Depth { get; }

    public bool IsGuard { get; }

    readonly int maxDepth;

    List<PropertyNode> children;
    object childrenStamp;

    internal PropertyNode(TypePlan.Member member, object[] owners, PropertyNode parent, int depth, int maxDepth) {
        Member = member;
        this.owners = owners;
        this.parent = parent;
        Name = member.Name;
        ValueType = member.ValueType;
        Category = member.Category;
        Depth = depth;
        this.maxDepth = maxDepth;
    }

    PropertyNode(string name, Type valueType, PropertyNode parent, int depth, int maxDepth) {
        Name = name;
        ValueType = valueType;
        Category = PropertyCategory.Unsupported;
        Depth = depth;
        IsGuard = true;
        this.parent = parent;
        owners = Array.Empty<object>();
        this.maxDepth = maxDepth;
    }

    public object[] GetValues() {
        var values = new object[owners.Length];
        for (int i = 0; i < owners.Length; i++)
            values[i] = owners[i] is null ? null : Member.Get(owners[i]);
        return values;
    }

    public object GetValue() =>
        !IsGuard && owners.Length > 0 && owners[0] is not null ? Member.Get(owners[0]) : null;

    public bool HasMultipleValues {
        get {
            if (IsGuard || owners.Length <= 1) return false;
            object first = GetValue();
            for (int i = 1; i < owners.Length; i++) {
                object v = owners[i] is null ? null : Member.Get(owners[i]);
                if (!Equals(v, first)) return true;
            }
            return false;
        }
    }

    public int TargetCount => owners.Length;

    public void SetValue(object value) {
        if (IsGuard) return;
        foreach (object owner in owners) {
            if (owner is null) continue;
            try { Member.Set(owner, value); }
            catch {
            }
        }

        children = null;
    }

    public IReadOnlyList<PropertyNode> GetChildren() {
        if (IsGuard) return Array.Empty<PropertyNode>();

        object active = GetValue();
        object stamp = active?.GetType();
        if (children is not null && Equals(stamp, childrenStamp))
            return children;

        children = BuildChildren(active);
        childrenStamp = stamp;
        return children;
    }

    List<PropertyNode> BuildChildren(object active) {
        var result = new List<PropertyNode>();
        if (Category is not (PropertyCategory.Nested or PropertyCategory.Polymorphic))
            return result;
        if (active is null)
            return result;

        if (Depth + 1 > maxDepth)
            return Guard("(max depth reached)", result);

        if (!active.GetType().IsValueType && IsOwnerOnPath(active))
            return Guard("→ already shown", result);

        TypePlan childPlan = TypePlan.For(active.GetType());
        object[] childOwners = ChildOwners();
        foreach (TypePlan.Member m in childPlan.Members)
            result.Add(new PropertyNode(m, childOwners, this, Depth + 1, maxDepth));

        return result;
    }

    object ActiveOwner => owners.Length > 0 ? owners[0] : null;

    bool IsOwnerOnPath(object value) {
        for (PropertyNode n = this; n is not null; n = n.parent)
            if (ReferenceEquals(n.ActiveOwner, value))
                return true;
        return false;
    }

    object[] ChildOwners() {
        var result = new object[owners.Length];
        for (int i = 0; i < owners.Length; i++)
            result[i] = owners[i] is null ? null : Member.Get(owners[i]);
        return result;
    }

    List<PropertyNode> Guard(string label, List<PropertyNode> into) {
        into.Add(new PropertyNode(label, ValueType, this, Depth + 1, maxDepth));
        return into;
    }
}
