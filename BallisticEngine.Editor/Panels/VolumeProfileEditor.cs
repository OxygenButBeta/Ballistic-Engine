using System.Reflection;
using BallisticEngine.AssetPipeline.Loaders;
using BallisticEngine.Editor.Inspector;
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
                // A disabled component contributes NOTHING to the stack (VolumeManager skips it),
                // even if its parameters are overridden — a common "I changed it and nothing happens"
                // trap. Warn clearly, and offer a one-click Enable.
                if (!component.Active && HasOverrides(component)) {
                    ImGui.PushStyleColor(ImGuiCol.Text, new SysVec4(1f, 0.72f, 0.25f, 1f));
                    ImGui.TextWrapped($"{EditorIcons.Warning} This override is DISABLED — its parameters have no effect. " +
                                      "Tick the checkbox by the name to enable it.");
                    ImGui.PopStyleColor();
                    if (ImGui.SmallButton("Enable")) {
                        component.Active = true;
                        changed = true;
                    }
                    ImGui.Spacing();
                }

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
        if (ImGui.Button($"{EditorIcons.Add}  Add Override", new SysVec2(-1, 0))) {
            addOverrideSearch = "";
            ImGui.OpenPopup("##addoverride");
        }

        if (ImGui.BeginPopup("##addoverride")) {
            // Search field (Unity's Add Override is searchable). Auto-focus on open; Enter adds the
            // first match.
            if (ImGui.IsWindowAppearing())
                ImGui.SetKeyboardFocusHere();
            ImGui.SetNextItemWidth(220);
            ImGui.InputTextWithHint("##search", "Search...", ref addOverrideSearch, 64);
            bool enter = ImGui.IsItemFocused() && ImGui.IsKeyPressed(ImGuiKey.Enter);
            ImGui.Separator();

            bool searching = addOverrideSearch.Length > 0;
            bool any = false;
            ComponentEntry? firstMatch = null;
            ImGui.BeginChild("##addlist", new SysVec2(220, 240));
            foreach (ComponentEntry entry in ComponentRegistry.VolumeMenu) {
                if (profile.Has(entry.Type))
                    continue;
                if (searching && !entry.DisplayName.Contains(addOverrideSearch, StringComparison.OrdinalIgnoreCase))
                    continue;
                any = true;
                firstMatch ??= entry;
                if (ImGui.MenuItem(entry.DisplayName)) {
                    profile.Add(entry.Type);
                    changed = true;
                    ImGui.CloseCurrentPopup();
                }
            }
            if (!any)
                ImGui.TextDisabled(searching ? "No match." : "All overrides added.");
            ImGui.EndChild();

            if (enter && firstMatch is { } hit) {
                profile.Add(hit.Type);
                changed = true;
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }

        return changed;
    }

    static string addOverrideSearch = "";

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

    // True if any parameter is overridden — used to warn when those overrides are inert because the
    // component itself is disabled.
    static bool HasOverrides(VolumeComponent component) {
        foreach (VolumeComponent.ParameterSlot slot in component.Parameters)
            if (slot.Parameter.Overridden)
                return true;
        return false;
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

    // The shared inspector drawer pipeline (Odin-style): the SAME value drawers + conditional/ordering
    // attributes the component inspector uses, so the two paths can't drift. ImGuiVolumeGui draws the
    // per-parameter override checkbox + label and disables the value cell when not overridden;
    // [ShowIf]/[HideIf] on a parameter field hide its row.
    static readonly DrawerStack pipeline = DrawerStack.CreateDefault();
    static readonly ImGuiVolumeGui volumeGui = new();

    static bool DrawParameters(VolumeComponent component) {
        if (!ImGui.BeginTable("##params", 2, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.PadOuterX))
            return false;
        ImGui.TableSetupColumn("label", ImGuiTableColumnFlags.WidthStretch, 0.45f);
        ImGui.TableSetupColumn("value", ImGuiTableColumnFlags.WidthStretch, 0.55f);

        var changed = false;
        // [PropertyOrder] sorts (stable: default 0 keeps declaration order).
        foreach (VolumeComponent.ParameterSlot slot in System.Linq.Enumerable.OrderBy(
                     component.Parameters, s => MemberAttributes.For(s.Field).Order)) {
            changed |= pipeline.Draw(new VolumeParamProperty(slot, component), volumeGui);
            changed |= volumeGui.TakeOverrideChanged();   // toggling the override checkbox is also a change
        }

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

    // (The per-parameter value switch is gone -- DrawParameters now runs every slot through the shared
    // composable DrawerStack + ImGuiVolumeGui (B0), the same value drawers the component inspector uses.)

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

    // ---- In-memory snapshot/restore for undo (bug 2b) ------------------------------------------
    // A volume profile is a .volume ASSET, not scene data, so the scene-snapshot undo doesn't cover
    // it. These capture/restore the profile's component set + each parameter's (Overridden, Value) so
    // an edit can be pushed as a callback undo step. Value is read/written via the parameter's public
    // `Value` property by reflection (the type is generic VolumeParameter<T>).

    internal sealed class ProfileSnapshot {
        public List<CompSnap> Components = new();
        public sealed class CompSnap { public Type Type; public bool Active; public List<ParamSnap> Params = new(); }
        public sealed class ParamSnap { public string Name; public bool Overridden; public object Value; }
    }

    public static object Snapshot(VolumeProfile profile) {
        var snap = new ProfileSnapshot();
        foreach (VolumeComponent c in profile.Components) {
            var cs = new ProfileSnapshot.CompSnap { Type = c.GetType(), Active = c.Active };
            foreach (VolumeComponent.ParameterSlot slot in c.Parameters)
                cs.Params.Add(new ProfileSnapshot.ParamSnap {
                    Name = slot.Name,
                    Overridden = slot.Parameter.Overridden,
                    Value = ValueProp(slot.Parameter)?.GetValue(slot.Parameter),
                });
            snap.Components.Add(cs);
        }
        return snap;
    }

    public static void Restore(VolumeProfile profile, object snapshotObj) {
        if (snapshotObj is not ProfileSnapshot snap)
            return;
        // Remove components no longer in the snapshot; add ones that are missing.
        foreach (VolumeComponent c in profile.Components.ToArray())
            if (!snap.Components.Exists(cs => cs.Type == c.GetType()))
                profile.Remove(c);
        foreach (ProfileSnapshot.CompSnap cs in snap.Components) {
            VolumeComponent c = profile.Get(cs.Type) ?? profile.Add(cs.Type);
            c.Active = cs.Active;
            foreach (VolumeComponent.ParameterSlot slot in c.Parameters) {
                ProfileSnapshot.ParamSnap ps = cs.Params.Find(p => p.Name == slot.Name);
                if (ps is null) continue;
                slot.Parameter.Overridden = ps.Overridden;
                System.Reflection.PropertyInfo vp = ValueProp(slot.Parameter);
                if (vp is not null && vp.CanWrite && ps.Value is not null)
                    vp.SetValue(slot.Parameter, ps.Value);
            }
        }
    }

    static System.Reflection.PropertyInfo ValueProp(VolumeParameter p) =>
        p.GetType().GetProperty("Value");
}
