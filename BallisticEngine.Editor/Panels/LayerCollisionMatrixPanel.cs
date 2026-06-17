using BallisticEngine.AssetPipeline;
using Hexa.NET.ImGui;
using SysVec2 = System.Numerics.Vector2;

namespace BallisticEngine.Editor;

// EF8: the Layer Collision Matrix lives in its OWN window (Window > Layer Collision Matrix), split out of
// TagsLayersPanel so tag/layer definitions and the physics collision grid are two distinct surfaces. Reads
// the SAME LayerManager store as TagsLayersPanel (defining layers there feeds this matrix) and persists every
// edit to ProjectSettings/TagsAndLayers.json via LayerSettings.Save (project-level config, no scene undo).
internal sealed class LayerCollisionMatrixPanel {
    public bool Open;

    void Persist() {
        if (AssetDatabase.Project is not null)
            LayerSettings.Save(AssetDatabase.Project);
    }

    public void Draw(float scale) {
        if (!Open)
            return;

        ImGui.SetNextWindowSize(new SysVec2(520 * scale, 480 * scale), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin($"{EditorIcons.Settings}  Layer Collision Matrix", ref Open, ImGuiWindowFlags.NoCollapse)) {
            ImGui.End();
            return;
        }

        DrawCollisionMatrix(scale);

        ImGui.End();
    }

    void DrawCollisionMatrix(float scale) {
        // Only named layers participate (an unnamed layer has nothing to collide as). The matrix is
        // symmetric: a checkbox at (row, col) drives SetCollision(row, col) and mirrors automatically.
        var layers = LayerManager.DefinedLayers().ToList();
        if (layers.Count == 0) {
            ImGui.TextDisabled("Name some layers in Tags & Layers to edit their collision matrix.");
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
        _ = (draw, origin, cell); // (reserved for a future rotated-header pass)
    }
}
