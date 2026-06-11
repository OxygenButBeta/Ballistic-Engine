using Hexa.NET.ImGui;
using SysVec2 = System.Numerics.Vector2;
using SysVec4 = System.Numerics.Vector4;

namespace BallisticEngine.Editor;

// Realtime frame profiler window (Window > Profiler): frame-time graph over the recorded
// history plus a per-zone breakdown of one frame, fed by EditorProfilerBackend's ring buffer.
// Pause freezes the history and unlocks scrubbing; Tracy (BALLISTIC_TRACY=1) remains the
// deep-dive tool â€” this is the always-available overview.
internal sealed class ProfilerPanel {
    // BALLISTIC_PROFILER=1 opens the panel at startup â€” lets headless/agent runs screenshot
    // frame timings without clicking through the Window menu.
    public bool Open = Environment.GetEnvironmentVariable("BALLISTIC_PROFILER") == "1";

    readonly float[] graph = new float[EditorProfilerBackend.HistorySize];
    int inspectAge;   // 0 = latest completed frame; scrubbed while paused

    public void Draw(EditorProfilerBackend profiler, float scale) {
        if (!Open)
            return;

        ImGui.SetNextWindowSize(new SysVec2(580 * scale, 500 * scale), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Profiler", ref Open)) {
            ImGui.End();
            return;
        }

        var available = profiler.AvailableFrames;
        if (available == 0) {
            ImGui.TextDisabled("No frames recorded yet.");
            ImGui.End();
            return;
        }

        // Header: pause/resume + headline numbers over the recorded window.
        if (ImGui.Button(profiler.Paused ? "Resume" : "Pause")) {
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
        ImGui.SameLine();
        ImGui.Text($"{latest:0.00} ms ({(latest > 0 ? 1000.0 / latest : 0):0} fps)");
        ImGui.SameLine();
        ImGui.TextDisabled($"avg {avg:0.00} ms   max {max:0.00} ms   ({available} frames)");

        // Frame-time graph, oldest to newest.
        for (var i = 0; i < available; i++)
            graph[i] = (float)profiler.CompletedFrame(available - 1 - i).TotalMs;
        ImGui.PlotLines("##frametimes", ref graph[0], available, 0, (string)null,
            0f, (float)Math.Max(max * 1.25, 1.0),
            new SysVec2(ImGui.GetContentRegionAvail().X, 80 * scale));

        // Scrubbing only makes sense over a frozen history.
        if (profiler.Paused)
            ImGui.SliderInt("Frame (0 = newest)", ref inspectAge, 0, available - 1);
        else
            inspectAge = 0;

        EditorProfilerBackend.Frame frame = profiler.CompletedFrame(Math.Min(inspectAge, available - 1));

        ImGui.Spacing();
        if (ImGui.BeginTable("##zones", 3,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY,
                new SysVec2(0, ImGui.GetContentRegionAvail().Y))) {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("Zone", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Time", ImGuiTableColumnFlags.WidthFixed, 90 * scale);
            ImGui.TableSetupColumn("% Frame", ImGuiTableColumnFlags.WidthFixed, 70 * scale);
            ImGui.TableHeadersRow();

            // Whole-frame row, then the recorded zones in begin order with depth indentation.
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.Text("Frame");
            ImGui.TableNextColumn();
            ImGui.Text($"{frame.TotalMs:0.000} ms");
            ImGui.TableNextColumn();
            ImGui.TextDisabled("100.0");

            for (var i = 0; i < frame.ZoneCount; i++) {
                ref readonly EditorProfilerBackend.ZoneRecord zone = ref frame.Zones[i];
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (zone.Depth + 1) * 14 * scale);
                ImGui.Text(zone.Name);
                ImGui.TableNextColumn();
                ImGui.Text($"{zone.DurationMs:0.000} ms");
                ImGui.TableNextColumn();
                var percent = frame.TotalMs > 0 ? zone.DurationMs / frame.TotalMs * 100.0 : 0;
                if (percent >= 50)
                    ImGui.TextColored(new SysVec4(1f, 0.55f, 0.35f, 1f), $"{percent:0.0}");
                else
                    ImGui.Text($"{percent:0.0}");
            }

            ImGui.EndTable();
        }

        ImGui.End();
    }
}
