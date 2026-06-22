using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace BallisticEngine.Cli.Commands;

internal sealed class GBufferCommand : ICommand {
    public string Name => "gbuffer";
    public string Summary => "Dump the raw G-buffer (depth/normal/albedo) for an agent to read.";
    public string Usage =>
        """
        Usage: bal gbuffer <scene.scene> [--out <dir>] [--frame F]
          --out     output directory (default: <project>/Library/GBuffer)
          --frame   capture frame (default 30; paused deterministic mode converges immediately)
        Writes depth.bin (R32F), normal.bin (RGBA16F packed N*0.5+0.5), albedo.bin (RGBA8 sRGB),
        and manifest.json (width/height/format/encoding per buffer).
        """;

    public int Run(string[] args) {
        string? scenePath = null, outDir = null;
        int frame = 30;
        for (int i = 0; i < args.Length; i++) {
            switch (args[i]) {
                case "--out": outDir = Next(args, ref i, "--out"); break;
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
        outDir ??= Path.Combine(root, "Library", "GBuffer");
        Directory.CreateDirectory(outDir);

        RunPlayer(FindPlayerExe(), root, sceneRel, outDir, frame);

        string manifestPath = Path.Combine(outDir, "manifest.json");
        if (!File.Exists(manifestPath))
            throw new Exception("player exited but wrote no G-buffer manifest");
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        Json.Write(new {
            ok = doc.RootElement.TryGetProperty("ok", out JsonElement okEl) && okEl.GetBoolean(),
            scene = sceneRel, dir = outDir, manifest = manifestPath,
            files = new {
                depth = Path.Combine(outDir, "depth.bin"),
                normal = Path.Combine(outDir, "normal.bin"),
                albedo = Path.Combine(outDir, "albedo.bin"),
            },
        });
        return 0;
    }

    static void RunPlayer(string playerExe, string projectRoot, string sceneRel, string outDir, int frame) {
        string bmp = Path.Combine(outDir, "_frame.bmp");
        var psi = new ProcessStartInfo {
            FileName = playerExe, WorkingDirectory = Path.GetDirectoryName(playerExe)!,
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        psi.ArgumentList.Add(projectRoot);
        psi.Environment["BALLISTIC_BACKEND"] = "dx12";
        psi.Environment["BALLISTIC_SCENE"] = sceneRel;
        psi.Environment["BALLISTIC_SCREENSHOT"] = bmp;
        psi.Environment["BALLISTIC_SCREENSHOT_FRAME"] = frame.ToString(CultureInfo.InvariantCulture);
        psi.Environment["BALLISTIC_SCREENSHOT_PAUSED"] = "1";
        psi.Environment["BALLISTIC_DETERMINISTIC"] = "1";
        psi.Environment["BALLISTIC_GBUFFER_DUMP"] = outDir;

        Console.Error.WriteLine($"  dumping g-buffer for {sceneRel}...");
        using Process process = Process.Start(psi)!;
        string stderr = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(300_000)) {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new Exception("player timed out dumping the g-buffer");
        }
        if (process.ExitCode != 0 && !File.Exists(Path.Combine(outDir, "manifest.json")))
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
