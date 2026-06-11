using ImGuiNET;
using SysVec2 = System.Numerics.Vector2;

namespace BallisticEngine.Editor;

// Floating statistics overlay (toggled from the toolbar). Everything here is computed
// editor-side from public engine state — the renderer is not involved, so the numbers are
// estimates (draw calls = visible renderers + sky) until real GPU metrics land in RenderMetrics.
internal sealed class StatsPanel {
    public void Draw(float fps, float editorCpuMs, SysVec2 sceneViewSize, float scale) {
        ImGui.SetNextWindowPos(new SysVec2(ImGui.GetIO().DisplaySize.X * 0.5f, 70 * scale), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowBgAlpha(0.85f);
        if (!ImGui.Begin("Statistics",
                ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings |
                ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoFocusOnAppearing)) {
            ImGui.End();
            return;
        }

        Scene scene = SceneManager.GetCurrentScene();

        var drawCalls = 0;
        long vertices = 0;
        long triangles = 0;
        var totalRenderers = 0;

        foreach (IStaticMeshRenderer renderer in RuntimeSet<IStaticMeshRenderer>.ReadOnlyCollection) {
            totalRenderers++;
            if (!renderer.IsRenderable || !renderer.IsActive)
                continue;
            drawCalls++;
            vertices += renderer.SharedMesh.Vertices.Length;
            triangles += renderer.SharedMesh.Indices.Length / 3;
        }

        var skyActive = Skybox.Active is { IsActive: true, Cubemap: not null };
        if (skyActive)
            drawCalls++;

        Line("FPS", $"{fps:0}");
        Line("Frame", $"{(fps > 0 ? 1000f / fps : 0):0.00} ms");
        Line("Editor CPU", $"{editorCpuMs:0.00} ms");
        ImGui.Separator();
        Line("Draw calls (est)", drawCalls.ToString());
        Line("Vertices", vertices.ToString("N0"));
        Line("Triangles", triangles.ToString("N0"));
        ImGui.Separator();
        Line("Entities", scene.Entities.Count.ToString());
        Line("Scene components", scene.SceneBehaviours.Count.ToString());
        Line("Renderers", $"{drawCalls - (skyActive ? 1 : 0)} visible / {totalRenderers}");
        ImGui.Separator();
        Line("Scene view", $"{(int)sceneViewSize.X} x {(int)sceneViewSize.Y}");
        Line("Managed mem", $"{GC.GetTotalMemory(false) / (1024.0 * 1024.0):0.0} MB");

        ImGui.End();
    }

    static void Line(string label, string value) {
        ImGui.TextDisabled(label);
        ImGui.SameLine(170);
        ImGui.Text(value);
    }
}
