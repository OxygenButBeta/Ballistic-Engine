using ImGuiNET;

namespace BallisticEngine.Editor;

// Lists the scene's entities; selection drives the Inspector. Contents only — the
// tiled layout in EditorApplication owns the window.
internal sealed class HierarchyPanel {
    readonly EditorState state;

    public HierarchyPanel(EditorState state) => this.state = state;

    public void DrawContents() {
        Scene scene = SceneManager.GetCurrentScene();

        if (ImGui.Button("+ Entity")) {
            EditorUndo.Push();
            state.Select(scene.CreateEntity("Entity"));
        }
        ImGui.SameLine();
        ImGui.BeginDisabled(state.Selected is null);
        if (ImGui.Button("Delete")) {
            EditorUndo.Push();
            scene.DestroyEntity(state.Selected);
            state.Selected = null;
        }
        ImGui.EndDisabled();

        ImGui.Separator();

        // Snapshot so create/delete during iteration is safe.
        var entities = scene.Entities.ToArray();
        foreach (Entity entity in entities) {
            bool selected = ReferenceEquals(entity, state.Selected);

            if (!entity.IsActive)
                ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);

            if (ImGui.Selectable($"{entity.Name}##{entity.InstanceId}", selected))
                state.Select(entity);

            if (!entity.IsActive)
                ImGui.PopStyleColor();
        }
    }
}
