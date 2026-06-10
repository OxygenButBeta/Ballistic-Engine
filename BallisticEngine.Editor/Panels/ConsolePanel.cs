using ImGuiNET;
using SysVec4 = System.Numerics.Vector4;

namespace BallisticEngine.Editor;

// Debug console: shows every engine log (info/warning/error) with filters and auto-scroll.
internal sealed class ConsolePanel {
    readonly List<(string message, int level)> entries = new();
    readonly object gate = new();

    bool showInfo = true, showWarnings = true, showErrors = true;
    bool autoScroll = true;

    static readonly SysVec4[] LevelColors = [
        new(0.82f, 0.82f, 0.82f, 1f), // info
        new(0.95f, 0.80f, 0.30f, 1f), // warning
        new(0.95f, 0.38f, 0.32f, 1f), // error
    ];

    public ConsolePanel() {
        Debugging.OnMessage += (message, level) => {
            lock (gate) {
                entries.Add((message, level));
                if (entries.Count > 600)
                    entries.RemoveRange(0, 100);
            }
        };
    }

    public void DrawContents() {
        int infoCount, warnCount, errorCount;
        lock (gate) {
            infoCount = entries.Count(e => e.level == 0);
            warnCount = entries.Count(e => e.level == 1);
            errorCount = entries.Count(e => e.level == 2);
        }

        if (ImGui.Button("Clear")) {
            lock (gate) entries.Clear();
        }
        ImGui.SameLine();
        ImGui.Checkbox($"Info ({infoCount})", ref showInfo);
        ImGui.SameLine();
        ImGui.Checkbox($"Warnings ({warnCount})", ref showWarnings);
        ImGui.SameLine();
        ImGui.Checkbox($"Errors ({errorCount})", ref showErrors);
        ImGui.SameLine();
        ImGui.Checkbox("Auto-scroll", ref autoScroll);
        ImGui.Separator();

        ImGui.BeginChild("##log", default, ImGuiChildFlags.None, ImGuiWindowFlags.HorizontalScrollbar);

        (string message, int level)[] snapshot;
        lock (gate) snapshot = entries.ToArray();

        foreach ((string message, int level) in snapshot) {
            var visible = level switch { 0 => showInfo, 1 => showWarnings, _ => showErrors };
            if (!visible)
                continue;
            ImGui.PushStyleColor(ImGuiCol.Text, LevelColors[level]);
            ImGui.TextUnformatted(message);
            ImGui.PopStyleColor();
        }

        if (autoScroll && ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 4)
            ImGui.SetScrollHereY(1f);

        ImGui.EndChild();
    }
}
