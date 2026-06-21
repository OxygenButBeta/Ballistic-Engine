using System.Reflection;
using BallisticEngine.AssetPipeline.Loaders;
using BallisticEngine.Editor.Inspector;
using SysVec2 = System.Numerics.Vector2;
using SysVec4 = System.Numerics.Vector4;

namespace BallisticEngine.Editor;

// Unity-style volume profile editor: one collapsible header per override component (with its
// Active checkbox and a right-click Remove), an override checkbox + value widget per parameter,
// and an Add Override menu of the remaining VolumeComponent types. Drawn both inline under a
// Volume component and as the asset view of a selected .volume file. Draw() returns true when
// anything changed; SaveToAsset persists the live (AssetDatabase-cached) instance to its JSON.
//
// Phase-7: routes through the IEditorGui seam (EditorGui.Shared) — zero raw ImGui. The custom override
// HEADER (a framed bar with an overlaid Active checkbox + "…" menu, positioned by item-rect geometry) uses
// the seam's FramedHeader + ItemRectMin/Max + SetCursorScreenPos. The shared DrawerStack pipeline draws the
// per-parameter rows through ImGuiVolumeGui (already on the seam).
internal static class VolumeProfileEditor {
    static IEditorGui gui => EditorGui.Shared;

    // Per-override body height carried frame-to-frame so the alternating band can paint without a nested
    // ChannelsSplit (the inspector body we draw inside is already split, and ImGui forbids nesting).
    static readonly Dictionary<string, float> bandHeights = new();

    public static bool Draw(VolumeProfile profile) {
        var changed = false;
        VolumeComponent remove = null;

        var componentOrdinal = 0;   // running count of overrides, for alternating body-bg banding
        foreach (VolumeComponent component in profile.Components) {
            gui.PushId(component.GetType().Name);

            // Alternating per-override band (1-a, 2-b, 3-a ...). The editor draws this profile INLINE inside
            // an inspector component body that is ALREADY mid-channel-split, and ImGui forbids nested
            // ChannelsSplit. So instead of splitting to paint behind, we draw the band first against a
            // measured height from the PREVIOUS frame (cached per component) — no split, nest-safe. The
            // first frame a new override has no cached height yet, so it simply skips its band that one frame.
            var draw = gui.WindowDrawList;
            SysVec2 bandStart = gui.CursorScreenPos;
            string bandKey = component.GetType().FullName ?? component.GetType().Name;
            if ((componentOrdinal & 1) == 1 && bandHeights.TryGetValue(bandKey, out float prevH) && prevH > 0) {
                float wx0 = gui.WindowPos.X;
                float wx1 = wx0 + gui.WindowSize.X;
                draw.AddRectFilled(new SysVec2(wx0, bandStart.Y - 2), new SysVec2(wx1, bandStart.Y + prevH + 4),
                                   gui.ColorU32(new SysVec4(0f, 0f, 0f, 0.06f)), 6f);
            }

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
                    gui.PushColor(EditorStyleColor.Text, EditorTheme.Warning);
                    gui.TextWrapped($"{EditorIcons.Warning} This override is DISABLED — its parameters have no effect. " +
                                    "Tick the checkbox by the name to enable it.");
                    gui.PopColor();
                    if (gui.SmallButton("Enable")) {
                        component.Active = true;
                        changed = true;
                    }
                    gui.Spacing();
                }

                changed |= DrawAllNoneRow(component);
                changed |= DrawParameters(component);
            }

            // Cache this override's body height so next frame can paint its band without a nested split.
            bandHeights[bandKey] = gui.CursorScreenPos.Y - bandStart.Y;

            componentOrdinal++;
            gui.PopId();
        }

        if (remove is not null) {
            profile.Remove(remove);
            changed = true;
        }

        gui.Spacing();
        if (gui.Button($"{EditorIcons.Add}  Add Override", new SysVec2(-1, 0))) {
            addOverrideSearch = "";
            gui.OpenPopup("##addoverride");
        }

        if (gui.BeginPopup("##addoverride")) {
            // Search field (Unity's Add Override is searchable). Auto-focus on open; Enter adds the
            // first match.
            if (gui.IsWindowAppearing())
                gui.SetKeyboardFocusHere();
            gui.SetNextItemWidth(220);
            gui.InputTextWithHint("##search", "Search...", ref addOverrideSearch, 64);
            bool enter = gui.IsItemFocused() && gui.KeyPressed(EditorGuiKey.Enter);
            gui.Separator();

            bool searching = addOverrideSearch.Length > 0;
            bool any = false;
            ComponentEntry? firstMatch = null;
            gui.BeginChild("##addlist", new SysVec2(220, 240), border: false);
            foreach (ComponentEntry entry in ComponentRegistry.VolumeMenu) {
                if (profile.Has(entry.Type))
                    continue;
                if (searching && !entry.DisplayName.Contains(addOverrideSearch, StringComparison.OrdinalIgnoreCase))
                    continue;
                any = true;
                firstMatch ??= entry;
                if (gui.MenuItem(entry.DisplayName)) {
                    profile.Add(entry.Type);
                    changed = true;
                    gui.CloseCurrentPopup();
                }
            }
            if (!any)
                gui.TextDisabled(searching ? "No match." : "All overrides added.");
            gui.EndChild();

            if (enter && firstMatch is { } hit) {
                profile.Add(hit.Type);
                changed = true;
                gui.CloseCurrentPopup();
            }
            gui.EndPopup();
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

        gui.AlignTextToFramePadding();
        gui.TextDisabled("Override:");
        gui.SameLine();
        if (gui.SmallButton("All"))
            changed = SetAllOverrides(component, true);
        gui.SameLine();
        if (gui.SmallButton("None"))
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
        if (!gui.BeginTable("##params", 2, EditorTableFlags.SizingStretchProp | EditorTableFlags.PadOuterX))
            return false;
        gui.TableSetupColumn("label", EditorColumnFlags.WidthStretch, 0.45f);
        gui.TableSetupColumn("value", EditorColumnFlags.WidthStretch, 0.55f);

        var changed = false;
        // [PropertyOrder] sorts via the single-sourced engine rule (stable: default 0 keeps slot order) --
        // same ordering the component inspector uses, keyed on the parameter's backing field, so the two
        // inspector paths can't drift on member order.
        foreach (VolumeComponent.ParameterSlot slot in
                     PropertyOrdering.Sort(component.Parameters, s => PropertyOrdering.OrderOf(s.Field))) {
            changed |= pipeline.Draw(new VolumeParamProperty(slot, component), volumeGui);
            changed |= volumeGui.TakeOverrideChanged();   // toggling the override checkbox is also a change
        }

        gui.EndTable();
        gui.Spacing();

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

        gui.PushFramePadding(new SysVec2(8, 5));
        // 5-space indent leaves room for the overlaid Active checkbox; ###id keeps a stable header identity.
        bool open = gui.FramedHeader($"     {label}###ovr_{label}");
        gui.PopStyleVar();

        if (gui.BeginPopupContextItem("##overridectx")) {
            if (gui.MenuItem("Remove Override"))
                removeRequested = true;
            gui.EndPopup();
        }

        SysVec2 min = gui.ItemRectMin;
        SysVec2 max = gui.ItemRectMax;
        SysVec2 cursor = gui.CursorScreenPos;
        float rowCenterY = min.Y + (max.Y - min.Y - gui.FrameHeight) * 0.5f + 1;

        gui.SetCursorScreenPos(new SysVec2(min.X + 24, rowCenterY));
        gui.Checkbox($"##active_{label}", ref active);

        float menuW = gui.FrameHeight + 8;
        gui.SetCursorScreenPos(new SysVec2(max.X - menuW - 4, rowCenterY));
        gui.PushColor(EditorStyleColor.Button, new SysVec4(0, 0, 0, 0));
        gui.PushColor(EditorStyleColor.ButtonHovered, new SysVec4(1, 1, 1, 0.08f));
        var menuClicked = gui.Button($"{EditorIcons.More}##menu_{label}", new SysVec2(menuW, 0));
        gui.PopColor(2);
        if (menuClicked)
            gui.OpenPopup("##overridemenu");
        if (gui.BeginPopup("##overridemenu")) {
            if (gui.MenuItem("Remove Override"))
                removeRequested = true;
            gui.EndPopup();
        }

        gui.SetCursorScreenPos(cursor);
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
