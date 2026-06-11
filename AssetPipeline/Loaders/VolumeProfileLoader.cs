using System.Text.Json;
using OpenTK.Mathematics;

namespace BallisticEngine.AssetPipeline.Loaders;

// .volume <-> VolumeProfile. Load builds the runtime profile from the JSON definition
// (unknown components warn and are skipped; unknown/missing parameters keep defaults).
// Save is the editor's write-back path: because AssetDatabase caches the loaded instance,
// the inspector edits the live profile and persists it here in one step.
public static class VolumeProfileLoader {
    public static VolumeProfile Load(BallisticProject project, string assetPath) {
        var definition = ContentText.ReadJson<VolumeProfileDefinition>(project, assetPath);
        if (definition is null) {
            Debugging.LogError($"'{assetPath}': volume profile not found.");
            return null;
        }
        var profile = new VolumeProfile { Name = Path.GetFileNameWithoutExtension(assetPath) };

        foreach (VolumeComponentDefinition componentDef in definition.Components ?? []) {
            Type type = ComponentRegistry.ResolveVolume(componentDef.Type);
            if (type is null) {
                Debugging.LogWarning($"'{assetPath}': unknown volume component '{componentDef.Type}'; skipped.");
                continue;
            }

            VolumeComponent component = profile.Add(type);
            component.Active = componentDef.Active;

            if (componentDef.Parameters is null)
                continue;

            foreach (VolumeComponent.ParameterSlot slot in component.Parameters) {
                if (!componentDef.Parameters.TryGetValue(slot.Name, out VolumeParameterDefinition parameterDef))
                    continue;

                slot.Parameter.Overridden = parameterDef.Overridden;
                ApplyValue(slot.Parameter, parameterDef.Value, assetPath, slot.Name);
            }
        }

        return profile;
    }

    public static void Save(VolumeProfile profile, string absolutePath) {
        var definition = new VolumeProfileDefinition();

        foreach (VolumeComponent component in profile.Components) {
            var componentDef = new VolumeComponentDefinition {
                Type = ComponentRegistry.VolumeNameOf(component),
                Active = component.Active,
            };

            foreach (VolumeComponent.ParameterSlot slot in component.Parameters) {
                componentDef.Parameters[slot.Name] = new VolumeParameterDefinition {
                    Overridden = slot.Parameter.Overridden,
                    Value = ToElement(slot.Parameter),
                };
            }

            definition.Components.Add(componentDef);
        }

        PipelineJson.Write(absolutePath, definition);
    }

    // Subtype order matters: Clamped* derive from their base parameters, Color from Vector3 —
    // matching the base class catches the whole family.
    static void ApplyValue(VolumeParameter parameter, JsonElement value, string assetPath, string name) {
        try {
            switch (parameter) {
                case IEnumParameter e when value.ValueKind is JsonValueKind.String:
                    var index = Array.IndexOf(e.Names, value.GetString());
                    if (index >= 0)
                        e.Index = index;
                    else
                        Debugging.LogWarning($"'{assetPath}': parameter '{name}' has unknown value '{value.GetString()}'; kept default.");
                    break;
                case IEnumParameter e when value.ValueKind is JsonValueKind.Number:
                    e.Index = value.GetInt32();
                    break;
                case BoolParameter b when value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                    b.Value = value.GetBoolean();
                    break;
                case IntParameter i when value.ValueKind is JsonValueKind.Number:
                    i.Value = (int)MathF.Round(value.GetSingle());
                    break;
                case FloatParameter f when value.ValueKind is JsonValueKind.Number:
                    f.Value = value.GetSingle();
                    break;
                case Vector3Parameter v when value.ValueKind is JsonValueKind.Array && value.GetArrayLength() >= 3:
                    v.Value = new Vector3(
                        value[0].GetSingle(), value[1].GetSingle(), value[2].GetSingle());
                    break;
                case null:
                    break;
                default:
                    if (value.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null)
                        Debugging.LogWarning($"'{assetPath}': parameter '{name}' has a mismatched value type; kept default.");
                    break;
            }
        }
        catch (Exception exception) {
            Debugging.LogWarning($"'{assetPath}': parameter '{name}' unreadable ({exception.Message}); kept default.");
        }
    }

    static JsonElement ToElement(VolumeParameter parameter) => parameter switch {
        IEnumParameter e => JsonSerializer.SerializeToElement(e.Names[e.Index]),
        BoolParameter b => JsonSerializer.SerializeToElement(b.Value),
        IntParameter i => JsonSerializer.SerializeToElement(i.Value),
        FloatParameter f => JsonSerializer.SerializeToElement(f.Value),
        Vector3Parameter v => JsonSerializer.SerializeToElement(
            new[] { v.Value.X, v.Value.Y, v.Value.Z }),
        _ => default,
    };
}
