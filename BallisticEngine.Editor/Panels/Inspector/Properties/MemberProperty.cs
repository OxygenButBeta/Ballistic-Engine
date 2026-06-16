using System;
using System.Reflection;
using SysVec3 = System.Numerics.Vector3;

namespace BallisticEngine.Editor.Inspector;

// IProperty over a reflected component member (the component-inspector path). Mirrors the old DrawMember
// value access (ComponentReflection.Get/SetValue). The host passes an `apply` delegate that routes
// through InspectorPanel.ApplyMember (multi-select) + MarkViewportDirty; the headless test uses the
// default reflection set so the pipeline logic is exercised without the editor.
public sealed class MemberProperty : IProperty {
    readonly MemberInfo member;
    readonly object owner;
    readonly Action<object> apply;

    public MemberProperty(MemberInfo member, object owner, Action<object> apply = null) {
        this.member = member;
        this.owner = owner;
        this.apply = apply;
        Attributes = MemberAttributes.For(member);
        ValueType = member is PropertyInfo p ? p.PropertyType : ((FieldInfo)member).FieldType;
    }

    // Exposed so the component GUI adapter can reuse the existing InspectorPanel helpers
    // (DrawMixedMarker / DrawAssetSlot) that need the raw member + target.
    public MemberInfo Member => member;
    public object Owner => owner;

    public string Name => member.Name;
    public string Label => Attributes.LabelText?.Text ?? InspectorReflection.Prettify(member.Name);
    public string Tooltip => Attributes.Tooltip?.Text;
    public Type ValueType { get; }
    public MemberAttributes Attributes { get; }

    public object Get() => member is PropertyInfo p ? p.GetValue(owner) : ((FieldInfo)member).GetValue(owner);

    public void Set(object value) {
        if (apply is not null) { apply(value); return; }
        if (member is PropertyInfo p) p.SetValue(owner, value);
        else ((FieldInfo)member).SetValue(owner, value);
    }

    public (float min, float max)? Range =>
        Attributes.Range is { } r ? (r.Min, r.Max) : null;

    public bool IsColor =>
        ValueType == typeof(SysVec3) &&
        (Attributes.ColorUsage is not null || member.Name.EndsWith("Color", StringComparison.Ordinal));

    public bool Hdr => Attributes.ColorUsage?.Hdr == true;

    public bool HasOverrideToggle => false;
    public bool Overridden { get => false; set { } }

    public bool TryGetSiblingValue(string memberName, out object value) =>
        InspectorReflection.TryGetSibling(owner, memberName, out value);
}
