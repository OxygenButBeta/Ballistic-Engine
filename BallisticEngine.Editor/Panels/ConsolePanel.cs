using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;

namespace BallisticEngine.Editor;

// Debug console: every engine log (info/warning/error) with severity filter chips (icon + live
// count), a per-row severity icon, a per-message timestamp, free-text search, optional collapsing of
// consecutive duplicates (with a count badge), copy-to-clipboard, and double-click-to-open-source for
// rows that carry an "Assets/...cs(line,col)" reference (script errors).
//
// Phase-7 EditorWindow: the body now draws entirely through IEditorGui (zero raw ImGui). Per-row severity
// tinting + the filter chips route through the seam's style scope (gui.PushColor/PopColor); the icon-button
// (GhostButton) and divider stay EditorIcons/EditorDecoration helper calls (seam-adjacent widgets).
internal sealed class ConsolePanel : EditorWindow {
    protected override void OnGui(IEditorGui gui) => DrawContents(gui);

    readonly record struct Entry(string Message, int Level, string Time);

    readonly List<Entry> entries = new();
    readonly object gate = new();

    bool showInfo = true, showWarnings = true, showErrors = true;
    bool autoScroll = true;
    bool collapse = true;
    string search = "";

    // Severity tints come from the central theme (EditorTheme.LogLevel: info/warning/error).
    static Vector4[] LevelColors => EditorTheme.LogLevel;

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
            // Timestamp at log time (HH:mm:ss). DateTime.Now is fine here — this is wall-clock display.
            string time = DateTime.Now.ToString("HH:mm:ss");
            lock (gate) {
                entries.Add(new Entry(message, level, time));
                if (entries.Count > 600)
                    entries.RemoveRange(0, 100);
            }
        };
    }

    public void DrawContents(IEditorGui gui) {
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
        gui.SameLine(0, 2);
        if (EditorIcons.GhostButton("copylog", EditorIcons.Document, "Copy visible log to clipboard"))
            CopyToClipboard(gui, snapshot);

        gui.SameLine(0, 10);
        FilterChip(gui, "Info", infoCount, 0, ref showInfo);
        gui.SameLine(0, 4);
        FilterChip(gui, "Warnings", warnCount, 1, ref showWarnings);
        gui.SameLine(0, 4);
        FilterChip(gui, "Errors", errorCount, 2, ref showErrors);

        gui.SameLine(0, 16);
        gui.Checkbox("Collapse", ref collapse);
        if (gui.IsItemHovered()) gui.Tooltip("Collapse consecutive identical messages into one row + count.");
        gui.SameLine(0, 10);
        gui.Checkbox("Auto-scroll", ref autoScroll);

        // ---- Toolbar row 2: search ----
        gui.SetNextItemWidth(-1);
        gui.InputTextWithHint("##consolesearch", $"{EditorIcons.Search} Filter log...", ref search, 128);
        EditorDecoration.DrawDivider();

        gui.BeginChild("##log", default, border: false, horizontalScroll: true);

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
            DrawRow(gui, e, dupCount, i);
        }

        if (!anyVisible)
            gui.TextDisabled(snapshot.Length == 0 ? "No log messages yet."
                : search.Length > 0 ? "No messages match the filter." : "All messages filtered out.");

        if (autoScroll && gui.ScrollY >= gui.ScrollMaxY - 4)
            gui.SetScrollHereY(1f);

        gui.EndChild();
    }

    void DrawRow(IEditorGui gui, Entry e, int dupCount, int id) {
        gui.PushId(id);

        // Timestamp (dim).
        gui.PushColor(EditorStyleColor.Text, gui.StyleColor(EditorStyleColor.TextDisabled));
        gui.TextUnformatted(e.Time);
        gui.PopColor();
        gui.SameLine(0, 8);

        // Severity icon.
        gui.PushColor(EditorStyleColor.Text, LevelColors[e.Level]);
        gui.TextUnformatted(LevelIcons[e.Level]);
        gui.PopColor();
        gui.SameLine(0, 8);

        // Message (errors/warnings tinted). Selectable so a double-click can open the source ref.
        if (e.Level > 0)
            gui.PushColor(EditorStyleColor.Text, LevelColors[e.Level]);
        gui.Selectable(e.Message, false);
        if (e.Level > 0)
            gui.PopColor();

        Match m = SourceRef.Match(e.Message);
        if (m.Success && gui.IsItemHovered()) {
            gui.Tooltip($"Double-click to open {m.Groups[1].Value}:{m.Groups[2].Value}");
            if (gui.Input.MouseDoubleClicked(0))
                OpenSource(m.Groups[1].Value, int.Parse(m.Groups[2].Value));
        }

        // Duplicate-count badge on the right.
        if (dupCount > 1) {
            string badge = $"x{dupCount}";
            float right = gui.WindowWidth - gui.CalcTextSize(badge).X - gui.ScrollbarSize - 8;
            gui.SameLine();
            gui.CursorPosX = Math.Max(gui.CursorPosX, right);
            gui.PushColor(EditorStyleColor.Text, gui.StyleColor(EditorStyleColor.TextDisabled));
            gui.TextUnformatted(badge);
            gui.PopColor();
        }

        gui.PopId();
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

    void CopyToClipboard(IEditorGui gui, Entry[] snapshot) {
        var sb = new StringBuilder();
        foreach (Entry e in snapshot) {
            if (!(e.Level switch { 0 => showInfo, 1 => showWarnings, _ => showErrors }))
                continue;
            if (search.Length > 0 && !e.Message.Contains(search, StringComparison.OrdinalIgnoreCase))
                continue;
            string tag = e.Level switch { 1 => "WARN", 2 => "ERROR", _ => "INFO" };
            sb.Append('[').Append(e.Time).Append("] ").Append(tag).Append(": ").AppendLine(e.Message);
        }
        gui.SetClipboardText(sb.ToString());
    }

    // Icon + count toggle chip; filled with the severity tint while enabled.
    static void FilterChip(IEditorGui gui, string label, int count, int level, ref bool enabled) {
        Vector4 tint = LevelColors[level];
        if (enabled) {
            gui.PushColor(EditorStyleColor.Button, new Vector4(tint.X, tint.Y, tint.Z, 0.16f));
            gui.PushColor(EditorStyleColor.ButtonHovered, new Vector4(tint.X, tint.Y, tint.Z, 0.26f));
            gui.PushColor(EditorStyleColor.ButtonActive, new Vector4(tint.X, tint.Y, tint.Z, 0.36f));
            gui.PushColor(EditorStyleColor.Text, gui.StyleColor(EditorStyleColor.Text));
        }
        else {
            gui.PushColor(EditorStyleColor.Button, new Vector4(0, 0, 0, 0));
            gui.PushColor(EditorStyleColor.ButtonHovered, new Vector4(1, 1, 1, 0.06f));
            gui.PushColor(EditorStyleColor.ButtonActive, new Vector4(1, 1, 1, 0.10f));
            gui.PushColor(EditorStyleColor.Text, gui.StyleColor(EditorStyleColor.TextDisabled));
        }

        if (gui.Button($"{LevelIcons[level]} {count}##chip{label}"))
            enabled = !enabled;
        gui.PopColor(4);
        if (gui.IsItemHovered())
            gui.Tooltip(enabled ? $"Hide {label.ToLowerInvariant()}" : $"Show {label.ToLowerInvariant()}");
    }
}
