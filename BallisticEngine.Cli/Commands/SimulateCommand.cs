using System.Globalization;
using System.Reflection;
using System.Text.Json;
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
        Usage: bal simulate <scene.scene> [--steps N] [--watch <Entity>[:<Component>.<Member>]]...
                            [--snapshot <Entity>]... [--every K] [--input script.json] [--quiet]
          --steps N   fixed 60 Hz steps to run (default 300 = 5 seconds)
          --watch     value to sample over time (repeatable); entity name alone = world position
          --snapshot  full live-state dump of an entity at the final step (repeatable): transform +
                      every public member of every component (live runtime introspection)
          --every K   sample every K steps (default keeps <= 100 samples)
          --input     deterministic input script: {"keys":[{"key":"W","from":0,"to":120}],
                      "mouse":[{"deltaX":2,"deltaY":0,"from":0,"to":60}],
                      "buttons":[{"button":"Left","from":10,"to":12}],
                      "axes":[{"player":0,"axis":0,"value":1.0,"from":0,"to":100}]}
          --quiet     suppress engine info logs on stderr
        """;

    public int Run(string[] args) {
        string? scenePath = null, inputPath = null;
        int steps = 300;
        int? every = null;
        bool quiet = false;
        var watchSpecs = new List<string>();
        var snapshotSpecs = new List<string>();
        for (int i = 0; i < args.Length; i++) {
            switch (args[i]) {
                case "--steps": steps = ParseInt(Next(args, ref i, "--steps"), "--steps"); break;
                case "--watch": watchSpecs.Add(Next(args, ref i, "--watch")); break;
                case "--snapshot": snapshotSpecs.Add(Next(args, ref i, "--snapshot")); break;
                case "--every": every = ParseInt(Next(args, ref i, "--every"), "--every"); break;
                case "--input": inputPath = Next(args, ref i, "--input"); break;
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

        ScriptedInput? scripted = inputPath is null ? null : LoadInputScript(inputPath);

        // Full engine bring-up, no window: scripts compile + load, assets import, physics binds.
        var bootstrap = new EngineBootstrap(new HeadlessRuntime(scripted), root);
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
                if (scripted is not null)
                    scripted.CurrentStep = step - 1; // script steps are 0-based
                bootstrap.UpdateFrame(dt);
                if (step % sampleEvery == 0 || step == steps)
                    foreach (Watch w in watches)
                        w.Sample(step);
            }

            // Live runtime introspection: a FULL snapshot of each named entity's live component state at the
            // final step (every public member of every component, not just pre-declared watches) — captured
            // while still in play so runtime-only values (velocities, script state) are live.
            var snapshots = snapshotSpecs.Count > 0
                ? snapshotSpecs.Select(spec => SnapshotEntity(scene, spec, steps)).ToList()
                : null;

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
                snapshots,
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

    // Parses the JSON input script (see Usage) into a ScriptedInput timeline. Key/button names are
    // the OpenTK enum names ("W", "Space", "LeftShift", "Left"/"Right" for mouse), case-insensitive.
    static ScriptedInput LoadInputScript(string path) {
        if (!File.Exists(path))
            throw new Exception($"input script not found: '{path}'");
        var scripted = new ScriptedInput();
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement root = doc.RootElement;

        if (root.TryGetProperty("keys", out JsonElement keys))
            foreach (JsonElement e in keys.EnumerateArray()) {
                string name = e.GetProperty("key").GetString()!;
                if (!Enum.TryParse(name, ignoreCase: true, out OpenTK.Windowing.GraphicsLibraryFramework.Keys key))
                    throw new Exception($"unknown key '{name}' (OpenTK key names: W, Space, LeftShift, ...)");
                scripted.AddKey(key, From(e), To(e));
            }
        if (root.TryGetProperty("buttons", out JsonElement buttons))
            foreach (JsonElement e in buttons.EnumerateArray()) {
                string name = e.GetProperty("button").GetString()!;
                if (!Enum.TryParse(name, ignoreCase: true, out OpenTK.Windowing.GraphicsLibraryFramework.MouseButton button))
                    throw new Exception($"unknown mouse button '{name}' (Left, Right, Middle, ...)");
                scripted.AddMouseButton(button, From(e), To(e));
            }
        if (root.TryGetProperty("mouse", out JsonElement mouse))
            foreach (JsonElement e in mouse.EnumerateArray())
                scripted.AddMouseDelta(
                    e.TryGetProperty("deltaX", out JsonElement dx) ? dx.GetSingle() : 0f,
                    e.TryGetProperty("deltaY", out JsonElement dy) ? dy.GetSingle() : 0f,
                    From(e), To(e));
        if (root.TryGetProperty("axes", out JsonElement axes))
            foreach (JsonElement e in axes.EnumerateArray())
                scripted.AddAxis(
                    e.TryGetProperty("player", out JsonElement pl) ? pl.GetInt32() : 0,
                    e.GetProperty("axis").GetInt32(),
                    e.GetProperty("value").GetSingle(),
                    From(e), To(e));

        return scripted;

        static int From(JsonElement e) => e.TryGetProperty("from", out JsonElement v) ? v.GetInt32() : 0;
        static int To(JsonElement e) => e.TryGetProperty("to", out JsonElement v) ? v.GetInt32() : int.MaxValue;
    }

    // One watched value: an entity's world position, or any public property/field on one of its
    // components (NOT limited to serializable members — runtime-only state like Velocity is the
    // whole point of probing).
    // Full live-state snapshot of one entity at the current step: transform + every public member of every
    // component, read by reflection (the introspection surface — "read any component's live values during
    // play"). Reuses SceneFile.ToJsonValue so vectors/enums/asset refs serialize consistently with watches.
    static object SnapshotEntity(Scene scene, string entityName, int step) {
        Entity? entity = scene.Entities.FirstOrDefault(e =>
            string.Equals(e.Name, entityName, StringComparison.OrdinalIgnoreCase));
        if (entity is null) {
            string? hint = Suggest.Closest(entityName, scene.Entities.Select(e => e.Name));
            throw new Exception($"--snapshot: no entity named '{entityName}'"
                + (hint is null ? "" : $" — did you mean '{hint}'?"));
        }

        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;
        // Only flatten values ToJsonValue can render to a scalar/vector — primitives/enums/strings + the
        // Vector*/Quaternion types. Engine REFERENCE values (Entity/Transform/Behaviour back-pointers, asset
        // handles) would recurse into a cycle (Entity.transform.Entity.transform...), so they're surfaced as a
        // short "<Type>" marker, not expanded. This is the introspection equivalent of the scene serializer's
        // "serializable members only" rule.
        static bool IsScalar(object? v) => v is null or string or bool or Enum or decimal
            || (v is not null && v.GetType() is { IsPrimitive: true })
            || v is Vector2 or Vector3 or Vector4 or Quaternion;

        object ReadMember(object obj, MemberInfo m) {
            try {
                object? v = m is PropertyInfo p ? p.GetValue(obj) : ((FieldInfo)m).GetValue(obj);
                return IsScalar(v) ? SceneFile.ToJsonValue(v)! : $"<{v?.GetType().Name ?? "null"}>";
            } catch (Exception ex) { return $"<threw: {ex.InnerException?.Message ?? ex.Message}>"; }
        }

        // Skip the noisy engine base-class plumbing every Behaviour exposes (transform/entity/lifecycle flags
        // already covered by the entity-level fields) so the snapshot is the COMPONENT's own state.
        var skip = new HashSet<string> { "transform", "Entity", "IsActive", "IsEnabled", "gameObject", "tag", "name" };

        var components = entity.Behaviours.Select(b => {
            Type t = b.GetType();
            var members = new Dictionary<string, object>();
            foreach (PropertyInfo p in t.GetProperties(flags))
                if (p.CanRead && p.GetIndexParameters().Length == 0 && !skip.Contains(p.Name))
                    members[p.Name] = ReadMember(b, p);
            foreach (FieldInfo f in t.GetFields(flags))
                if (!skip.Contains(f.Name)) members[f.Name] = ReadMember(b, f);
            return new { type = t.Name, enabled = b.IsEnabled, members };
        }).ToList();

        return new {
            entity = entity.Name,
            step,
            transform = new {
                position = SceneFile.ToJsonValue(entity.transform.Position),
                worldPosition = SceneFile.ToJsonValue(entity.transform.WorldPosition),
                rotationEulerDegrees = SceneFile.ToJsonValue(entity.transform.EulerAngles),
                scale = SceneFile.ToJsonValue(entity.transform.Scale),
            },
            active = entity.IsActive,
            components,
        };
    }

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
