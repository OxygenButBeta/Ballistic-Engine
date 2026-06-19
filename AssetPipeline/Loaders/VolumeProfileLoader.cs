using System.Text.Json;

namespace BallisticEngine.AssetPipeline.Loaders;

// .volume <-> VolumeProfile. Load builds the runtime profile from the JSON definition
// (unknown components warn and are skipped; unknown/missing parameters keep defaults).
// Save is the editor's write-back path: because AssetDatabase caches the loaded instance,
// the inspector edits the live profile and persists it here in one step.
public static class VolumeProfileLoader {
    // Renamed volume components: profiles saved under the old name keep loading (and write
    // back the new name on the next editor save).
    static readonly Dictionary<string, string> LegacyTypeNames = new() {
        ["VolumetricLight"] = "VolumetricFog",
        // The old screen-space-GI / reflection volume components were REMOVED with the legacy GI stack
        // (Lumen V2). Their on-disk type names are no longer remapped to anything — a profile that still
        // names them warn-and-skips in Load (ResolveVolume → null), exactly like any unknown component.
    };

    // Per-old-type parameter renames (none currently — the GI-consolidation renames were dropped with the
    // legacy GI volumes). Kept as the extension point for future component renames.
    static readonly Dictionary<string, Dictionary<string, string>> LegacyParameterNames = new();

    public static VolumeProfile Load(BallisticProject project, string assetPath) {
        var definition = ContentText.ReadJson<VolumeProfileDefinition>(project, assetPath);
        if (definition is null) {
            Debugging.LogError($"'{assetPath}': volume profile not found.");
            return null;
        }
        var profile = new VolumeProfile { Name = Path.GetFileNameWithoutExtension(assetPath) };

        // MIGRATION (legacy GI stack → Lumen V2): reflections used to live INSIDE the GlobalIllumination
        // component as `reflectionsMode`/`reflectionsIntensity`; they were split into the standalone
        // `Reflections` component. A profile saved under the old shape has NO Reflections block, so its
        // reflections override silently did nothing (the bridge gates `SsrEnabled` on the missing component).
        // Carry the old fields out into a synthesized Reflections component below.
        ReflectionMode? legacyReflMode = null;
        float? legacyReflIntensity = null;

        foreach (VolumeComponentDefinition componentDef in definition.Components ?? []) {
            if (componentDef.Type == "GlobalIllumination" && componentDef.Parameters is { } giParams) {
                if (giParams.TryGetValue("reflectionsMode", out VolumeParameterDefinition rm)
                    && rm.Value.ValueKind is JsonValueKind.String
                    && Enum.TryParse(rm.Value.GetString(), out ReflectionMode parsedMode))
                    legacyReflMode = parsedMode;
                if (giParams.TryGetValue("reflectionsIntensity", out VolumeParameterDefinition ri)
                    && ri.Value.ValueKind is JsonValueKind.Number)
                    legacyReflIntensity = ri.Value.GetSingle();
            }
            var typeName = LegacyTypeNames.GetValueOrDefault(componentDef.Type, componentDef.Type);
            Type type = ComponentRegistry.ResolveVolume(typeName);
            if (type is null) {
                Debugging.LogWarning($"'{assetPath}': unknown volume component '{componentDef.Type}'; skipped.");
                continue;
            }

            bool alreadyPresent = profile.Has(type);
            VolumeComponent component = profile.Add(type);
            // A merge target (two old types → one new, e.g. SSGI + SSR → GlobalIllumination) stays active
            // if EITHER source was active; a fresh component takes its source's Active straight.
            component.Active = alreadyPresent ? component.Active || componentDef.Active : componentDef.Active;

            if (componentDef.Parameters is null)
                continue;

            // Old-name → new-field renames for this on-disk type (GI consolidation); empty for the rest.
            LegacyParameterNames.TryGetValue(componentDef.Type, out Dictionary<string, string> paramRenames);

            foreach (VolumeComponent.ParameterSlot slot in component.Parameters) {
                // Find the file key that feeds this slot: the renamed old name if one maps here, else the
                // slot's own name. (A merge skips slots a source doesn't carry — its keys stay default.)
                string fileKey = slot.Name;
                if (paramRenames is not null)
                    foreach (var (oldName, newName) in paramRenames)
                        if (newName == slot.Name) { fileKey = oldName; break; }

                if (!componentDef.Parameters.TryGetValue(fileKey, out VolumeParameterDefinition parameterDef))
                    continue;

                slot.Parameter.Overridden = parameterDef.Overridden;
                ApplyValue(slot.Parameter, parameterDef.Value, assetPath, slot.Name);
            }
        }

        // Finish the legacy-reflections migration: synthesize a Reflections component from the old
        // GlobalIllumination.reflections* fields when the profile didn't already carry its own. Only when the
        // old fields were actually present (so untouched/new profiles are byte-identical). The next editor Save
        // persists the new shape and drops the stale GI fields.
        if ((legacyReflMode is not null || legacyReflIntensity is not null) && !profile.Has(typeof(Reflections))) {
            var refl = (Reflections)profile.Add(typeof(Reflections));
            if (legacyReflMode is { } m) { refl.mode.Value = m; refl.mode.Overridden = true; }
            if (legacyReflIntensity is { } i) { refl.intensity.Value = i; refl.intensity.Overridden = true; }
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
