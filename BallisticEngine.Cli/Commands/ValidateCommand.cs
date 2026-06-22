using System.Reflection;
using BallisticEngine.Serialization;

namespace BallisticEngine.Cli.Commands;

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

        SceneFile.BuildRegistry(path);

        SceneDocument doc;
        try {
            doc = SceneYaml.Deserializer.Deserialize<SceneDocument>(File.ReadAllText(path));
        }
        catch (Exception ex) {
            Json.Write(new ValidateResult(false, 1, [new Issue($"{path}: malformed YAML — {ex.Message}", "error")]));
            return 1;
        }

        var issues = new List<Issue>();
        if (doc is null) {
            issues.Add(new Issue($"{path}: empty or unreadable scene", "error"));
            Json.Write(new ValidateResult(false, issues.Count, issues));
            return 1;
        }

        var entityIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (EntityDocument e in doc.Entities ?? [])
            if (!string.IsNullOrEmpty(e.Id))
                entityIds.Add(e.Id);

        foreach (ComponentDocument c in doc.SceneComponents ?? [])
            ValidateComponent(path, "sceneComponents", c, ComponentRegistry.ResolveScene,
                ComponentRegistry.SceneMenu, issues);

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

        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (MemberInfo m in ComponentReflection.SerializableMembers(type))
            known.Add(m.Name);
        if (c.Members is not null)
            foreach (string memberName in c.Members.Keys) {
                if (!known.Contains(memberName))
                    issues.Add(new Issue($"{prefix}.{c.Type}.{memberName}: not a serializable member of {type.Name}", "warning"));
            }
    }

    static string DidYouMean(string typed, IReadOnlyList<ComponentEntry> menu) =>
        Suggest.Closest(typed, menu.Select(e => e.Type.Name));

    record ValidateResult(bool valid, int issueCount, List<Issue> issues);
    record Issue(string message, string severity);
}
