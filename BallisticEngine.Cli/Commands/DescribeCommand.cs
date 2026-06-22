using BallisticEngine.Serialization;

namespace BallisticEngine.Cli.Commands;

internal sealed class DescribeCommand : ICommand {
    public string Name => "describe";
    public string Summary => "Summarize a .scene: entity tree, components, counts.";
    public string Usage => "Usage: bal describe <path-to-scene.scene> [--flat]";

    public int Run(string[] args) {
        string path = null;
        bool flat = false;
        foreach (string a in args) {
            if (a == "--flat") flat = true;
            else if (path is null) path = a;
            else throw new CliUsageException($"unexpected argument '{a}'");
        }
        if (path is null) throw new CliUsageException("expected a scene path");
        if (!File.Exists(path)) throw new Exception($"scene file not found: '{path}'");

        SceneDocument doc = SceneYaml.Deserializer.Deserialize<SceneDocument>(File.ReadAllText(path))
            ?? throw new Exception("empty or unreadable scene");

        var entities = doc.Entities ?? [];
        int componentTotal = 0;
        foreach (EntityDocument e in entities)
            componentTotal += e.Components?.Count ?? 0;

        var sceneComps = new List<string>();
        foreach (ComponentDocument c in doc.SceneComponents ?? [])
            sceneComps.Add(c.Type);

        object tree = flat
            ? entities.Select(ToNode).ToList()
            : BuildTree(entities);

        Json.Write(new DescribeResult(
            doc.Name,
            new Counts(entities.Count, componentTotal, sceneComps.Count),
            sceneComps.Count > 0 ? sceneComps : null,
            tree));
        return 0;
    }

    static List<Node> BuildTree(List<EntityDocument> entities) {
        var byId = new Dictionary<string, EntityDocument>(StringComparer.Ordinal);
        foreach (EntityDocument e in entities)
            if (!string.IsNullOrEmpty(e.Id)) byId[e.Id] = e;

        var childrenOf = new Dictionary<string, List<EntityDocument>>(StringComparer.Ordinal);
        var roots = new List<EntityDocument>();
        foreach (EntityDocument e in entities) {
            string parent = e.Transform?.Parent;
            if (!string.IsNullOrEmpty(parent) && byId.ContainsKey(parent)) {
                if (!childrenOf.TryGetValue(parent, out var list)) childrenOf[parent] = list = new();
                list.Add(e);
            }
            else roots.Add(e);
        }

        Node Recurse(EntityDocument e) {
            Node n = ToNode(e);
            if (!string.IsNullOrEmpty(e.Id) && childrenOf.TryGetValue(e.Id, out var kids))
                n = n with { children = kids.Select(Recurse).ToList() };
            return n;
        }
        return roots.Select(Recurse).ToList();
    }

    static Node ToNode(EntityDocument e) {
        var comps = new List<string>();
        foreach (ComponentDocument c in e.Components ?? [])
            comps.Add(c.Type);
        return new Node(
            e.Name,
            e.IsActive ? null : false, comps.Count > 0 ? comps : null,
            null);
    }

    record DescribeResult(string name, Counts counts, List<string> sceneComponents, object entities);
    record Counts(int entities, int components, int sceneComponents);
    record Node(string name, bool? active, List<string> components, List<Node> children);
}
