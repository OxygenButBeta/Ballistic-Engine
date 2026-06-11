using ImGuiNET;

namespace BallisticEngine.Editor;

// Two hierarchies as tabs: "Entities" (the scene's entity list) and "Scene" (scene-wide
// SceneBehaviours: skybox, fog, ...). Selection from either drives the Inspector.
internal sealed class HierarchyPanel {
    readonly EditorState state;

    public HierarchyPanel(EditorState state) => this.state = state;

    public void DrawContents() {
        if (!ImGui.BeginTabBar("##hierarchytabs"))
            return;

        if (ImGui.BeginTabItem("Entities")) {
            DrawEntities();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Scene")) {
            DrawSceneBehaviours();
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    void DrawEntities() {
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

    void DrawSceneBehaviours() {
        Scene scene = SceneManager.GetCurrentScene();

        if (ImGui.Button("+ Add", new System.Numerics.Vector2(-1, 0)))
            ImGui.OpenPopup("##addscenebehaviour");

        if (ImGui.BeginPopup("##addscenebehaviour")) {
            foreach (ComponentEntry entry in ComponentRegistry.SceneMenu) {
                if (ImGui.MenuItem(entry.DisplayName)) {
                    EditorUndo.Push();
                    state.SelectSceneBehaviour(scene.AddSceneBehaviour(entry.Type));
                }
            }
            ImGui.EndPopup();
        }

        ImGui.Separator();

        var behaviours = scene.SceneBehaviours.ToArray();
        if (behaviours.Length == 0)
            ImGui.TextDisabled("No scene components.\nAdd a Skybox to get started.");

        foreach (SceneBehaviour behaviour in behaviours) {
            bool selected = ReferenceEquals(behaviour, state.SelectedSceneBehaviour);

            if (!behaviour.IsEnabled)
                ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);

            if (ImGui.Selectable($"{behaviour.GetType().Name}##{behaviour.InstanceId}", selected))
                state.SelectSceneBehaviour(behaviour);

            if (!behaviour.IsEnabled)
                ImGui.PopStyleColor();

            if (ImGui.BeginPopupContextItem($"##sbctx{behaviour.InstanceId}")) {
                if (ImGui.MenuItem("Remove")) {
                    EditorUndo.Push();
                    scene.RemoveSceneBehaviour(behaviour);
                    if (ReferenceEquals(state.SelectedSceneBehaviour, behaviour))
                        state.SelectSceneBehaviour(null);
                }
                ImGui.EndPopup();
            }
        }
    }
}
