using System.Diagnostics;
using System.Globalization;
using BallisticEngine.Serialization;
using OpenTK.Mathematics;

namespace BallisticEngine.Cli.Commands;

// `bal render <scene>` — render a scene to images by driving the headless player (deterministic
// paused captures: BALLISTIC_SCENE + BALLISTIC_SCREENSHOT + BALLISTIC_DETERMINISTIC). One shot of
// the serialized camera by default; `--orbit N` captures N viewpoints on a circle around --center
// (camera scene copies go to Library/Temp, never into Assets). Multi-view is the cheap fix for
// single-viewpoint spatial reasoning: an agent judging a layout looks at 4-8 angles, not one.
internal sealed class RenderCommand : ICommand {
    public string Name => "render";
    public string Summary => "Render a scene to BMP(s) headlessly; --orbit N for multi-view.";
    public string Usage =>
        """
        Usage: bal render <scene.scene> [--out <dir>] [--orbit N] [--center x,y,z] [--radius R]
                                        [--height H] [--frame F] [--idmap] [--play]
                                        [--eye x,y,z] [--look x,y,z]
          --out     output directory (default: <project>/Library/Renders)
          --orbit   N camera positions on a circle around --center, looking at it
          --center  orbit target (default 0,0,0)
          --radius  orbit radius (default: the scene camera's horizontal distance to center)
          --height  orbit camera height (default: the scene camera's Y)
          --eye     place the camera at this exact world position (reproduce a free-fly view)
          --look    point the --eye camera at this world target (default: the scene center / --center)
          --frame   capture frame (default 30; deterministic mode converges immediately)
          --idmap   also capture the entity-ID map per shot
          --play    run play mode before capture (default: paused edit mode, bit-exact)
        """;

    public int Run(string[] args) {
        string? scenePath = null, outDir = null;
        int orbit = 0, frame = 30;
        Vector3 center = Vector3.Zero;
        float? radius = null, height = null;
        bool idmap = false, play = false;
        Vector3? eye = null, look = null;
        for (int i = 0; i < args.Length; i++) {
            switch (args[i]) {
                case "--out": outDir = Next(args, ref i, "--out"); break;
                case "--orbit": orbit = ParseInt(Next(args, ref i, "--orbit"), "--orbit"); break;
                case "--center": center = SceneFile.ParseVec3(Next(args, ref i, "--center")); break;
                case "--radius": radius = ParseFloat(Next(args, ref i, "--radius"), "--radius"); break;
                case "--height": height = ParseFloat(Next(args, ref i, "--height"), "--height"); break;
                case "--eye": eye = SceneFile.ParseVec3(Next(args, ref i, "--eye")); break;
                case "--look": look = SceneFile.ParseVec3(Next(args, ref i, "--look")); break;
                case "--frame": frame = ParseInt(Next(args, ref i, "--frame"), "--frame"); break;
                case "--idmap": idmap = true; break;
                case "--play": play = true; break;
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
        string playerExe = FindPlayerExe();
        outDir ??= Path.Combine(root, "Library", "Renders");
        Directory.CreateDirectory(outDir);

        var shots = new List<object>();
        if (eye is { } eyePos) {
            // EXACT-POSE capture: place the camera at --eye looking at --look (or the scene center).
            // The cheap way to reproduce a user's free-fly viewpoint headlessly — paused captures of
            // the serialized camera couldn't hit the angles where view-dependent artifacts show.
            SceneFile.BuildRegistry(sceneAbs);
            SceneDocument doc = SceneFile.Load(sceneAbs);
            EntityDocument cam = (doc.Entities ?? [])
                .FirstOrDefault(e => (e.Components ?? []).Any(c => string.Equals(c.Type, "HDCamera", StringComparison.OrdinalIgnoreCase)))
                ?? throw new Exception("no entity with an HDCamera component in the scene");
            cam.Transform ??= new TransformDocument();
            cam.Transform.Position = eyePos;
            cam.Transform.Rotation = LookAt(eyePos, look ?? center);
            string tempDir = Path.Combine(root, "Library", "Temp");
            Directory.CreateDirectory(tempDir);
            string tempScene = Path.Combine(tempDir, "bal-eye.scene");
            SceneFile.Save(tempScene, doc);
            try {
                string outPath = Path.Combine(outDir, Path.GetFileNameWithoutExtension(sceneRel) + "_eye.bmp");
                RunPlayer(playerExe, root, "Library/Temp/bal-eye.scene", outPath, frame, idmap, play);
                shots.Add(ShotInfo(outPath, null, idmap));
            }
            finally {
                try { File.Delete(tempScene); } catch { }
            }
        }
        else if (orbit <= 0) {
            string outPath = Path.Combine(outDir, Path.GetFileNameWithoutExtension(sceneRel) + ".bmp");
            RunPlayer(playerExe, root, sceneRel, outPath, frame, idmap, play);
            shots.Add(ShotInfo(outPath, null, idmap));
        }
        else {
            // Orbit: rewrite the scene camera per angle into Library/Temp (asset refs are
            // project-relative, so a scene loads from anywhere inside the project).
            SceneFile.BuildRegistry(sceneAbs);
            SceneDocument doc = SceneFile.Load(sceneAbs);
            EntityDocument camera = (doc.Entities ?? [])
                .FirstOrDefault(e => (e.Components ?? []).Any(c => string.Equals(c.Type, "HDCamera", StringComparison.OrdinalIgnoreCase)))
                ?? throw new Exception("no entity with an HDCamera component in the scene");
            camera.Transform ??= new TransformDocument();

            Vector3 original = camera.Transform.Position;
            float r = radius ?? MathF.Max(1f, new Vector2(original.X - center.X, original.Z - center.Z).Length);
            float y = height ?? original.Y;

            string tempDir = Path.Combine(root, "Library", "Temp");
            Directory.CreateDirectory(tempDir);
            try {
                for (int k = 0; k < orbit; k++) {
                    float angle = k * MathF.Tau / orbit;
                    var pos = new Vector3(center.X + r * MathF.Sin(angle), y, center.Z + r * MathF.Cos(angle));
                    camera.Transform.Position = pos;
                    camera.Transform.Rotation = LookAt(pos, center);

                    string tempScene = Path.Combine(tempDir, $"bal-orbit-{k}.scene");
                    SceneFile.Save(tempScene, doc);

                    string outPath = Path.Combine(outDir, $"orbit_{k}.bmp");
                    RunPlayer(playerExe, root, $"Library/Temp/bal-orbit-{k}.scene", outPath, frame, idmap, play);
                    shots.Add(ShotInfo(outPath, Math.Round(angle * 180 / MathF.PI, 1), idmap));
                }
            }
            finally {
                for (int k = 0; k < orbit; k++)
                    try { File.Delete(Path.Combine(tempDir, $"bal-orbit-{k}.scene")); } catch { }
            }
        }

        Json.Write(new { ok = true, scene = sceneRel, frame, deterministic = true, shots });
        return 0;
    }

    // Engine camera convention (Transform.EulerAngles -> FromEulerAngles(pitch, yaw, 0)):
    // forward = (cos p sin y, -sin p, cos p cos y), so pitch = -asin(f.Y), yaw = atan2(f.X, f.Z).
    static Quaternion LookAt(Vector3 eye, Vector3 target) {
        Vector3 f = Vector3.Normalize(target - eye);
        float pitch = -MathF.Asin(Math.Clamp(f.Y, -1f, 1f));
        float yaw = MathF.Atan2(f.X, f.Z);
        return SceneFile.EulerDegreesToQuaternion(new Vector3(
            MathHelper.RadiansToDegrees(pitch), MathHelper.RadiansToDegrees(yaw), 0));
    }

    static void RunPlayer(string playerExe, string projectRoot, string sceneRel, string outPath,
        int frame, bool idmap, bool play) {
        var psi = new ProcessStartInfo {
            FileName = playerExe,
            WorkingDirectory = Path.GetDirectoryName(playerExe)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add(projectRoot);
        psi.Environment["BALLISTIC_SCENE"] = sceneRel;
        psi.Environment["BALLISTIC_SCREENSHOT"] = outPath;
        psi.Environment["BALLISTIC_SCREENSHOT_FRAME"] = frame.ToString(CultureInfo.InvariantCulture);
        psi.Environment["BALLISTIC_DETERMINISTIC"] = "1";
        if (!play)
            psi.Environment["BALLISTIC_SCREENSHOT_PAUSED"] = "1";
        if (idmap)
            psi.Environment["BALLISTIC_IDMAP"] = Path.ChangeExtension(outPath, null) + "_idmap";

        Console.Error.WriteLine($"  rendering {Path.GetFileName(outPath)} ({sceneRel})...");
        using Process process = Process.Start(psi)!;
        string stderr = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(300_000)) {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new Exception($"player timed out rendering '{outPath}'");
        }
        if (process.ExitCode != 0)
            throw new Exception($"player exited {process.ExitCode} rendering '{outPath}'"
                                + (stderr.Length > 0 ? $" — {stderr[..Math.Min(stderr.Length, 400)]}" : ""));
        if (!File.Exists(outPath))
            throw new Exception($"player exited 0 but '{outPath}' was not written");
    }

    static object ShotInfo(string outPath, double? angleDegrees, bool idmap) => new {
        image = outPath,
        stats = outPath + ".stats.json",
        idmap = idmap ? Path.ChangeExtension(outPath, null) + "_idmap.json" : null,
        angleDegrees,
    };

    // The player exe sits in the engine repo's build tree. BALLISTIC_ENGINE_ROOT overrides; the
    // default walks up from bal.exe to the repo root (BallisticEngine.slnx marker) and prefers
    // the same configuration bal itself was built with.
    static string FindPlayerExe() {
        string? engineRoot = Environment.GetEnvironmentVariable("BALLISTIC_ENGINE_ROOT");
        if (engineRoot is null) {
            DirectoryInfo? dir = new(AppContext.BaseDirectory);
            for (int i = 0; dir is not null && i < 8; i++, dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "BallisticEngine.slnx"))) {
                    engineRoot = dir.FullName;
                    break;
                }
        }
        if (engineRoot is null)
            throw new Exception("can't locate the engine repo (set BALLISTIC_ENGINE_ROOT)");

        string config = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}")
            ? "Release" : "Debug";
        foreach (string c in new[] { config, config == "Debug" ? "Release" : "Debug" }) {
            string exe = Path.Combine(engineRoot, "BallisticEngine.Runtime", "bin", c, "net9.0", "BallisticEngine.Runtime.exe");
            if (File.Exists(exe))
                return exe;
        }
        throw new Exception("BallisticEngine.Runtime.exe not found — build the solution first (dotnet build BallisticEngine.slnx)");
    }

    static string Next(string[] args, ref int i, string flag) =>
        ++i < args.Length ? args[i] : throw new CliUsageException($"{flag} needs a value");

    static int ParseInt(string s, string flag) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
            ? v : throw new CliUsageException($"{flag} expects an integer (got '{s}')");

    static float ParseFloat(string s, string flag) =>
        float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v)
            ? v : throw new CliUsageException($"{flag} expects a number (got '{s}')");
}
