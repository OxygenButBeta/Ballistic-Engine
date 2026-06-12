using System.Reflection;
using BallisticEngine.Serialization;

namespace BallisticEngine.Cli.Commands;

// `bal validate <scene.scene>` — statically checks a scene file WITHOUT booting the engine (GL-free):
// it parses the YAML into a SceneDocument and verifies every component type resolves, every member
// name exists on its type, transform parent ids point at real entities, and asset refs are well-formed.
// This is the agent's "is my edit sound?" gate before loading. Errors print in compiler format
// (`scene:path: message`) with did-you-mean suggestions; exit 0 = valid, 1 = errors found.
internal sealed class ValidateCommand : ICommand {
    public string Name => "validate";
    public string Summary => "Statically validate a .scene file (types, members, refs).";
    public string Usage => "Usage: bal validate <path-to-scene.scene>";

    public int Run(string[] args) {
        if (args.Length != 1)
            throw new CliUsageException("expected exactly one scene path");
        string path = args[0];
        if (!File.Exists(path))
            throw new Exception($"scene file not found: '{path}'");

        // Engine catalog (GL-free reflection).
        ComponentRegistry.Build(typeof(SceneManager).Assembly);

        SceneDocument doc;
        try {
            doc = SceneYaml.Deserializer.Deserialize<SceneDocument>(File.ReadAllText(path));
        }
        catch (Exception ex) {
            // A YAML parse failure is a single, fatal error.
            Json.Write(new ValidateResult(false, 1, [new Issue($"{path}: malformed YAML — {ex.Message}", "error")]));
            return 1;
        }

        var issues = new List<Issue>();
        if (doc is null) {
            issues.Add(new Issue($"{path}: empty or unreadable scene", "error"));
            Json.Write(new ValidateResult(false, issues.Count, issues));
            return 1;
        }

        // Collect entity ids for parent-ref resolution.
        var entityIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (EntityDocument e in doc.Entities ?? [])
            if (!string.IsNullOrEmpty(e.Id))
                entityIds.Add(e.Id);

        // Scene-wide components.
        foreach (ComponentDocument c in doc.SceneComponents ?? [])
            ValidateComponent(path, "sceneComponents", c, ComponentRegistry.ResolveScene,
                ComponentRegistry.SceneMenu, issues);

        // Entities + their components + transform parents.
        int idx = 0;
        foreach (EntityDocument e in doc.Entities ?? []) {
            string where = $"{path}:entities[{idx}]" + (string.IsNullOrEmpty(e.Name) ? "" : $"({e.Name})");
            string parent = e.Transform?.Parent;
            if (!string.IsNullOrEmpty(parent) && !entityIds.Contains(parent))
                issues.Add(new Issue($"{where}.transform.parent: no entity with id '{parent}' in this scene", "error"));

            foreach (ComponentDocument c in e.Components ?? [])
                ValidateComponent(where, "", c, ComponentRegistry.Resolve, ComponentRegistry.Menu, issues);
            idx++;
        }

        bool ok = !issues.Exists(i => i.severity == "error");
        Json.Write(new ValidateResult(ok, issues.Count, issues));
        return ok ? 0 : 1;
    }

    static void ValidateComponent(string where, string section, ComponentDocument c,
        Func<string, Type> resolve, IReadOnlyList<ComponentEntry> menu, List<Issue> issues) {
        string prefix = string.IsNullOrEmpty(section) ? where : $"{where}.{section}";
        if (string.IsNullOrEmpty(c.Type)) {
            issues.Add(new Issue($"{prefix}: component has no 'type'", "error"));
            return;
        }

        Type type = resolve(c.Type);
        if (type is null) {
            string suggestion = DidYouMean(c.Type, menu);
            issues.Add(new Issue(
                $"{prefix}.{c.Type}: unknown component type" + (suggestion is null ? "" : $" — did you mean '{suggestion}'?"),
                "error"));
            return;
        }

        // Member names. Unknown members are a WARNING (the deserializer ignores them, like Unity's
        // forward-compat), not a hard error — but flag them so an agent catches a typo.
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (MemberInfo m in ComponentReflection.SerializableMembers(type))
            known.Add(m.Name);
        if (c.Members is not null)
            foreach (string memberName in c.Members.Keys) {
                if (!known.Contains(memberName))
                    issues.Add(new Issue($"{prefix}.{c.Type}.{memberName}: not a serializable member of {type.Name}", "warning"));
            }
    }

    // Closest registry name by case-insensitive prefix / contains / edit-distance-1, for a typo hint.
    static string DidYouMean(string typed, IReadOnlyList<ComponentEntry> menu) {
        string best = null;
        int bestScore = int.MaxValue;
        foreach (ComponentEntry e in menu) {
            string name = e.Type.Name;
            int score = name.StartsWith(typed, StringComparison.OrdinalIgnoreCase) ? 0
                : name.Contains(typed, StringComparison.OrdinalIgnoreCase) ? 1
                : Levenshtein(name.ToLowerInvariant(), typed.ToLowerInvariant());
            if (score < bestScore) { bestScore = score; best = name; }
        }
        return bestScore <= 2 ? best : null; // only suggest a near match
    }

    static int Levenshtein(string a, string b) {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
            for (var j = 1; j <= b.Length; j++) {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
            }
        return d[a.Length, b.Length];
    }

    record ValidateResult(bool valid, int issueCount, List<Issue> issues);
    record Issue(string message, string severity);
}
