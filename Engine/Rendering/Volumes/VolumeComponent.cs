using System.Reflection;

namespace BallisticEngine;

public abstract class VolumeComponent {
    public bool Active = true;

    public readonly record struct ParameterSlot(string Name, VolumeParameter Parameter, FieldInfo Field);

    readonly List<ParameterSlot> parameters = new();

    public IReadOnlyList<ParameterSlot> Parameters => parameters;

    protected VolumeComponent() {
        foreach (FieldInfo field in GetType()
                     .GetFields(BindingFlags.Public | BindingFlags.Instance)
                     .Where(f => typeof(VolumeParameter).IsAssignableFrom(f.FieldType))
                     .OrderBy(f => f.MetadataToken)) {
            if (field.GetValue(this) is VolumeParameter parameter)
                parameters.Add(new ParameterSlot(field.Name, parameter, field));
        }
    }

    internal void Override(VolumeComponent overrides, float interpFactor) {
        for (var i = 0; i < parameters.Count; i++) {
            VolumeParameter source = overrides.parameters[i].Parameter;
            if (source.Overridden) {
                parameters[i].Parameter.Interp(source, interpFactor);
                parameters[i].Parameter.Overridden = true;
            }
        }
    }

    internal void CopyValuesFrom(VolumeComponent source) {
        for (var i = 0; i < parameters.Count; i++) {
            parameters[i].Parameter.CopyValueFrom(source.parameters[i].Parameter);
            parameters[i].Parameter.Overridden = false;
        }
    }
}
