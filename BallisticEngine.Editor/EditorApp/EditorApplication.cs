using ImGuiNET;
using OpenTK.Graphics.OpenGL4;

namespace BallisticEngine.Editor;

// Drives the editor: owns the ImGui controller and builds the UI each frame on top of the
// running engine. The engine is brought up via EngineBootstrap; the editor decides when to
// render the scene and when to play. (Panels arrive in Phase 8; for now a status window.)
internal sealed class EditorApplication {
    readonly IBallisticEngineRuntime runtime;
    readonly EngineBootstrap bootstrap;
    readonly ImGuiController imgui;

    public EditorApplication(GLBallisticEngineWindow window, string projectPath) {
        runtime = window;
        bootstrap = new EngineBootstrap(window, projectPath);
        imgui = new ImGuiController(window);

        bootstrap.LoadStartupScene();

        window.OnResizeCallback += (w, h) => imgui.WindowResized(w, h);
        imgui.WindowResized(window.Width, window.Height);

        runtime.Window.SetFrequency(0); // uncapped editor frame rate
        runtime.WindowUpdateCallback += OnUpdate;
        runtime.WindowRenderCallback += OnRender;
    }

    void OnUpdate(double delta) {
        bootstrap.UpdateFrame(delta); // ticks clock + scene (scene update no-ops unless playing)
    }

    void OnRender(double delta) {
        // Clear the backbuffer (engine viewport redirection lands in Phase 6).
        GL.ClearColor(0.10f, 0.10f, 0.12f, 1f);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        imgui.Update((float)delta);
        BuildUI();
        imgui.Render();
    }

    void BuildUI() {
        ImGui.SetNextWindowSize(new System.Numerics.Vector2(420, 260), ImGuiCond.FirstUseEver);
        if (ImGui.Begin("Ballistic Editor")) {
            ImGui.Text($"Project: {bootstrap.Project.Manifest.Name}");
            ImGui.Text($"Scene: {SceneManager.GetCurrentScene().Name}");
            ImGui.Text($"Entities: {SceneManager.GetCurrentScene().Entities.Count}");
            ImGui.Separator();

            if (SceneManager.IsPlaying) {
                if (ImGui.Button("Stop")) SceneManager.StopPlay();
            }
            else {
                if (ImGui.Button("Play")) SceneManager.StartPlay();
            }

            ImGui.SameLine();
            ImGui.Text(SceneManager.IsPlaying ? "(playing)" : "(edit mode)");
            ImGui.Separator();
            ImGui.TextDisabled("Hierarchy / Inspector / Viewport land in later phases.");
        }
        ImGui.End();
    }
}
