using System.Numerics;

namespace BallisticEngine.Editor;

// Realtime frame profiler window (Window > Profiler): frame-time graph over the recorded
// history plus a per-zone breakdown of one frame, fed by EditorProfilerBackend's ring buffer.
// Pause freezes the history and unlocks scrubbing; Tracy (BALLISTIC_TRACY=1) remains the
// deep-dive tool — this is the always-available overview.
//
// Phase-2 EditorWindow: the body draws through IEditorGui (no raw ImGui). The profiler backend is now a
// FIELD (set once at construction) instead of a per-frame Draw arg — WindowShell's OnGui takes no extra
// context, so window state that the body needs lives on the instance.
internal sealed class ProfilerPanel : EditorWindow {
    readonly EditorProfilerBackend profiler;

    readonly float[] graph = new float[EditorProfilerBackend.HistorySize];
    int inspectAge;   // 0 = latest completed frame; scrubbed while paused

    public ProfilerPanel(EditorProfilerBackend profiler) {
        this.profiler = profiler;
        DockKey = "win.profiler";
        Title = "Profiler";
        Icon = null;
        DesiredSize = new Vector2(580, 500);
        // BALLISTIC_PROFILER=1 opens the panel at startup — lets headless/agent runs screenshot
        // frame timings without clicking through the Window menu.
        Open = Environment.GetEnvironmentVariable("BALLISTIC_PROFILER") == "1";
    }

    protected override void OnGui(IEditorGui gui) {
        float scale = gui.Scale;
        var available = profiler.AvailableFrames;
        if (available == 0) {
            gui.TextDisabled("No frames recorded yet.");
            return;
        }

        // Header: pause/resume + headline numbers over the recorded window.
        if (gui.Button(profiler.Paused ? "Resume" : "Pause")) {
            profiler.Paused = !profiler.Paused;
            inspectAge = 0;
        }

        double avg = 0, max = 0;
        for (var i = 0; i < available; i++) {
            var t = profiler.CompletedFrame(i).TotalMs;
            avg += t;
            if (t > max)
                max = t;
        }
        avg /= available;

        var latest = profiler.CompletedFrame(0).TotalMs;
        gui.SameLine();
        gui.Text($"{latest:0.00} ms ({(latest > 0 ? 1000.0 / latest : 0):0} fps)");
        gui.SameLine();
        gui.TextDisabled($"avg {avg:0.00} ms   max {max:0.00} ms   ({available} frames)");

        // Frame-time graph, oldest to newest.
        for (var i = 0; i < available; i++)
            graph[i] = (float)profiler.CompletedFrame(available - 1 - i).TotalMs;
        gui.PlotLines("##frametimes", graph, available, null,
            0f, (float)Math.Max(max * 1.25, 1.0),
            new Vector2(gui.ContentRegionAvail.X, 80 * scale));

        // Scrubbing only makes sense over a frozen history.
        if (profiler.Paused)
            gui.SliderInt("Frame (0 = newest)", ref inspectAge, 0, available - 1);
        else
            inspectAge = 0;

        EditorProfilerBackend.Frame frame = profiler.CompletedFrame(Math.Min(inspectAge, available - 1));

        gui.Spacing();
        if (gui.BeginTable("##zones", 3,
                EditorTableFlags.RowBg | EditorTableFlags.BordersInnerV | EditorTableFlags.ScrollY,
                new Vector2(0, gui.ContentRegionAvail.Y))) {
            gui.TableSetupScrollFreeze(0, 1);
            gui.TableSetupColumn("Zone", EditorColumnFlags.WidthStretch);
            gui.TableSetupColumn("Time", EditorColumnFlags.WidthFixed, 90 * scale);
            gui.TableSetupColumn("% Frame", EditorColumnFlags.WidthFixed, 70 * scale);
            gui.TableHeadersRow();

            // Whole-frame row, then the recorded zones in begin order with depth indentation.
            gui.TableNextRow();
            gui.TableNextColumn();
            gui.Text("Frame");
            gui.TableNextColumn();
            gui.Text($"{frame.TotalMs:0.000} ms");
            gui.TableNextColumn();
            gui.TextDisabled("100.0");

            for (var i = 0; i < frame.ZoneCount; i++) {
                ref readonly EditorProfilerBackend.ZoneRecord zone = ref frame.Zones[i];
                gui.TableNextRow();
                gui.TableNextColumn();
                gui.CursorPosX += (zone.Depth + 1) * 14 * scale;
                gui.Text(zone.Name);
                gui.TableNextColumn();
                gui.Text($"{zone.DurationMs:0.000} ms");
                gui.TableNextColumn();
                var percent = frame.TotalMs > 0 ? zone.DurationMs / frame.TotalMs * 100.0 : 0;
                if (percent >= 50)
                    gui.TextColored(EditorTheme.Error, $"{percent:0.0}");
                else
                    gui.Text($"{percent:0.0}");
            }

            gui.EndTable();
        }
    }
}
