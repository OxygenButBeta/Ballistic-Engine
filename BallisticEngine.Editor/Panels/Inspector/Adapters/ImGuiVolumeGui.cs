using System;
using Hexa.NET.ImGui;
using SysVec2 = System.Numerics.Vector2;
using SysVec3 = System.Numerics.Vector3;

namespace BallisticEngine.Editor.Inspector;

// INTEGRATION-TIME (compiled in the real editor; NOT in the headless test). The volume-profile path's
// IInspectorGui: BeginRow draws the per-parameter override checkbox + label and disables the value cell
// when not overridden (exactly VolumeProfileEditor.DrawParameter's chrome). The host loop owns the
// 2-column table; per row it calls pipeline.Draw, then OR's in TakeOverrideChanged() so toggling an
// override checkbox still marks the profile dirty even when the value itself didn't change.
public sealed class ImGuiVolumeGui : IInspectorGui {
    bool gatedByOverride;
    bool overrideChanged;

    public bool TakeOverrideChanged() { bool c = overrideChanged; overrideChanged = false; return c; }

    public void PushId(string id) => ImGui.PushID(id);
    public void PopId() => ImGui.PopID();
    public void BeginDisabled() => ImGui.BeginDisabled();
    public void EndDisabled() => ImGui.EndDisabled();

    public void BeginRow(IProperty p) {
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);

        bool overridden = p.Overridden;
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new SysVec2(2, 2) * EditorTheme.UiScale);
        if (ImGui.Checkbox("##override", ref overridden)) { p.Overridden = overridden; overrideChanged = true; }
        ImGui.PopStyleVar();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(overridden ? "Overriding. Click to use the default." : "Click to override this parameter.");

        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(p.Label);
        if (p.Tooltip is not null && ImGui.IsItemHovered())
            ImGui.SetTooltip(p.Tooltip);

        ImGui.TableSetColumnIndex(1);
        ImGui.SetNextItemWidth(-1);

        gatedByOverride = !p.Overridden;
        if (gatedByOverride) ImGui.BeginDisabled();
    }

    public void EndRow() { if (gatedByOverride) ImGui.EndDisabled(); }

    // Volume params carry no Header/Space today; keep them inert inside the table.
    public void Header(string t) { }
    public void Space(float h) { }
    public void HelpBox(string t) { ImGui.TextDisabled(t); }

    public bool Checkbox(ref bool v) {
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new SysVec2(2, 2) * EditorTheme.UiScale);
        bool changed = ImGui.Checkbox("##v", ref v);
        ImGui.PopStyleVar();
        return changed;
    }
    public bool SliderFloat(ref float v, float min, float max) {
        ImGui.PushStyleColor(ImGuiCol.SliderGrab, EditorTheme.SliderGrabRest);   // EF11: legible value over the grab
        bool changed = ScalarField.SliderFloat("##v", ref v, min, max, "%.3f");  // double-click to type
        ImGui.PopStyleColor();
        return changed;
    }
    public bool DragFloat(ref float v, float speed) => ScalarField.DragFloat("##v", ref v, speed, 0, 0, "%.3f");
    public bool SliderInt(ref int v, int min, int max) {
        ImGui.PushStyleColor(ImGuiCol.SliderGrab, EditorTheme.SliderGrabRest);   // EF11: legible value over the grab
        bool changed = ScalarField.SliderInt("##v", ref v, min, max);
        ImGui.PopStyleColor();
        return changed;
    }
    public bool DragInt(ref int v) => ScalarField.DragInt("##v", ref v);
    public bool InputText(ref string v, int maxLength) => ImGui.InputText("##v", ref v, (uint)maxLength);
    public bool Combo(ref int index, string[] names) => ImGui.Combo("##v", ref index, names, names.Length);
    public bool ColorEdit3(ref SysVec3 v, bool hdr) =>
        ImGui.ColorEdit3("##v", ref v, hdr ? ImGuiColorEditFlags.Hdr | ImGuiColorEditFlags.Float : ImGuiColorEditFlags.None);
    public bool DragFloat2(ref System.Numerics.Vector2 v, float speed) => ImGui.DragFloat2("##v", ref v, speed);
    public bool DragFloat3(ref SysVec3 v, float speed) => ImGui.DragFloat3("##v", ref v, speed);
    public void Unsupported(Type t) => ImGui.TextDisabled($"({t.Name})");
}
