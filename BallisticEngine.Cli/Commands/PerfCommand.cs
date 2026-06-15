using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using BallisticEngine.Serialization;

namespace BallisticEngine.Cli.Commands;

// `bal perf <scene>` — structured render-perf query (the agent's autonomous-perf-work surface). Renders one
// deterministic frame headlessly and emits RenderStats as JSON: draw calls, triangles, culled submeshes,
// punctual/shadowed lights, CPU frame ms (+ per-pass GPU ms once DX12 timestamp queries land). Device-free
// CLI — same subprocess pattern as `bal render` / `bal query`.
internal sealed class PerfCommand : ICommand {
    public string Name => "perf";
    public string Summary => "Render-perf stats for a scene (draws/tris/cull/lights/CPU ms) as JSON.";
    public string Usage =>
        """
        Usage: bal perf <scene.scene> [--frame F]
          --frame   capture frame (default 60; lets exposure/streaming settle)
        Emits { drawCalls, triangles, subMeshesCulled, punctualLights, shadowedLights, cpuFrameMs, ... }.
        """;

    public int Run(string[] args) {
        string? scenePath = null;
        int frame = 60;
        for (int i = 0; i < args.Length; i++) {
            switch (args[i]) {
                case "--frame": frame = ParseInt(Next(args, ref i, "--frame"), "--frame"); break;
                default:
                    if (scenePath is null) scenePath = args[i];
                    else throw new CliUsageException($"unexpected argument '{args[i]}'");
                    break;
            }
        }
        if (scenePath is null) throw new CliUsageException("expected a scene path");
        string sceneAbs = Path.GetFullPath(scenePath);
        if (!File.Exists(sceneAbs)) throw new Exception($"scene file not found: '{scenePath}'");
        string root = SceneFile.ResolveProjectRoot(sceneAbs);
        string sceneRel = Path.GetRelativePath(root, sceneAbs).Replace('\\', '/');

        string tempDir = Path.Combine(root, "Library", "Temp");
        Directory.CreateDirectory(tempDir);
        string statsOut = Path.Combine(tempDir, "bal-perf.json");
        string bmp = Path.Combine(tempDir, "bal-perf.bmp");

        try {
            RunPlayer(FindPlayerExe(), root, sceneRel, bmp, statsOut, frame);
            if (!File.Exists(statsOut))
                throw new Exception("player exited but wrote no perf stats");
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(statsOut));
            Json.WriteRaw(doc.RootElement);
            return 0;
        } finally {
            try { File.Delete(statsOut); } catch { }
            try { File.Delete(bmp); } catch { }
        }
    }

    static void RunPlayer(string playerExe, string projectRoot, string sceneRel, string bmp, string statsOut, int frame) {
        var psi = new ProcessStartInfo {
            FileName = playerExe, WorkingDirectory = Path.GetDirectoryName(playerExe)!,
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        psi.ArgumentList.Add(projectRoot);
        psi.Environment["BALLISTIC_BACKEND"] = "dx12";
        psi.Environment["BALLISTIC_SCENE"] = sceneRel;
        psi.Environment["BALLISTIC_SCREENSHOT"] = bmp;          // forces the headless host + a rendered frame
        psi.Environment["BALLISTIC_SCREENSHOT_FRAME"] = frame.ToString(CultureInfo.InvariantCulture);
        psi.Environment["BALLISTIC_SCREENSHOT_PAUSED"] = "1";
        psi.Environment["BALLISTIC_STATS_OUT"] = statsOut;

        Console.Error.WriteLine($"  profiling {sceneRel}...");
        using Process process = Process.Start(psi)!;
        string stderr = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(300_000)) {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new Exception("player timed out profiling the scene");
        }
        if (process.ExitCode != 0 && !File.Exists(statsOut))
            throw new Exception($"player exited {process.ExitCode}"
                + (stderr.Length > 0 ? $" — {stderr[..Math.Min(stderr.Length, 400)]}" : ""));
    }

    static string FindPlayerExe() {
        string? engineRoot = Environment.GetEnvironmentVariable("BALLISTIC_ENGINE_ROOT");
        if (engineRoot is null) {
            DirectoryInfo? dir = new(AppContext.BaseDirectory);
            for (int i = 0; dir is not null && i < 8; i++, dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "BallisticEngine.slnx"))) { engineRoot = dir.FullName; break; }
        }
        if (engineRoot is null) throw new Exception("can't locate the engine repo (set BALLISTIC_ENGINE_ROOT)");
        string config = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}")
            ? "Release" : "Debug";
        foreach (string c in new[] { config, config == "Debug" ? "Release" : "Debug" }) {
            string exe = Path.Combine(engineRoot, "BallisticEngine.Runtime", "bin", c, "net9.0", "BallisticEngine.Runtime.exe");
            if (File.Exists(exe)) return exe;
        }
        throw new Exception("BallisticEngine.Runtime.exe not found — build the solution first");
    }

    static string Next(string[] args, ref int i, string flag) =>
        ++i < args.Length ? args[i] : throw new CliUsageException($"{flag} needs a value");

    static int ParseInt(string s, string flag) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
            ? v : throw new CliUsageException($"{flag} expects an integer (got '{s}')");
}
