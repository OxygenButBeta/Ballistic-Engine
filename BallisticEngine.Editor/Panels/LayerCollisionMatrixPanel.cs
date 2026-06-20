using System.Numerics;
using BallisticEngine.AssetPipeline;

namespace BallisticEngine.Editor;

// EF8: the Layer Collision Matrix lives in its OWN window (Window > Layer Collision Matrix), split out of
// TagsLayersPanel so tag/layer definitions and the physics collision grid are two distinct surfaces. Reads
// the SAME LayerManager store as TagsLayersPanel (defining layers there feeds this matrix) and persists every
// edit to ProjectSettings/TagsAndLayers.json via LayerSettings.Save (project-level config, no scene undo).
//
// Phase-2 EditorWindow: the body draws through IEditorGui (no raw ImGui). WindowShell owns Begin/End.
internal sealed class LayerCollisionMatrixPanel : EditorWindow {
    public LayerCollisionMatrixPanel() {
        DockKey = "win.layercollision";
        Title = "Layer Collision Matrix";
        Icon = EditorIcons.Settings;
        NoCollapse = true;
        DesiredSize = new Vector2(520, 480);
    }

    static void Persist() {
        if (AssetDatabase.Project is not null)
            LayerSettings.Save(AssetDatabase.Project);
    }

    protected override void OnGui(IEditorGui gui) {
        // Only named layers participate (an unnamed layer has nothing to collide as). The matrix is
        // symmetric: a checkbox at (row, col) drives SetCollision(row, col) and mirrors automatically.
        var layers = LayerManager.DefinedLayers().ToList();
        if (layers.Count == 0) {
            gui.TextDisabled("Name some layers in Tags & Layers to edit their collision matrix.");
            return;
        }

        gui.TextDisabled("Checked = the two layers' physics bodies collide.");
        gui.Spacing();

        float labelW = 0;
        foreach ((_, string name) in layers)
            labelW = MathF.Max(labelW, gui.CalcTextSize(name).X);
        labelW += 12 * gui.Scale;

        for (var r = 0; r < layers.Count; r++) {
            (int rowIndex, string rowName) = layers[r];
            gui.PushId(rowIndex);

            gui.AlignTextToFramePadding();
            gui.TextUnformatted(rowName);
            gui.SameLine(labelW);

            // Each checkbox pairs this row layer with every OTHER named layer at-or-after it (so each
            // unordered pair shows once). Includes the self-pair (a layer colliding with itself).
            for (var col = r; col < layers.Count; col++) {
                (int colIndex, string colName) = layers[col];
                gui.PushId(colIndex);
                bool collide = LayerManager.GetCollision(rowIndex, colIndex);
                if (gui.Checkbox("##c", ref collide)) {
                    LayerManager.SetCollision(rowIndex, colIndex, collide);
                    Persist();
                }
                if (gui.IsItemHovered())
                    gui.Tooltip($"{rowName}  <->  {colName}");
                gui.SameLine(0, 4 * gui.Scale);
                gui.PopId();
            }
            gui.NewLine();
            gui.PopId();
        }
    }
}
