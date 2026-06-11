using System.Reflection;
using BallisticEngine.AssetPipeline.Loaders;
using Hexa.NET.ImGui;
using SysVec2 = System.Numerics.Vector2;
using SysVec4 = System.Numerics.Vector4;

namespace BallisticEngine.Editor;

// Unity-style volume profile editor: one collapsible header per override component (with its
// Active checkbox and a right-click Remove), an override checkbox + value widget per parameter,
// and an Add Override menu of the remaining VolumeComponent types. Drawn both inline under a
// Volume component and as the asset view of a selected .volume file. Draw() returns true when
// anything changed; SaveToAsset persists the live (AssetDatabase-cached) instance to its JSON.
internal static class VolumeProfileEditor {
    public static bool Draw(VolumeProfile profile) {
        var changed = false;
        VolumeComponent remove = null;

        foreach (VolumeComponent component in profile.Components) {
            ImGui.PushID(component.GetType().Name);

            bool active = component.Active;
            bool open = OverrideHeader(Prettify(component.GetType().Name), ref active, out bool removeRequested);
            if (active != component.Active) {
                component.Active = active;
                changed = true;
            }
            if (removeRequested)
                remove = component;

            if (open) {
                changed |= DrawAllNoneRow(component);
                changed |= DrawParameters(component);
            }

            ImGui.PopID();
        }

        if (remove is not null) {
            profile.Remove(remove);
            changed = true;
        }

        ImGui.Spacing();
        if (ImGui.Button($"{EditorIcons.Add}  Add Override", new SysVec2(-1, 0)))
            ImGui.OpenPopup("##addoverride");

        if (ImGui.BeginPopup("##addoverride")) {
            foreach (ComponentEntry entry in ComponentRegistry.VolumeMenu) {
                if (profile.Has(entry.Type))
                    continue;
                if (ImGui.MenuItem(entry.DisplayName)) {
                    profile.Add(entry.Type);
                    changed = true;
                }
            }
            ImGui.EndPopup();
        }

        return changed;
    }

    // Writes the profile back to its .volume source. Profile edits are asset edits (like the
    // material editor), so they save immediately and sit outside the scene-snapshot undo.
    public static void SaveToAsset(VolumeProfile profile) {
        if (!AssetDatabase.TryGetAssetGuid(profile, out Guid guid))
            return;

        var assetPath = AssetDatabase.GuidToAssetPath(guid);
        if (assetPath is null)
            return;

        VolumeProfileLoader.Save(profile, AssetDatabase.Project.ResolveAbsolute(assetPath));
    }

    // Unity's "ALL / NONE" row: flips every parameter's override checkbox in one click.
    static bool DrawAllNoneRow(VolumeComponent component) {
        var changed = false;

        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled("Override:");
        ImGui.SameLine();
        if (ImGui.SmallButton("All"))
            changed = SetAllOverrides(component, true);
        ImGui.SameLine();
        if (ImGui.SmallButton("None"))
            changed = SetAllOverrides(component, false);

        return changed;
    }

    static bool SetAllOverrides(VolumeComponent component, bool overridden) {
        var changed = false;
        foreach (VolumeComponent.ParameterSlot slot in component.Parameters) {
            if (slot.Parameter.Overridden != overridden) {
                slot.Parameter.Overridden = overridden;
                changed = true;
            }
        }
        return changed;
    }

    static bool DrawParameters(VolumeComponent component) {
        if (!ImGui.BeginTable("##params", 2, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.PadOuterX))
            return false;
        ImGui.TableSetupColumn("label", ImGuiTableColumnFlags.WidthStretch, 0.45f);
        ImGui.TableSetupColumn("value", ImGuiTableColumnFlags.WidthStretch, 0.55f);

        var changed = false;
        foreach (VolumeComponent.ParameterSlot slot in component.Parameters)
            changed |= DrawParameter(slot);

        ImGui.EndTable();
        ImGui.Spacing();

        if (changed)
            EnforceParametermnvariants(component);

        return changed;
    }

    // Cross-parameter constraints the per-slot sliders can't express on their own. Auto exposure
    // exposes its EV floor/ceiling as two independent dials, so they can be dragged past each other
    // into an inverted range - which later makes Math.Clamp in the metering pass throw. Keep min <=
    // max here by pushing whichever the user just moved, so the saved profile is always valid.
    static void EnforceParametermnvariants(VolumeComponent component) {
        if (component is not Exposure exposure)
            return;
        if (exposure.limitMin.Value > exposure.limitMax.Value)
            exposure.limitMax.Value = exposure.limitMin.Value;
    }

    static bool DrawParameter(VolumeComponent.ParameterSlot slot) {
        VolumeParameter parameter = slot.Parameter;
        var changed = false;

        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.PushID(slot.Name);

        bool overridden = parameter.Overridden;
        if (ImGui.Checkbox("##override", ref overridden)) {
            parameter.Overridden = overridden;
            changed = true;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(overridden ? "Overriding. Click to use the default." : "Click to override this parameter.");

        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(Prettify(slot.Name));
        if (slot.Field.GetCustomAttribute<TooltipAttribute>() is { } tooltip && ImGui.IsItemHovered())
            ImGui.SetTooltip(tooltip.Text);

        ImGui.TableSetColumnIndex(1);
        ImGui.SetNextItemWidth(-1);

        bool disabled = !parameter.Overridden;
        if (disabled) ImGui.BeginDisabled();

        // Clamped/Color subtypes first; the base-type cases catch the rest of each family.
        switch (parameter) {
            case IEnumParameter e: {
                var index = e.Index;
                if (ImGui.Combo("##v", ref index, e.Names, e.Names.Length)) { e.Index = index; changed = true; }
                break;
            }
            case BoolParameter b: {
                var value = b.Value;
                if (ImGui.Checkbox("##v", ref value)) { b.Value = value; changed = true; }
                break;
            }
            case ClampedIntParameter ci: {
                var value = ci.Value;
                if (ImGui.SliderInt("##v", ref value, ci.Min, ci.Max)) { ci.Value = value; changed = true; }
                break;
            }
            case IntParameter i: {
                var value = i.Value;
                if (ImGui.DragInt("##v", ref value)) { i.Value = value; changed = true; }
                break;
            }
            case ClampedFloatParameter cf: {
                var value = cf.Value;
                if (ImGui.SliderFloat("##v", ref value, cf.Min, cf.Max)) { cf.Value = value; changed = true; }
                break;
            }
            case FloatParameter f: {
                var value = f.Value;
                if (ImGui.DragFloat("##v", ref value, 0.05f)) { f.Value = value; changed = true; }
                break;
            }
            case ColorParameter c: {
                var value = new System.Numerics.Vector3(c.Value.X, c.Value.Y, c.Value.Z);
                var flags = c.Hdr ? ImGuiColorEditFlags.Hdr | ImGuiColorEditFlags.Float : ImGuiColorEditFlags.None;
                if (ImGui.ColorEdit3("##v", ref value, flags)) {
                    c.Value = new OpenTK.Mathematics.Vector3(value.X, value.Y, value.Z);
                    changed = true;
                }
                break;
            }
            case Vector3Parameter v3: {
                var value = new System.Numerics.Vector3(v3.Value.X, v3.Value.Y, v3.Value.Z);
                if (ImGui.DragFloat3("##v", ref value, 0.05f)) {
                    v3.Value = new OpenTK.Mathematics.Vector3(value.X, value.Y, value.Z);
                    changed = true;
                }
                break;
            }
            default:
                ImGui.TextDisabled($"({parameter.GetType().Name})");
                break;
        }

        if (disabled) ImGui.EndDisabled();
        ImGui.PopID();
        return changed;
    }

    // Compact framed header with an Active checkbox overlaid after the arrow (the inline version
    // of InspectorPanel's component header) and a "..." menu button on the right edge. Remove
    // Override is reachable from both that menu and a right-click on the header.
    static bool OverrideHeader(string label, ref bool active, out bool removeRequested) {
        removeRequested = false;

        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new SysVec2(8, 5));
        bool open = ImGui.TreeNodeEx($"     {label}###ovr_{label}",
            ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.AllowOverlap | ImGuiTreeNodeFlags.Framed |
            ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.NoTreePushOnOpen);
        ImGui.PopStyleVar();

        if (ImGui.BeginPopupContextItem("##overridectx")) {
            if (ImGui.MenuItem("Remove Override"))
                removeRequested = true;
            ImGui.EndPopup();
        }

        SysVec2 min = ImGui.GetItemRectMin();
        SysVec2 max = ImGui.GetItemRectMax();
        SysVec2 cursor = ImGui.GetCursorScreenPos();
        float rowCenterY = min.Y + (max.Y - min.Y - ImGui.GetFrameHeight()) * 0.5f + 1;

        ImGui.SetCursorScreenPos(new SysVec2(min.X + 24, rowCenterY));
        ImGui.Checkbox($"##active_{label}", ref active);

        float menuW = ImGui.GetFrameHeight() + 8;
        ImGui.SetCursorScreenPos(new SysVec2(max.X - menuW - 4, rowCenterY));
        ImGui.PushStyleColor(ImGuiCol.Button, new SysVec4(0, 0, 0, 0));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new SysVec4(1, 1, 1, 0.08f));
        var menuClicked = ImGui.Button($"{EditorIcons.More}##menu_{label}", new SysVec2(menuW, 0));
        ImGui.PopStyleColor(2);
        if (menuClicked)
            ImGui.OpenPopup("##overridemenu");
        if (ImGui.BeginPopup("##overridemenu")) {
            if (ImGui.MenuItem("Remove Override"))
                removeRequested = true;
            ImGui.EndPopup();
        }

        ImGui.SetCursorScreenPos(cursor);
        return open;
    }

    static string Prettify(string name) {
        if (string.IsNullOrEmpty(name))
            return name;

        var result = new System.Text.StringBuilder(name.Length + 4);
        result.Append(char.ToUpperInvariant(name[0]));
        for (var i = 1; i < name.Length; i++) {
            if (char.IsUpper(name[i]) && !char.IsUpper(name[i - 1]))
                result.Append(' ');
            result.Append(name[i]);
        }
        return result.ToString();
    }
}
