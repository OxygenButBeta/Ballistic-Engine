using System;
using System.Collections.Generic;
using System.Reflection;

namespace BallisticEngine.Editor.Inspector;

// IProperty over a VolumeComponent.ParameterSlot (the volume profile path). The logical ValueType is the
// parameter's `Value` property type (a VolumeParameter<T> unwrapped to T), so a ClampedFloatParameter
// routes to the SAME FloatDrawer as a `[Range] float` component member, and an EnumParameter<GiMode> to
// the same EnumDrawer as a `GiMode` field. Range comes from the Clamped* bounds; the override checkbox
// and the disabled-unless-overridden gate are the volume GUI adapter's job (HasOverrideToggle = true).
public sealed class VolumeParamProperty : IProperty {
    readonly VolumeComponent.ParameterSlot slot;
    readonly VolumeComponent owner;
    readonly PropertyInfo valueProp;

    public VolumeParamProperty(VolumeComponent.ParameterSlot slot, VolumeComponent owner) {
        this.slot = slot;
        this.owner = owner;
        Attributes = MemberAttributes.For(slot.Field);
        valueProp = ValuePropFor(slot.Parameter.GetType());
        ValueType = valueProp.PropertyType;
    }

    public string Name => slot.Name;
    public string Label => Attributes.LabelText?.Text ?? InspectorReflection.Prettify(slot.Name);
    public string Tooltip => Attributes.Tooltip?.Text;
    public Type ValueType { get; }
    public object Owner => owner;
    public MemberAttributes Attributes { get; }

    public object Get() => valueProp.GetValue(slot.Parameter);
    public void Set(object value) => valueProp.SetValue(slot.Parameter, value);

    public (float min, float max)? Range => slot.Parameter switch {
        ClampedFloatParameter cf => (cf.Min, cf.Max),
        ClampedIntParameter ci => (ci.Min, ci.Max),
        _ => null,
    };

    public bool IsColor => slot.Parameter is ColorParameter;
    public bool Hdr => slot.Parameter is ColorParameter c && c.Hdr;

    public bool HasOverrideToggle => true;
    public bool Overridden {
        get => slot.Parameter.Overridden;
        set => slot.Parameter.Overridden = value;
    }

    public bool TryGetSiblingValue(string memberName, out object value) =>
        InspectorReflection.TryGetSibling(owner, memberName, out value);

    static readonly Dictionary<Type, PropertyInfo> valuePropCache = new();
    static PropertyInfo ValuePropFor(Type parameterType) {
        if (valuePropCache.TryGetValue(parameterType, out PropertyInfo p)) return p;
        p = parameterType.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
        valuePropCache[parameterType] = p;
        return p;
    }
}
