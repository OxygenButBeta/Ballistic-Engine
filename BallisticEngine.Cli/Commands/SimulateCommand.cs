using System.Globalization;
using System.Reflection;
using BallisticEngine.Serialization;

namespace BallisticEngine.Cli.Commands;

// `bal simulate <scene> --steps N [--watch ...]` — headless play-mode probe: boots the REAL engine
// (HeadlessRuntime: scripts compile + load, physics, contact events — everything but rendering),
// loads the scene, enters play, steps the fixed 60 Hz loop, and samples watched values into a JSON
// time series. This is the agent's numeric verification channel: assert "the crate lands by step
// 120" from data instead of staring at screenshots.
//
// Watch grammar (repeatable):
//   --watch Player                          entity world position (the common case)
//   --watch Player:Rigidbody.Velocity       a component member (any public property/field —
//                                           runtime-only members like Velocity included)
internal sealed class SimulateCommand : ICommand {
    public string Name => "simulate";
    public string Summary => "Headless play-mode run with numeric probes.";
    public string Usage =>
        """
        Usage: bal simulate <scene.scene> [--steps N] [--watch <Entity>[:<Component>.<Member>]]... [--every K] [--quiet]
          --steps N   fixed 60 Hz steps to run (default 300 = 5 seconds)
          --watch     value to sample (repeatable); entity name alone = world position
          --every K   sample every K steps (default keeps <= 100 samples)
          --quiet     suppress engine info logs on stderr
        """;

    public int Run(string[] args) {
        string? scenePath = null;
        int steps = 300;
        int? every = null;
        bool quiet = false;
        var watchSpecs = new List<string>();
        for (int i = 0; i < args.Length; i++) {
            switch (args[i]) {
                case "--steps": steps = ParseInt(Next(args, ref i, "--steps"), "--steps"); break;
                case "--watch": watchSpecs.Add(Next(args, ref i, "--watch")); break;
                case "--every": every = ParseInt(Next(args, ref i, "--every"), "--every"); break;
                case "--quiet": quiet = true; break;
                default:
                    if (scenePath is null) scenePath = args[i];
                    else throw new CliUsageException($"unexpected argument '{args[i]}'");
                    break;
            }
        }
        if (scenePath is null) throw new CliUsageException("expected a scene path");
        if (steps < 1) throw new CliUsageException("--steps must be >= 1");
        string sceneAbs = Path.GetFullPath(scenePath);
        if (!File.Exists(sceneAbs)) throw new Exception($"scene file not found: '{scenePath}'");
        string root = SceneFile.ResolveProjectRoot(sceneAbs);
        int sampleEvery = every ?? Math.Max(1, steps / 100);

        // Engine logs -> stderr (stdout stays JSON); errors are also counted for the report.
        int errorCount = 0;
        var firstErrors = new List<string>();
        Debugging.OnMessage += (message, level) => {
            if (level == 2) {
                errorCount++;
                if (firstErrors.Count < 10) firstErrors.Add(message);
            }
            if (level > 0 || !quiet) Console.Error.WriteLine(message);
        };

        // Full engine bring-up, no window: scripts compile + load, assets import, physics binds.
        var bootstrap = new EngineBootstrap(new HeadlessRuntime(), root);
        try {
            Scene scene = SceneManager.GetCurrentScene();
            scene.Clear();
            SceneSerializer.Deserialize(File.ReadAllText(sceneAbs));

            var watches = watchSpecs.Count > 0
                ? watchSpecs.Select(spec => Watch.Resolve(scene, spec)).ToList()
                : new List<Watch>();

            SceneManager.StartPlay();
            if (!SceneManager.IsPlaying)
                throw new Exception("StartPlay refused (compile errors? see stderr)");

            const double dt = 1.0 / 60.0;
            foreach (Watch w in watches)
                w.Sample(0); // initial state before the first step
            for (int step = 1; step <= steps; step++) {
                bootstrap.UpdateFrame(dt);
                if (step % sampleEvery == 0 || step == steps)
                    foreach (Watch w in watches)
                        w.Sample(step);
            }
            SceneManager.StopPlay();

            Json.Write(new {
                ok = errorCount == 0,
                scene = scenePath.Replace('\\', '/'),
                steps,
                secondsSimulated = Math.Round(steps / 60.0, 3),
                sampleEvery,
                errors = errorCount,
                firstErrors = firstErrors.Count > 0 ? firstErrors : null,
                watches = watches.Select(w => new {
                    target = w.Spec,
                    series = w.Series,
                }).ToList(),
            });
            return errorCount == 0 ? 0 : 1;
        }
        finally {
            JobSystem.Shutdown(); // scheduler workers are foreground threads — without this we hang
        }
    }

    static string Next(string[] args, ref int i, string flag) =>
        ++i < args.Length ? args[i] : throw new CliUsageException($"{flag} needs a value");

    static int ParseInt(string s, string flag) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
            ? v : throw new CliUsageException($"{flag} expects an integer (got '{s}')");

    // One watched value: an entity's world position, or any public property/field on one of its
    // components (NOT limited to serializable members — runtime-only state like Velocity is the
    // whole point of probing).
    sealed class Watch {
        public string Spec = "";
        public List<object> Series { get; } = new();

        Entity entity = null!;
        Behaviour? component;
        MemberInfo? member;

        public static Watch Resolve(Scene scene, string spec) {
            int colon = spec.IndexOf(':');
            string entityName = colon < 0 ? spec : spec[..colon];

            Entity? entity = scene.Entities.FirstOrDefault(e =>
                string.Equals(e.Name, entityName, StringComparison.OrdinalIgnoreCase));
            if (entity is null) {
                string? hint = Suggest.Closest(entityName, scene.Entities.Select(e => e.Name));
                throw new Exception($"no entity named '{entityName}' in the scene"
                    + (hint is null ? "" : $" — did you mean '{hint}'?"));
            }

            var watch = new Watch { Spec = spec, entity = entity };
            if (colon < 0)
                return watch; // position watch

            string memberPath = spec[(colon + 1)..];
            int dot = memberPath.IndexOf('.');
            if (dot <= 0 || dot == memberPath.Length - 1)
                throw new CliUsageException($"watch '{spec}': expected <Entity>:<Component>.<Member>");
            string componentName = memberPath[..dot];
            string memberName = memberPath[(dot + 1)..];

            Behaviour? component = entity.Behaviours.FirstOrDefault(b =>
                string.Equals(b.GetType().Name, componentName, StringComparison.OrdinalIgnoreCase));
            if (component is null) {
                string? hint = Suggest.Closest(componentName, entity.Behaviours.Select(b => b.GetType().Name));
                throw new Exception($"watch '{spec}': '{entityName}' has no {componentName} component"
                    + (hint is null ? "" : $" — did you mean '{hint}'?"));
            }

            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;
            MemberInfo? member = (MemberInfo?)component.GetType().GetProperty(memberName, flags)
                                 ?? component.GetType().GetField(memberName, flags);
            if (member is null) {
                var names = component.GetType().GetProperties(flags).Select(p => p.Name)
                    .Concat(component.GetType().GetFields(flags).Select(f => f.Name));
                string? hint = Suggest.Closest(memberName, names);
                throw new Exception($"watch '{spec}': {componentName} has no public member '{memberName}'"
                    + (hint is null ? "" : $" — did you mean '{hint}'?"));
            }

            watch.component = component;
            watch.member = member;
            return watch;
        }

        public void Sample(int step) {
            object? value;
            if (member is null) {
                value = entity.transform.WorldPosition;
            }
            else {
                try {
                    value = member is PropertyInfo p ? p.GetValue(component) : ((FieldInfo)member).GetValue(component);
                }
                catch (Exception ex) {
                    value = $"<threw: {ex.InnerException?.Message ?? ex.Message}>";
                }
            }
            Series.Add(new { step, value = SceneFile.ToJsonValue(value) });
        }
    }
}
