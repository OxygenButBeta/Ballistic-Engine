using BallisticEngine.AssetPipeline;
using Hexa.NET.ImGui;
using SysVec2 = System.Numerics.Vector2;
using SysVec4 = System.Numerics.Vector4;

namespace BallisticEngine.Editor;

// Project Tags & Layers editor (Window > Tags & Layers). Edits the engine's TagManager / LayerManager
// directly and persists to ProjectSettings/TagsAndLayers.json via LayerSettings.Save on every change
// (project-level config, not scene state — no scene undo, same as other project settings).
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
        ImGui.Spacing();
        DrawCollisionMatrix(scale);

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

    void DrawCollisionMatrix(float scale) {
        if (!ImGui.CollapsingHeader("Layer Collision Matrix", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        // Only named layers participate (an unnamed layer has nothing to collide as). The matrix is
        // symmetric: a checkbox at (row, col) drives SetCollision(row, col) and mirrors automatically.
        var layers = LayerManager.DefinedLayers().ToList();
        if (layers.Count == 0) {
            ImGui.TextDisabled("Name some layers above to edit their collision matrix.");
            return;
        }

        ImGui.TextDisabled("Checked = the two layers' physics bodies collide.");
        ImGui.Spacing();

        float cell = ImGui.GetFrameHeight();
        float labelW = 0;
        foreach ((_, string name) in layers)
            labelW = MathF.Max(labelW, ImGui.CalcTextSize(name).X);
        labelW += 12 * scale;

        var draw = ImGui.GetWindowDrawList();
        SysVec2 origin = ImGui.GetCursorScreenPos();

        // Column headers: layer names rotated would be ideal, but horizontal indices keep it readable.
        // We draw a triangular grid (upper triangle) — row labels on the left, a small index on top.
        for (var r = 0; r < layers.Count; r++) {
            (int rowIndex, string rowName) = layers[r];
            ImGui.PushID(rowIndex);

            ImGui.AlignTextToFramePadding();
            ImGui.SetCursorPosX(ImGui.GetCursorPosX());
            ImGui.TextUnformatted(rowName);
            ImGui.SameLine(labelW);

            // Each checkbox pairs this row layer with every OTHER named layer at-or-after it (so each
            // unordered pair shows once). Includes the self-pair (a layer colliding with itself).
            for (var col = r; col < layers.Count; col++) {
                (int colIndex, string colName) = layers[col];
                ImGui.PushID(colIndex);
                bool collide = LayerManager.GetCollision(rowIndex, colIndex);
                if (ImGui.Checkbox("##c", ref collide)) {
                    LayerManager.SetCollision(rowIndex, colIndex, collide);
                    Persist();
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip($"{rowName}  ↔  {colName}");
                ImGui.SameLine(0, 4 * scale);
                ImGui.PopID();
            }
            ImGui.NewLine();
            ImGui.PopID();
        }
        _ = (draw, origin); // (reserved for a future rotated-header pass)
    }
}
