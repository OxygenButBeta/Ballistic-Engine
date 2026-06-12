using BallisticEngine.AssetPipeline;

namespace BallisticEngine.Cli.Commands;

// `bal assets <action>` — GL-free asset-database queries over the .meta sidecars and text assets
// (no import, no engine boot). The reverse-reference query is the agent's "what breaks if I touch
// this" map: it scans every text asset (.scene/.mat/.volume/.cubemap/.shader/.asset/.prefab and
// project.json) for both the guid: form and the path form of a reference.
internal sealed class AssetsCommand : ICommand {
    public string Name => "assets";
    public string Summary => "Query assets: resolve path<->guid, reverse refs, list.";
    public string Usage =>
        """
        Usage: bal assets <action> <project-dir-or-any-path-inside> ...
          resolve <project> <Assets/path | guid:<32hex> | 32hex>   path<->guid<->importer
          refs    <project> <Assets/path | guid:...>               who references this asset
          list    <project> [--ext .obj] [--folder Assets/Sub]     all assets with guids
        """;

    // Text formats that can contain asset references.
    static readonly string[] RefBearingExtensions =
        [".scene", ".mat", ".volume", ".cubemap", ".shader", ".asset", ".prefab"];

    public int Run(string[] args) {
        if (args.Length < 2) throw new CliUsageException("expected an action and a project path");
        string action = args[0];
        string root = SceneFile.ResolveProjectRoot(args[1]);
        string[] rest = args[2..];

        return action switch {
            "resolve" => Resolve(root, rest),
            "refs" => Refs(root, rest),
            "list" => List(root, rest),
            _ => throw new CliUsageException($"unknown action '{action}'"),
        };
    }

    // ---- resolve --------------------------------------------------------------

    static int Resolve(string root, string[] args) {
        if (args.Length != 1) throw new CliUsageException("resolve needs one asset reference");
        (string path, Guid guid, string? importer) = ResolveReference(root, args[0]);
        Json.Write(new { path, guid = guid.ToString("N"), importer });
        return 0;
    }

    // "Assets/..." (load its .meta directly), "guid:<hex>" or bare 32-hex (scan the .meta files).
    static (string path, Guid guid, string? importer) ResolveReference(string root, string reference) {
        string assetsRoot = Path.Combine(root, "Assets");

        if (AssetRef.IsGuidRef(reference, out Guid wanted) ||
            (reference.Length == 32 && Guid.TryParseExact(reference, "N", out wanted))) {
            foreach ((string path, MetaFile meta) in EnumerateMetas(root))
                if (meta.Guid == wanted)
                    return (path, meta.Guid, meta.Importer);
            throw new Exception($"no asset with guid {wanted:N} in '{assetsRoot}'");
        }

        string norm = reference.Replace('\\', '/');
        if (!norm.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            throw new Exception($"asset refs are 'Assets/...' paths or 'guid:<32hex>' (got '{reference}')");
        string abs = Path.Combine(root, norm);
        if (!File.Exists(abs))
            throw new Exception($"asset file not found: '{norm}' (under '{root}')");
        string metaPath = MetaFile.PathFor(abs);
        if (!File.Exists(metaPath))
            throw new Exception($"'{norm}' has no .meta sidecar — run 'bal import' first");
        MetaFile m = MetaFile.Load(metaPath);
        return (norm, m.Guid, m.Importer);
    }

    // ---- refs -----------------------------------------------------------------

    static int Refs(string root, string[] args) {
        if (args.Length != 1) throw new CliUsageException("refs needs one asset reference");

        // A guid that no longer resolves is still searchable — that's the broken-ref hunt
        // ("what references the asset I deleted?"), so don't require resolution for guid form.
        string? path;
        Guid guid;
        if (AssetRef.IsGuidRef(args[0], out Guid wanted) ||
            (args[0].Length == 32 && Guid.TryParseExact(args[0], "N", out wanted))) {
            guid = wanted;
            try { (path, _, _) = ResolveReference(root, args[0]); }
            catch { path = null; } // searching for a dangling guid is the point
        }
        else {
            (path, guid, _) = ResolveReference(root, args[0]);
        }

        string guidNeedle = "guid:" + guid.ToString("N");
        var referencedBy = new List<object>();
        int total = 0;

        foreach (string file in EnumerateRefBearingFiles(root)) {
            string text;
            try { text = File.ReadAllText(file); }
            catch { continue; }

            int guidHits = CountOccurrences(text, guidNeedle);
            int pathHits = path is null ? 0 : CountOccurrences(text, path);
            if (guidHits + pathHits == 0) continue;

            string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            // Skip the asset file itself; everything else (including generated siblings) counts.
            if (string.Equals(rel, path, StringComparison.OrdinalIgnoreCase)) continue;

            var via = new List<string>();
            if (guidHits > 0) via.Add("guid");
            if (pathHits > 0) via.Add("path");
            referencedBy.Add(new { file = rel, hits = guidHits + pathHits, via });
            total += guidHits + pathHits;
        }

        Json.Write(new {
            asset = new { path, guid = guid.ToString("N") },
            count = referencedBy.Count,
            totalHits = total,
            referencedBy,
        });
        return 0;
    }

    static int CountOccurrences(string text, string needle) {
        int count = 0;
        for (int i = text.IndexOf(needle, StringComparison.OrdinalIgnoreCase); i >= 0;
             i = text.IndexOf(needle, i + needle.Length, StringComparison.OrdinalIgnoreCase))
            count++;
        return count;
    }

    static IEnumerable<string> EnumerateRefBearingFiles(string root) {
        string assetsRoot = Path.Combine(root, "Assets");
        if (Directory.Exists(assetsRoot))
            foreach (string file in Directory.EnumerateFiles(assetsRoot, "*", SearchOption.AllDirectories))
                if (RefBearingExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                    yield return file;
        string manifest = Path.Combine(root, "project.json");
        if (File.Exists(manifest))
            yield return manifest;
    }

    // ---- list -----------------------------------------------------------------

    static int List(string root, string[] args) {
        string? ext = null, folder = null;
        for (int i = 0; i < args.Length; i++) {
            switch (args[i]) {
                case "--ext": ext = Next(args, ref i, "--ext"); break;
                case "--folder": folder = Next(args, ref i, "--folder")!.Replace('\\', '/').TrimEnd('/'); break;
                default: throw new CliUsageException($"unexpected argument '{args[i]}'");
            }
        }
        if (ext is not null && !ext.StartsWith('.')) ext = "." + ext;

        var assets = new List<object>();
        foreach ((string path, MetaFile meta) in EnumerateMetas(root)) {
            if (ext is not null && !path.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) continue;
            if (folder is not null && !path.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase)) continue;
            assets.Add(new { path, guid = meta.Guid.ToString("N"), importer = meta.Importer });
        }

        Json.Write(new { count = assets.Count, assets });
        return 0;
    }

    // ---- shared (MapCommand also scans the sidecars) ---------------------------

    internal static IEnumerable<(string assetPath, MetaFile meta)> EnumerateMetas(string root) {
        string assetsRoot = Path.Combine(root, "Assets");
        if (!Directory.Exists(assetsRoot)) yield break;
        foreach (string metaPath in Directory.EnumerateFiles(assetsRoot, "*.meta", SearchOption.AllDirectories)) {
            MetaFile? meta;
            try { meta = MetaFile.Load(metaPath); }
            catch { continue; } // a corrupt sidecar shouldn't kill a query; validate flags those
            if (meta is null) continue;
            string rel = Path.GetRelativePath(root, metaPath[..^".meta".Length]).Replace('\\', '/');
            yield return (rel, meta);
        }
    }

    static string Next(string[] args, ref int i, string flag) =>
        ++i < args.Length ? args[i] : throw new CliUsageException($"{flag} needs a value");
}
