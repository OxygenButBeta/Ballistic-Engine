using System.Reflection;

namespace BallisticEngine;

// One effect's worth of overridable settings inside a VolumeProfile (Unity's VolumeComponent).
// Subclasses declare public readonly VolumeParameter fields; the base constructor discovers them
// by reflection in declaration order, so blending, serialization and the editor UI all work on
// any new component without further wiring. Concrete subclasses are picked up by
// ComponentRegistry.Build, which is what makes them appear in the editor's Add Override menu.
public abstract class VolumeComponent {
    // Per-profile master switch (the checkbox next to the override's name in the editor):
    // an inactive component contributes nothing to the stack.
    public bool Active = true;

    public readonly record struct ParameterSlot(string Name, VolumeParameter Parameter, FieldInfo Field);

    readonly List<ParameterSlot> parameters = new();

    public IReadOnlyList<ParameterSlot> Parameters => parameters;

    protected VolumeComponent() {
        // MetadataToken preserves declaration order (base-class fields first is fine: built-ins
        // declare everything at one level, and order only has to agree across instances).
        foreach (FieldInfo field in GetType()
                     .GetFields(BindingFlags.Public | BindingFlags.Instance)
                     .Where(f => typeof(VolumeParameter).IsAssignableFrom(f.FieldType))
                     .OrderBy(f => f.MetadataToken)) {
            if (field.GetValue(this) is VolumeParameter parameter)
                parameters.Add(new ParameterSlot(field.Name, parameter, field));
        }
    }

    // Pulls the stack's values toward this profile component's overridden parameters.
    // `this` is the stack's working instance; `overrides` comes from a volume's profile.
    internal void Override(VolumeComponent overrides, float interpFactor) {
        for (var i = 0; i < parameters.Count; i++) {
            VolumeParameter source = overrides.parameters[i].Parameter;
            if (source.Overridden) {
                parameters[i].Parameter.Interp(source, interpFactor);
                // Propagate the Overridden flag to the stack parameter so consumers can tell a value
                // came from a PROFILE vs the default. Without this the stack value was correct but
                // Overridden stayed false, so a bridge that gates on .Overridden (e.g. the GI bridge,
                // to stop a default Lumen component clobbering GlobalIllumination) saw nothing as set.
                parameters[i].Parameter.Overridden = true;
            }
        }
    }

    // Stack reset: copy the default instance's values back in before a new blend pass. Also clears the
    // Overridden flags (Override sets them true when a profile contributes; they must reset each frame
    // or a one-time override would stick forever).
    internal void CopyValuesFrom(VolumeComponent source) {
        for (var i = 0; i < parameters.Count; i++) {
            parameters[i].Parameter.CopyValueFrom(source.parameters[i].Parameter);
            parameters[i].Parameter.Overridden = false;
        }
    }
}
