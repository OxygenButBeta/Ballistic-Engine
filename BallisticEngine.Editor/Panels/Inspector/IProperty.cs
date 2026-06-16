using System;

namespace BallisticEngine.Editor.Inspector;

// A single drawable value, abstracting over (a) a reflected component member and (b) a VolumeComponent
// parameter slot. The pipeline and drawers see ONLY this, so one drawer set serves both the component
// inspector and the volume profile editor. ValueType is the LOGICAL type (a VolumeParameter is unwrapped
// to its .Value type), so a ClampedFloatParameter and a `public float x` route to the same FloatDrawer.
public interface IProperty {
    string Name { get; }
    string Label { get; }      // [LabelText] override, else prettified Name
    string Tooltip { get; }
    Type ValueType { get; }
    object Owner { get; }      // the component / volume-component instance (for sibling conditions)

    object Get();
    void Set(object value);    // adapter-specific: component applies + marks dirty, volume sets .Value

    // The resolved+cached attributes (the editor's existing MemberAttributes, extended with conditionals).
    MemberAttributes Attributes { get; }

    // [Range] OR a Clamped{Float,Int}Parameter's bounds — unified so one numeric drawer covers both.
    (float min, float max)? Range { get; }
    bool IsColor { get; }
    bool Hdr { get; }

    // Volume override gate (component returns false / no-op setter).
    bool HasOverrideToggle { get; }
    bool Overridden { get; set; }

    // Sibling lookup for conditional attributes. Returns the LOGICAL value (VolumeParameter unwrapped).
    bool TryGetSiblingValue(string memberName, out object value);
}
