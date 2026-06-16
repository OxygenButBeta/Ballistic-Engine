using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using BallisticEngine.Serialization;

namespace BallisticEngine.Cli.Commands;

// `bal query <op> <scene> --points "x,y,z;..."` — the AI agent's SPATIAL PERCEPTION over the scene. Drives
// the headless DX12 player (GpuSceneQuery: inline RayQuery over the scene TLAS) and relays its JSON. Ops:
//   occupancy  — is each point inside solid geometry?
//   classify   — open / enclosed / solid per point
//   nudge      — move each occupied point to the nearest free-space position
//   rooms      — visibility-cluster labels (which points share a room)
//   visibility — clear line of sight per A->B pair (use --pairs "ax,ay,az>bx,by,bz; ...")
// Device-free CLI: spawns BallisticEngine.Runtime.exe with BALLISTIC_QUERY=<spec> (same pattern as
// `bal render`), so the agent gets sane spatial answers without staring at pixels. JSON to stdout.
internal sealed class QueryCommand : ICommand {
    public string Name => "query";
    public string Summary => "Spatial scene queries (occupancy/visibility/classify/nudge/rooms) over the TLAS.";
    public string Usage =>
        """
        Usage: bal query <op> <scene.scene> [--points "x,y,z; x,y,z; ..."] [--pairs "ax,ay,az>bx,by,bz; ..."]
                                            [--probe-radius R]
          op            occupancy | classify | nudge | rooms | visibility
          --points      semicolon-separated world points (occupancy/classify/nudge/rooms)
          --pairs       semicolon-separated A>B world-point pairs (visibility)
          --probe-radius  max ray reach for occupancy/classify (world units, default 200)
        Examples:
          bal query occupancy Main.scene --points "0,1,0; 5,1,5"
          bal query rooms     Main.scene --points "0,1,0; 0,1,40; 30,1,0"
          bal query visibility Main.scene --pairs "0,1,0>10,1,0; 0,1,0>0,1,50"
        """;

    public int Run(string[] args) {
        string? op = null, scenePath = null, pointsSpec = null, pairsSpec = null;
        float probeRadius = 200f;
        for (int i = 0; i < args.Length; i++) {
            switch (args[i]) {
                case "--points": pointsSpec = Next(args, ref i, "--points"); break;
                case "--pairs": pairsSpec = Next(args, ref i, "--pairs"); break;
                case "--probe-radius": probeRadius = ParseFloat(Next(args, ref i, "--probe-radius"), "--probe-radius"); break;
                default:
                    if (op is null) op = args[i];
                    else if (scenePath is null) scenePath = args[i];
                    else throw new CliUsageException($"unexpected argument '{args[i]}'");
                    break;
            }
        }
        if (op is null) throw new CliUsageException("expected an op (occupancy/classify/nudge/rooms/visibility)");
        if (scenePath is null) throw new CliUsageException("expected a scene path");
        op = op.ToLowerInvariant();
        if (op is not ("occupancy" or "classify" or "nudge" or "rooms" or "visibility"))
            throw new CliUsageException($"unknown op '{op}'");

        string sceneAbs = Path.GetFullPath(scenePath);
        if (!File.Exists(sceneAbs)) throw new Exception($"scene file not found: '{scenePath}'");
        string root = SceneFile.ResolveProjectRoot(sceneAbs);
        string sceneRel = Path.GetRelativePath(root, sceneAbs).Replace('\\', '/');

        // Build the query spec JSON.
        object spec;
        if (op == "visibility") {
            if (pairsSpec is null) throw new CliUsageException("visibility needs --pairs \"ax,ay,az>bx,by,bz; ...\"");
            spec = new { op, pairs = ParsePairs(pairsSpec), probeRadius };
        } else {
            if (pointsSpec is null) throw new CliUsageException($"{op} needs --points \"x,y,z; ...\"");
            spec = new { op, points = ParsePoints(pointsSpec), probeRadius };
        }

        string tempDir = Path.Combine(root, "Library", "Temp");
        Directory.CreateDirectory(tempDir);
        string specPath = Path.Combine(tempDir, "bal-query.json");
        string outPath = Path.Combine(tempDir, "bal-query.out.json");
        File.WriteAllText(specPath, JsonSerializer.Serialize(spec));

        try {
            RunPlayer(FindPlayerExe(), root, sceneRel, specPath, outPath);
            if (!File.Exists(outPath))
                throw new Exception("player exited but wrote no query output");
            // Relay the player's result JSON verbatim (re-parse so we emit pretty, validated JSON + our exit code).
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(outPath));
            bool ok = doc.RootElement.TryGetProperty("ok", out JsonElement okEl) && okEl.GetBoolean();
            Json.WriteRaw(doc.RootElement);
            return ok ? 0 : 1;
        } finally {
            try { File.Delete(specPath); } catch { }
            try { File.Delete(outPath); } catch { }
        }
    }

    static float[][] ParsePoints(string spec) {
        var list = new List<float[]>();
        foreach (string tok in spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            list.Add(Vec3(tok));
        if (list.Count == 0) throw new CliUsageException("--points parsed to zero points");
        return list.ToArray();
    }

    static float[][][] ParsePairs(string spec) {
        var list = new List<float[][]>();
        foreach (string tok in spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
            string[] ab = tok.Split('>');
            if (ab.Length != 2) throw new CliUsageException($"pair '{tok}' must be 'ax,ay,az>bx,by,bz'");
            list.Add(new[] { Vec3(ab[0]), Vec3(ab[1]) });
        }
        if (list.Count == 0) throw new CliUsageException("--pairs parsed to zero pairs");
        return list.ToArray();
    }

    static float[] Vec3(string tok) {
        string[] c = tok.Split(',');
        if (c.Length != 3) throw new CliUsageException($"'{tok}' must be 'x,y,z'");
        return new[] { ParseFloat(c[0], "x"), ParseFloat(c[1], "y"), ParseFloat(c[2], "z") };
    }

    static void RunPlayer(string playerExe, string projectRoot, string sceneRel, string specPath, string outPath) {
        var psi = new ProcessStartInfo {
            FileName = playerExe, WorkingDirectory = Path.GetDirectoryName(playerExe)!,
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        psi.ArgumentList.Add(projectRoot);
        psi.Environment["BALLISTIC_BACKEND"] = "dx12";
        psi.Environment["BALLISTIC_SCENE"] = sceneRel;
        psi.Environment["BALLISTIC_SCREENSHOT_PAUSED"] = "1";   // edit mode — no scripts/physics, stable geometry
        psi.Environment["BALLISTIC_QUERY"] = specPath;
        psi.Environment["BALLISTIC_QUERY_OUT"] = outPath;

        Console.Error.WriteLine($"  querying {sceneRel}...");
        using Process process = Process.Start(psi)!;
        string stderr = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(300_000)) {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new Exception("player timed out running the query");
        }
        if (process.ExitCode != 0 && !File.Exists(outPath))
            throw new Exception($"player exited {process.ExitCode}"
                + (stderr.Length > 0 ? $" — {stderr[..Math.Min(stderr.Length, 400)]}" : ""));
    }

    // The player exe sits in the engine repo's build tree (mirrors RenderCommand.FindPlayerExe).
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

    static float ParseFloat(string s, string flag) =>
        float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v)
            ? v : throw new CliUsageException($"{flag} expects a number (got '{s}')");
}
