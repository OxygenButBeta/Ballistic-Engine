namespace BallisticEngine.Editor;

internal sealed class TagsLayersPanel : EditorWindow {
    string newTag = "";

    public TagsLayersPanel() {
        DockKey = "win.tagslayers";
        Title = "Tags & Layers";
        Icon = EditorIcons.Settings;
        NoCollapse = true;
        DesiredSize = new Vector2(560, 560);
    }

    static void Persist() {
        if (AssetDatabase.Project is not null)
            LayerSettings.Save(AssetDatabase.Project);
    }

    protected override void OnGui(IEditorGui gui) {
        DrawTags(gui);
        gui.Spacing();
        DrawLayers(gui);
    }

    void DrawTags(IEditorGui gui) {
        if (!gui.CollapsingHeader("Tags", defaultOpen: true))
            return;

        foreach (string tag in TagManager.Tags.ToArray()) {
            gui.PushId(tag);
            bool reserved = tag == TagManager.Untagged;
            gui.AlignTextToFramePadding();
            gui.TextUnformatted($"{EditorIcons.Pin}  {tag}");
            if (!reserved) {
                gui.SameLine(gui.ContentRegionAvail.X - gui.FrameHeight);
                if (EditorIcons.GhostButtonSmall("rm", EditorIcons.Delete, "Remove tag")) {
                    TagManager.RemoveTag(tag);
                    Persist();
                }
            }
            gui.PopId();
        }

        gui.Spacing();
        gui.SetNextItemWidth(gui.ContentRegionAvail.X - 100);
        gui.InputTextWithHint("##newtag", "New tag name...", ref newTag, 64);
        gui.SameLine();
        gui.BeginDisabled(string.IsNullOrWhiteSpace(newTag) || TagManager.IsDefined(newTag));
        if (gui.Button($"{EditorIcons.Add}  Add Tag")) {
            TagManager.AddTag(newTag.Trim());
            newTag = "";
            Persist();
        }
        gui.EndDisabled();
    }

    void DrawLayers(IEditorGui gui) {
        if (!gui.CollapsingHeader("Layers", defaultOpen: true))
            return;

        for (var i = 0; i < LayerManager.LayerCount; i++) {
            gui.PushId(i);
            gui.AlignTextToFramePadding();
            gui.TextDisabled($"{i,2}");
            gui.SameLine(40 * gui.Scale);
            gui.SetNextItemWidth(-1);
            var name = LayerManager.NameOf(i) ?? "";
            if (gui.InputText("##name", ref name, 64))
                LayerManager.SetName(i, name);
            if (gui.IsItemDeactivatedAfterEdit())
                Persist();
            gui.PopId();
        }
    }
}
