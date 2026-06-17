using BallisticEngine.AssetPipeline;
using Hexa.NET.ImGui;
using SysVec2 = System.Numerics.Vector2;
using SysVec4 = System.Numerics.Vector4;

namespace BallisticEngine.Editor;

// Project Tags & Layers editor (Window > Tags & Layers). Edits the engine's TagManager / LayerManager
// directly and persists to ProjectSettings/TagsAndLayers.json via LayerSettings.Save on every change
// (project-level config, not scene state — no scene undo, same as other project settings).
// EF8: the Layer Collision Matrix was SPLIT out into LayerCollisionMatrixPanel (Window > Layer Collision
// Matrix); this panel now owns only tag + layer definitions. Both read the same LayerManager store.
internal sealed class TagsLayersPanel {
    public bool Open;

    string newTag = "";

    void Persist() {
        if (AssetDatabase.Project is not null)
            LayerSettings.Save(AssetDatabase.Project);
    }

    public void Draw(float scale) {
        if (!Open)
            return;

        ImGui.SetNextWindowSize(new SysVec2(560 * scale, 560 * scale), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin($"{EditorIcons.Settings}  Tags & Layers", ref Open, ImGuiWindowFlags.NoCollapse)) {
            ImGui.End();
            return;
        }

        DrawTags();
        ImGui.Spacing();
        DrawLayers(scale);

        ImGui.End();
    }

    void DrawTags() {
        if (!ImGui.CollapsingHeader("Tags", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        // Existing tags, each with a remove button. "Untagged" is the reserved default — not removable.
        foreach (string tag in TagManager.Tags.ToArray()) {
            ImGui.PushID(tag);
            bool reserved = tag == TagManager.Untagged;
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted($"{EditorIcons.Pin}  {tag}");
            if (!reserved) {
                ImGui.SameLine(ImGui.GetContentRegionAvail().X - ImGui.GetFrameHeight());
                if (EditorIcons.GhostButtonSmall("rm", EditorIcons.Delete, "Remove tag")) {
                    TagManager.RemoveTag(tag);
                    Persist();
                }
            }
            ImGui.PopID();
        }

        ImGui.Spacing();
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 90 * ImGui.GetIO().DisplayFramebufferScale.X - 100);
        ImGui.InputTextWithHint("##newtag", "New tag name...", ref newTag, 64);
        ImGui.SameLine();
        ImGui.BeginDisabled(string.IsNullOrWhiteSpace(newTag) || TagManager.IsDefined(newTag));
        if (ImGui.Button($"{EditorIcons.Add}  Add Tag")) {
            TagManager.AddTag(newTag.Trim());
            newTag = "";
            Persist();
        }
        ImGui.EndDisabled();
    }

    void DrawLayers(float scale) {
        if (!ImGui.CollapsingHeader("Layers", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        // All 32 slots. 0..7 are conventionally builtin (Default etc.) — still editable, Unity lets you
        // rename them too. An empty name = an undefined/unused layer.
        for (var i = 0; i < LayerManager.LayerCount; i++) {
            ImGui.PushID(i);
            ImGui.AlignTextToFramePadding();
            ImGui.TextDisabled($"{i,2}");
            ImGui.SameLine(40 * scale);
            ImGui.SetNextItemWidth(-1);
            var name = LayerManager.NameOf(i) ?? "";
            if (ImGui.InputText("##name", ref name, 64)) {
                LayerManager.SetName(i, name);
            }
            if (ImGui.IsItemDeactivatedAfterEdit())
                Persist();
            ImGui.PopID();
        }
    }
}
