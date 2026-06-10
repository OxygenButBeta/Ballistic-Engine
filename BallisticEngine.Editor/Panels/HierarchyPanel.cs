using ImGuiNET;

namespace BallisticEngine.Editor;

// Lists the scene's entities as a flat selectable list (parenting display is a follow-up).
// Create/delete entities; selection drives the Inspector and gizmo.
internal sealed class HierarchyPanel {
    readonly EditorState state;

    public HierarchyPanel(EditorState state) => this.state = state;

    public void Draw() {
        ImGui.SetNextWindowPos(new System.Numerics.Vector2(10, 90), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new System.Numerics.Vector2(200, 500), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Hierarchy")) { ImGui.End(); return; }

        Scene scene = SceneManager.GetCurrentScene();

        if (ImGui.Button("+ Entity"))
            state.Select(scene.CreateEntity("Entity"));
        ImGui.SameLine();
        using (Disabled(state.Selected is null)) {
            if (ImGui.Button("Delete") && state.Selected is not null) {
                scene.DestroyEntity(state.Selected);
                state.Selected = null;
            }
        }

        ImGui.Separator();

        // Snapshot to a list so create/delete during iteration is safe.
        var entities = scene.Entities.ToArray();
        foreach (Entity entity in entities) {
            bool selected = ReferenceEquals(entity, state.Selected);
            if (ImGui.Selectable($"{entity.Name}##{entity.InstanceId}", selected))
                state.Select(entity);
        }

        ImGui.End();
    }

    static DisabledScope Disabled(bool disabled) => new(disabled);

    readonly struct DisabledScope : IDisposable {
        readonly bool active;
        public DisabledScope(bool disabled) {
            active = disabled;
            if (active) ImGui.BeginDisabled();
        }
        public void Dispose() {
            if (active) ImGui.EndDisabled();
        }
    }
}
