using Hexa.NET.ImGui;
using SysVec2 = System.Numerics.Vector2;

namespace BallisticEngine.Editor;

internal sealed class StatsPanel {
    public unsafe bool Draw(float fps, float editorCpuMs, SysVec2 viewSize, float scale,
        SysVec2 anchorMin, SysVec2 anchorSize, float topOffset, RenderStats rs, bool showTiming) {
        ImGui.SetNextWindowPos(
            new SysVec2(anchorMin.X + anchorSize.X - 10 * scale, anchorMin.Y + topOffset),
            ImGuiCond.Always, new SysVec2(1, 0));
        float maxH = Math.Max(140 * scale, viewSize.Y - topOffset - 12 * scale);
        ImGui.SetNextWindowSizeConstraints(new SysVec2(0, 0), new SysVec2(float.MaxValue, maxH));
        ImGui.SetNextWindowBgAlpha(0.88f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 8f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new SysVec2(12 * scale, 9 * scale));
        ImGui.PushStyleColor(ImGuiCol.Border, EditorTheme.Hairline);
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

        ImGui.GetWindowDrawList().AddText(ImGuiController.Bold, ImGui.GetFontSize(),
            ImGui.GetCursorScreenPos(), ImGui.GetColorU32(ImGuiCol.Text), "Statistics");
        ImGui.Dummy(new SysVec2(80 * scale, ImGui.GetTextLineHeight()));
        ImGui.SameLine();
        ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), 203 * scale));
        ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        if (EditorIcons.GhostButtonSmall("closestats", EditorIcons.Cancel, "Close"))
            open = false;
        ImGui.PopStyleColor();

        float bodyH = maxH - 38 * scale;
        ImGui.BeginChild("##statsbody", new SysVec2(300 * scale, bodyH), ImGuiChildFlags.None,
            ImGuiWindowFlags.NoBackground);

        if (showTiming) {
            EditorDecoration.DrawSectionHeader("Timing");
            Line("FPS", $"{fps:0}", scale);
            Line("Frame", $"{(fps > 0 ? 1000f / fps : 0):0.00} ms", scale);
            Line("Editor CPU", $"{editorCpuMs:0.00} ms", scale);
        }
        EditorDecoration.DrawSectionHeader("Rendering");
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
            EditorDecoration.DrawSectionHeader("GPU");
            Line("GPU frame", $"{rs.GpuFrameMs:0.00} ms", scale);
            foreach ((string name, double ms) in rs.GpuPasses)
                if (ms >= 0.005)
                    Line(name, $"{ms:0.00} ms", scale);
        }

        EditorDecoration.DrawSectionHeader("Scene");
        Line("Entities", scene.Entities.Count.ToString(), scale);
        Line("Scene components", scene.SceneBehaviours.Count.ToString(), scale);
        Line("Managed mem", $"{GC.GetTotalMemory(false) / (1024.0 * 1024.0):0.0} MB", scale);

        ImGui.EndChild();
        ImGui.End();
        ImGui.PopStyleColor();
        ImGui.PopStyleVar(2);
        return open;
    }

    static void Line(string label, string value, float scale) {
        ImGui.TextDisabled(label);
        ImGui.SameLine(140 * scale);
        float w = ImGui.CalcTextSize(value).X;
        ImGui.SetCursorPosX(Math.Max(140 * scale, 225 * scale - w));
        ImGui.Text(value);
    }
}
