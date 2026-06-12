using Hexa.NET.ImGui;
using SysVec2 = System.Numerics.Vector2;
using SysVec4 = System.Numerics.Vector4;

namespace BallisticEngine.Editor;

// Statistics overlay pinned to the active view's top-right corner (toggled from the toolbar),
// drawn over both the Scene and Game views. Submission counters and per-pass GPU times come
// straight from the renderer via RenderStats (timestamp queries, a few frames of latency).
internal sealed class StatsPanel {
    // anchorMin/anchorSize = the view image's screen rect; topOffset leaves room for anything
    // already living in that corner (the Scene view's orientation cube).
    // Returns false when the user clicked the overlay's close button (the caller untoggles).
    public unsafe bool Draw(float fps, float editorCpuMs, SysVec2 viewSize, float scale,
        SysVec2 anchorMin, SysVec2 anchorSize, float topOffset, RenderStats rs) {
        ImGui.SetNextWindowPos(
            new SysVec2(anchorMin.X + anchorSize.X - 10 * scale, anchorMin.Y + topOffset),
            ImGuiCond.Always, new SysVec2(1, 0));   // pivot top-right: grows leftward/downward
        ImGui.SetNextWindowBgAlpha(0.88f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 8f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new SysVec2(12 * scale, 9 * scale));
        ImGui.PushStyleColor(ImGuiCol.Border, new SysVec4(1, 1, 1, 0.07f));
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.AlwaysAutoResize |
            ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav;

        if (!ImGui.Begin("##statsoverlay", flags)) {
            ImGui.End();
            ImGui.PopStyleColor();
            ImGui.PopStyleVar(2);
            return true;
        }

        var open = true;

        Scene scene = SceneManager.GetCurrentScene();

        var totalRenderers = 0;
        foreach (IStaticMeshRenderer _ in RuntimeSet<IStaticMeshRenderer>.ReadOnlyCollection)
            totalRenderers++;

        // Title row: bold "Statistics" + a close button at the right edge (Unity-style).
        ImGui.GetWindowDrawList().AddText(ImGuiController.Bold, ImGui.GetFontSize(),
            ImGui.GetCursorScreenPos(), ImGui.GetColorU32(ImGuiCol.Text), "Statistics");
        ImGui.Dummy(new SysVec2(80 * scale, ImGui.GetTextLineHeight()));
        ImGui.SameLine();
        ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), 203 * scale));
        ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        if (EditorIcons.GhostButtonSmall("closestats", EditorIcons.Cancel, "Close"))
            open = false;
        ImGui.PopStyleColor();

        ImGui.SeparatorText("Timing");
        Line("FPS", $"{fps:0}", scale);
        Line("Frame", $"{(fps > 0 ? 1000f / fps : 0):0.00} ms", scale);
        Line("Editor CPU", $"{editorCpuMs:0.00} ms", scale);
        ImGui.SeparatorText("Rendering");
        Line("Draw calls", rs.DrawCalls.ToString(), scale);
        Line("Depth draws", rs.DepthOnlyDrawCalls.ToString(), scale);
        if (rs.DrawsSavedByInstancing > 0)
            Line("Instanced away", rs.DrawsSavedByInstancing.ToString(), scale);
        Line("Triangles", rs.Triangles.ToString("N0"), scale);
        Line("Renderers", $"{rs.RenderersVisible} drawn / {rs.RenderersCulled} culled / {totalRenderers}", scale);
        if (rs.SubMeshesCulled > 0)
            Line("Submeshes culled", rs.SubMeshesCulled.ToString(), scale);
        Line("View", $"{(int)viewSize.X} x {(int)viewSize.Y}", scale);
        if (rs.GpuPasses.Count > 0) {
            ImGui.SeparatorText("GPU");
            Line("GPU frame", $"{rs.GpuFrameMs:0.00} ms", scale);
            foreach ((string name, double ms) in rs.GpuPasses)
                if (ms >= 0.005)
                    Line(name, $"{ms:0.00} ms", scale);
        }

        ImGui.SeparatorText("Scene");
        Line("Entities", scene.Entities.Count.ToString(), scale);
        Line("Scene components", scene.SceneBehaviours.Count.ToString(), scale);
        Line("Managed mem", $"{GC.GetTotalMemory(false) / (1024.0 * 1024.0):0.0} MB", scale);

        ImGui.End();
        ImGui.PopStyleColor();
        ImGui.PopStyleVar(2);
        return open;
    }

    // Right-aligned values in a fixed column read like a proper profiler readout.
    static void Line(string label, string value, float scale) {
        ImGui.TextDisabled(label);
        ImGui.SameLine(140 * scale);
        float w = ImGui.CalcTextSize(value).X;
        ImGui.SetCursorPosX(Math.Max(140 * scale, 225 * scale - w));
        ImGui.Text(value);
    }
}
