using SysVec2 = System.Numerics.Vector2;
using SysVec4 = System.Numerics.Vector4;

namespace BallisticEngine.Editor;

internal sealed class SceneHierarchyWindow : EditorWindow {
    static IEditorGui gui => EditorGui.Shared;

    readonly EditorState state;

    public SceneHierarchyWindow(EditorState state) {
        DockKey = EditorLayout.SceneComponents;
        Title = "Scene Components";
        Icon = EditorIcons.World;
        Singleton = false;
        this.state = state;
    }

    protected override void OnGui(IEditorGui gui) => DrawSceneBehaviours();

    public void DrawSceneContents() => DrawSceneBehaviours();

    void DrawSceneBehaviours() {
        Scene scene = SceneManager.GetCurrentScene();

        if (gui.Button($"{EditorIcons.Add}  Add Scene Component", new SysVec2(-1, 0)))
            gui.OpenPopup("##addscenebehaviour");

        if (gui.BeginPopup("##addscenebehaviour")) {
            foreach (ComponentEntry entry in ComponentRegistry.SceneMenu) {
                (string entryIcon, _) = EditorIcons.ForComponentType(entry.Type);
                if (gui.MenuItem($"{entryIcon}  {entry.DisplayName}")) {
                    EditorCommands.EditScene($"Add {entry.DisplayName}",
                        () => state.SelectSceneBehaviour(scene.AddSceneBehaviour(entry.Type)));
                }
            }
            gui.EndPopup();
        }

        gui.Separator();

        var behaviours = scene.SceneBehaviours.ToArray();
        if (behaviours.Length == 0) {
            gui.Spacing();
            gui.TextDisabled("No scene components.");
            gui.TextDisabled("Add a Skybox to get started.");
        }

        foreach (SceneBehaviour behaviour in behaviours) {
            bool selected = ReferenceEquals(behaviour, state.SelectedSceneBehaviour);
            (string icon, SysVec4 tint) = EditorIcons.ForComponentType(behaviour.GetType());

            if (!behaviour.IsEnabled)
                gui.PushColor(EditorStyleColor.Text, gui.StyleColor(EditorStyleColor.TextDisabled));

            if (gui.Selectable($"      {behaviour.GetType().Name}##{behaviour.InstanceId}", selected))
                state.SelectSceneBehaviour(behaviour);

            if (!behaviour.IsEnabled)
                gui.PopColor();

            HierarchyPanel.DrawRowIcon(gui.ItemRectMin + new SysVec2(4, 0), icon, tint, behaviour.IsEnabled);
            if (selected)
                HierarchyPanel.DrawSelectionBar(gui.ItemRectMin, gui.ItemRectMax);

            if (gui.BeginPopupContextItem($"##sbctx{behaviour.InstanceId}")) {
                if (gui.MenuItem("Remove")) {
                    EditorCommands.EditScene($"Remove {behaviour.GetType().Name}", () => {
                        scene.RemoveSceneBehaviour(behaviour);
                        if (ReferenceEquals(state.SelectedSceneBehaviour, behaviour))
                            state.SelectSceneBehaviour(null);
                    });
                }
                gui.EndPopup();
            }
        }
    }
}
