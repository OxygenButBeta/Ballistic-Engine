using BallisticEngine.AssetPipeline.Loaders;
using BallisticEngine.Editor.Inspector;
using SysVec2 = System.Numerics.Vector2;
using SysVec4 = System.Numerics.Vector4;

namespace BallisticEngine.Editor;

internal static class VolumeProfileEditor {
    static IEditorGui gui => EditorGui.Shared;

    static readonly Dictionary<string, float> bandHeights = new();

    public static bool Draw(VolumeProfile profile) {
        var changed = false;
        VolumeComponent remove = null;

        var componentOrdinal = 0;
        foreach (VolumeComponent component in profile.Components) {
            gui.PushId(component.GetType().Name);

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

    public static void SaveToAsset(VolumeProfile profile) {
        if (!AssetDatabase.TryGetAssetGuid(profile, out Guid guid))
            return;

        var assetPath = AssetDatabase.GuidToAssetPath(guid);
        if (assetPath is null)
            return;

        VolumeProfileLoader.Save(profile, AssetDatabase.Project.ResolveAbsolute(assetPath));
    }

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

    static readonly DrawerStack pipeline = DrawerStack.CreateDefault();
    static readonly ImGuiVolumeGui volumeGui = new();

    static bool DrawParameters(VolumeComponent component) {
        if (!gui.BeginTable("##params", 2, EditorTableFlags.SizingStretchProp | EditorTableFlags.PadOuterX))
            return false;
        gui.TableSetupColumn("label", EditorColumnFlags.WidthStretch, 0.45f);
        gui.TableSetupColumn("value", EditorColumnFlags.WidthStretch, 0.55f);

        var changed = false;
        foreach (VolumeComponent.ParameterSlot slot in
                 PropertyOrdering.Sort(component.Parameters, s => PropertyOrdering.OrderOf(s.Field))) {
            changed |= pipeline.Draw(new VolumeParamProperty(slot, component), volumeGui);
            changed |= volumeGui.TakeOverrideChanged();
        }

        gui.EndTable();
        gui.Spacing();

        if (changed)
            EnforceParametermnvariants(component);

        return changed;
    }

    static void EnforceParametermnvariants(VolumeComponent component) {
        if (component is not Exposure exposure)
            return;
        if (exposure.limitMin.Value > exposure.limitMax.Value)
            exposure.limitMax.Value = exposure.limitMin.Value;
    }

    static bool OverrideHeader(string label, ref bool active, out bool removeRequested) {
        removeRequested = false;

        gui.PushFramePadding(new SysVec2(8, 5));
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
