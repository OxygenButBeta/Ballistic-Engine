using System.Text;
using System.Text.RegularExpressions;
using Hexa.NET.ImGui;
using SysVec2 = System.Numerics.Vector2;
using SysVec4 = System.Numerics.Vector4;

namespace BallisticEngine.Editor;

// Debug console: every engine log (info/warning/error) with severity filter chips (icon + live
// count), a per-row severity icon, a per-message timestamp, free-text search, optional collapsing of
// consecutive duplicates (with a count badge), copy-to-clipboard, and double-click-to-open-source for
// rows that carry an "Assets/...cs(line,col)" reference (script errors).
//
// Framework pilot (Phase 1): first panel ported to the EditorWindow base. Identity (dock key/title/icon)
// lives in the base; OnGui routes to DrawContents. The body still calls ImGui directly — that is allowed
// (the architecture rule is only that the PLAYER never sees ImGui; inside the editor it's free). The
// registry still drives DrawContents today; Phase 3 switches it to drive the window through WindowShell.
internal sealed class ConsolePanel : EditorWindow {
    protected override void OnGui(IEditorGui gui) => DrawContents();

    readonly record struct Entry(string Message, int Level, string Time);

    readonly List<Entry> entries = new();
    readonly object gate = new();

    bool showInfo = true, showWarnings = true, showErrors = true;
    bool autoScroll = true;
    bool collapse = true;
    string search = "";

    // Severity tints come from the central theme (EditorTheme.LogLevel: info/warning/error). EF5b — the
    // per-level array used to be hand-typed here; routed through the theme so the console reads with the
    // same status palette as the rest of the editor.
    static SysVec4[] LevelColors => EditorTheme.LogLevel;

    static readonly string[] LevelIcons = [EditorIcons.Info, EditorIcons.Warning, EditorIcons.Error];

    // Matches a "Assets/Foo/Bar.cs(12,5)" or "Assets/Foo/Bar.cs:12" source reference in a message.
    static readonly Regex SourceRef =
        new(@"(Assets[\\/][^\s:()]+\.cs)[(:](\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public ConsolePanel() {
        DockKey = EditorLayout.Console;
        Title = "Console";
        Icon = EditorIcons.Document;
        Singleton = false;        // duplicable via the Add-Tab host

        Debugging.OnMessage += (message, level) => {
            // Timestamp at log time (HH:mm:ss). DateTime.Now is fine here — this is wall-clock display,
            // not gameplay timing.
            string time = DateTime.Now.ToString("HH:mm:ss");
            lock (gate) {
                entries.Add(new Entry(message, level, time));
                if (entries.Count > 600)
                    entries.RemoveRange(0, 100);
            }
        };
    }

    public void DrawContents() {
        int infoCount, warnCount, errorCount;
        Entry[] snapshot;
        lock (gate) {
            infoCount = entries.Count(e => e.Level == 0);
            warnCount = entries.Count(e => e.Level == 1);
            errorCount = entries.Count(e => e.Level == 2);
            snapshot = entries.ToArray();
        }

        // ---- Toolbar row 1: clear / copy / filter chips / auto-scroll ----
        if (EditorIcons.GhostButton("clearlog", EditorIcons.Delete, "Clear the log"))
            lock (gate) entries.Clear();
        ImGui.SameLine(0, 2);
        if (EditorIcons.GhostButton("copylog", EditorIcons.Document, "Copy visible log to clipboard"))
            CopyToClipboard(snapshot);

        ImGui.SameLine(0, 10);
        FilterChip("Info", infoCount, 0, ref showInfo);
        ImGui.SameLine(0, 4);
        FilterChip("Warnings", warnCount, 1, ref showWarnings);
        ImGui.SameLine(0, 4);
        FilterChip("Errors", errorCount, 2, ref showErrors);

        ImGui.SameLine(0, 16);
        ImGui.Checkbox("Collapse", ref collapse);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Collapse consecutive identical messages into one row + count.");
        ImGui.SameLine(0, 10);
        ImGui.Checkbox("Auto-scroll", ref autoScroll);

        // ---- Toolbar row 2: search ----
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##consolesearch", $"{EditorIcons.Search} Filter log...", ref search, 128);
        EditorDecoration.DrawDivider();

        ImGui.BeginChild("##log", default, ImGuiChildFlags.None, ImGuiWindowFlags.HorizontalScrollbar);

        var anyVisible = false;
        for (var i = 0; i < snapshot.Length; i++) {
            Entry e = snapshot[i];
            if (!(e.Level switch { 0 => showInfo, 1 => showWarnings, _ => showErrors }))
                continue;
            if (search.Length > 0 && !e.Message.Contains(search, StringComparison.OrdinalIgnoreCase))
                continue;

            // Collapse: if this row equals the next visible ones, count them and skip ahead.
            int dupCount = 1;
            if (collapse) {
                while (i + 1 < snapshot.Length &&
                       snapshot[i + 1].Level == e.Level && snapshot[i + 1].Message == e.Message) {
                    dupCount++;
                    i++;
                }
            }

            anyVisible = true;
            DrawRow(e, dupCount, i);
        }

        if (!anyVisible)
            ImGui.TextDisabled(snapshot.Length == 0 ? "No log messages yet."
                : search.Length > 0 ? "No messages match the filter." : "All messages filtered out.");

        if (autoScroll && ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 4)
            ImGui.SetScrollHereY(1f);

        ImGui.EndChild();
    }

    void DrawRow(Entry e, int dupCount, int id) {
        ImGui.PushID(id);

        // Timestamp (dim, fixed-ish width via the monospace-y digits of the UI font).
        ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        ImGui.TextUnformatted(e.Time);
        ImGui.PopStyleColor();
        ImGui.SameLine(0, 8);

        // Severity icon.
        ImGui.PushStyleColor(ImGuiCol.Text, LevelColors[e.Level]);
        ImGui.TextUnformatted(LevelIcons[e.Level]);
        ImGui.PopStyleColor();
        ImGui.SameLine(0, 8);

        // Message (errors/warnings tinted). Selectable so a double-click can open the source ref.
        if (e.Level > 0)
            ImGui.PushStyleColor(ImGuiCol.Text, LevelColors[e.Level]);
        ImGui.Selectable(e.Message, false, ImGuiSelectableFlags.AllowDoubleClick);
        if (e.Level > 0)
            ImGui.PopStyleColor();

        Match m = SourceRef.Match(e.Message);
        if (m.Success && ImGui.IsItemHovered()) {
            ImGui.SetTooltip($"Double-click to open {m.Groups[1].Value}:{m.Groups[2].Value}");
            if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                OpenSource(m.Groups[1].Value, int.Parse(m.Groups[2].Value));
        }

        // Duplicate-count badge on the right.
        if (dupCount > 1) {
            string badge = $"x{dupCount}";
            float right = ImGui.GetWindowWidth() - ImGui.CalcTextSize(badge).X - ImGui.GetStyle().ScrollbarSize - 8;
            ImGui.SameLine();
            ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), right));
            ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
            ImGui.TextUnformatted(badge);
            ImGui.PopStyleColor();
        }

        ImGui.PopID();
    }

    static void OpenSource(string assetRelativePath, int line) {
        try {
            string absolute = AssetDatabase.Project is not null
                ? AssetDatabase.Project.ResolveAbsolute(assetRelativePath.Replace('\\', '/'))
                : assetRelativePath;
            // Prefer VS Code's go-to-line if available; fall back to the OS default opener.
            try {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
                    FileName = "code", Arguments = $"-g \"{absolute}:{line}\"", UseShellExecute = true,
                });
            }
            catch {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
                    FileName = absolute, UseShellExecute = true,
                });
            }
        }
        catch (Exception ex) {
            Debugging.LogWarning($"Could not open source: {ex.Message}");
        }
    }

    void CopyToClipboard(Entry[] snapshot) {
        var sb = new StringBuilder();
        foreach (Entry e in snapshot) {
            if (!(e.Level switch { 0 => showInfo, 1 => showWarnings, _ => showErrors }))
                continue;
            if (search.Length > 0 && !e.Message.Contains(search, StringComparison.OrdinalIgnoreCase))
                continue;
            string tag = e.Level switch { 1 => "WARN", 2 => "ERROR", _ => "INFO" };
            sb.Append('[').Append(e.Time).Append("] ").Append(tag).Append(": ").AppendLine(e.Message);
        }
        ImGui.SetClipboardText(sb.ToString());
    }

    // Icon + count toggle chip; filled with the severity tint while enabled.
    static void FilterChip(string label, int count, int level, ref bool enabled) {
        SysVec4 tint = LevelColors[level];
        if (enabled) {
            ImGui.PushStyleColor(ImGuiCol.Button, new SysVec4(tint.X, tint.Y, tint.Z, 0.16f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new SysVec4(tint.X, tint.Y, tint.Z, 0.26f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new SysVec4(tint.X, tint.Y, tint.Z, 0.36f));
            ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.Text]);
        }
        else {
            ImGui.PushStyleColor(ImGuiCol.Button, new SysVec4(0, 0, 0, 0));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new SysVec4(1, 1, 1, 0.06f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new SysVec4(1, 1, 1, 0.10f));
            ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        }

        if (ImGui.Button($"{LevelIcons[level]} {count}##chip{label}"))
            enabled = !enabled;
        ImGui.PopStyleColor(4);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(enabled ? $"Hide {label.ToLowerInvariant()}" : $"Show {label.ToLowerInvariant()}");
    }
}
