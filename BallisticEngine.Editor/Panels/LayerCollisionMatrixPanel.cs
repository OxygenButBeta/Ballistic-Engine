namespace BallisticEngine.Editor;

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
