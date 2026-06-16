using System.Formats.Tar;
using System.IO.Compression;

namespace BallisticEngine.AssetPipeline.Unity;

// Extracts a .unitypackage (a gzip-compressed tar) into a normal folder tree.
//
// A .unitypackage stores each asset as a directory named by the asset's GUID, containing:
//   <guid>/asset        -- the actual file bytes (absent for folder entries)
//   <guid>/asset.meta   -- Unity's YAML meta (carries the same guid)
//   <guid>/pathname     -- the original project-relative path, e.g. "Assets/Saloon/bar.fbx"
//   <guid>/preview.png  -- thumbnail (ignored)
//
// We rebuild the real path tree under `destinationDir` from each entry's `pathname`, writing the
// asset bytes there and the meta as "<file>.meta" beside it (Unity's own on-disk convention). The
// result is exactly what an unpacked Unity "Assets/" folder looks like, so the YAML parser and
// converter can treat packed and loose imports identically.
public static class UnityPackageReader {
    public sealed class Entry {
        public string Guid;
        public string PathName;     // project-relative, as recorded in the package ("Assets/...")
        public byte[] Asset;        // null for folder-only entries
        public byte[] Meta;         // null if no meta recorded
    }

    public sealed class Result {
        public readonly List<string> ExtractedFiles = new();   // absolute paths of written asset files
        public readonly List<string> Scenes = new();           // absolute .unity paths
        public readonly List<string> Prefabs = new();          // absolute .prefab paths
        public int FolderCount;
        public int SkippedFiles;                               // Unity-only/code files we don't extract
    }

    // Unity-only or code files the engine can't use — and that actively cause harm if extracted.
    // .cs/.asmdef: target the UnityEngine API; the engine would try to compile them as game scripts
    // and every one fails (UnityEngine/MonoBehaviour unknown) -> the project's whole script build
    // fails -> play is blocked / the standalone exits. The rest are Unity-runtime artifacts with no
    // engine meaning. Meshes/textures/materials/.unity/.prefab are kept (the converter uses them).
    static readonly HashSet<string> ExcludedExtensions = new(StringComparer.OrdinalIgnoreCase) {
        ".cs", ".asmdef", ".asmref", ".rsp", ".dll", ".pdb", ".mdb",
        ".shader", ".cginc", ".hlsl", ".compute", ".shadergraph", ".shadersubgraph", ".uxml", ".uss",
        ".inputactions", ".unitypackage", ".dummy",
    };

    static bool IsExcluded(string pathName) {
        var ext = Path.GetExtension(pathName).ToLowerInvariant();
        if (ExcludedExtensions.Contains(ext))
            return true;
        // Unity engine/editor folders that only contain settings or generated junk.
        var p = pathName.Replace('\\', '/');
        return p.Contains("/Editor/", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith("ProjectSettings/", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase);
    }

    // Reads the package into in-memory entries (GUID-keyed). Does NOT touch disk.
    public static List<Entry> Read(string packageAbsolutePath) {
        using FileStream file = File.OpenRead(packageAbsolutePath);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);

        // Collect raw members first; tar entries for one asset (asset / asset.meta / pathname) are
        // adjacent but we key by the GUID directory so order never matters.
        var byGuid = new Dictionary<string, Entry>(StringComparer.Ordinal);

        while (tar.GetNextEntry() is { } member) {
            // Entry name looks like "<guid>/asset" or "./<guid>/pathname". Normalize separators.
            var name = member.Name.Replace('\\', '/').TrimStart('.', '/');
            var slash = name.IndexOf('/');
            if (slash <= 0)
                continue; // top-level, no guid component

            var guid = name[..slash];
            var leaf = name[(slash + 1)..];

            if (!byGuid.TryGetValue(guid, out Entry entry)) {
                entry = new Entry { Guid = guid };
                byGuid[guid] = entry;
            }

            switch (leaf) {
                case "asset":
                    entry.Asset = ReadBytes(member);
                    break;
                case "asset.meta":
                    entry.Meta = ReadBytes(member);
                    break;
                case "pathname":
                    // pathname can carry a trailing "00" asset-origin line; keep only the first line.
                    var text = System.Text.Encoding.UTF8.GetString(ReadBytes(member) ?? []);
                    entry.PathName = text.Split('\n', '\r')[0].Trim();
                    break;
            }
        }

        return [.. byGuid.Values];
    }

    // Materializes the package under destinationDir, rebuilding the original path tree. STREAMS each
    // asset blob straight to its destination file in a single tar pass — never buffering the whole
    // package in RAM (a Megascans scene package is multiple GB; the old buffer-all approach OOM'd /
    // was very slow). The asset blob is the LARGE part; pathname/meta are tiny.
    //
    // The catch: within a guid's tar group, the blob order is asset, asset.meta, pathname — but we
    // can't know the final filename until we've read pathname, which comes AFTER the blob. So we:
    //   1. write each asset blob to a TEMP file named by guid (streamed),
    //   2. stash the tiny meta bytes + pathname per guid,
    //   3. after the pass, MOVE each temp file to its real path and write its .meta.
    // Temp lives under destinationDir/.bal_unpack and is cleaned up.
    public static Result Extract(string packageAbsolutePath, string destinationDir) {
        var result = new Result();
        var tempDir = Path.Combine(destinationDir, ".bal_unpack");
        Directory.CreateDirectory(tempDir);

        // guid -> (pathname, meta bytes, temp blob path). Pathname/meta are small; blob is on disk.
        var pathByGuid = new Dictionary<string, string>(StringComparer.Ordinal);
        var metaByGuid = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var blobByGuid = new Dictionary<string, string>(StringComparer.Ordinal);

        try {
            using (FileStream file = File.OpenRead(packageAbsolutePath))
            using (var gzip = new GZipStream(file, CompressionMode.Decompress))
            using (var tar = new TarReader(gzip)) {
                while (tar.GetNextEntry() is { } member) {
                    var name = member.Name.Replace('\\', '/').TrimStart('.', '/');
                    var slash = name.IndexOf('/');
                    if (slash <= 0)
                        continue;
                    var guid = name[..slash];
                    var leaf = name[(slash + 1)..];

                    switch (leaf) {
                        case "asset":
                            if (member.DataStream is not null) {
                                var blobPath = Path.Combine(tempDir, guid);
                                using (FileStream outFile = File.Create(blobPath))
                                    member.DataStream.CopyTo(outFile);   // streamed, constant memory
                                blobByGuid[guid] = blobPath;
                            }
                            break;
                        case "asset.meta":
                            metaByGuid[guid] = ReadBytes(member);
                            break;
                        case "pathname":
                            var text = System.Text.Encoding.UTF8.GetString(ReadBytes(member) ?? []);
                            pathByGuid[guid] = text.Split('\n', '\r')[0].Trim();
                            break;
                    }
                }
            }

            // Place each guid's blob at its real path; write the meta beside it.
            var destFull = Path.GetFullPath(destinationDir);
            foreach ((var guid, var pathName) in pathByGuid) {
                if (string.IsNullOrWhiteSpace(pathName))
                    continue;

                // Skip Unity-only / code files: their C# targets the UnityEngine API and would BREAK
                // the project's game-script compile (every .cs fails -> PlayBlocked -> nothing runs);
                // asmdefs/shaders/Unity binaries are equally unusable. We keep meshes, textures,
                // materials, scenes and prefabs — everything the converter actually consumes.
                if (IsExcluded(pathName)) {
                    result.SkippedFiles++;
                    continue;
                }

                var relative = pathName;
                const string assetsPrefix = "Assets/";
                if (relative.StartsWith(assetsPrefix, StringComparison.OrdinalIgnoreCase))
                    relative = relative[assetsPrefix.Length..];

                var targetAbsolute = Path.GetFullPath(Path.Combine(destinationDir, relative));
                if (!targetAbsolute.StartsWith(destFull, StringComparison.OrdinalIgnoreCase)) {
                    Debugging.LogWarning($"Unity package: skipping out-of-tree entry '{pathName}'.");
                    continue;
                }

                if (!blobByGuid.TryGetValue(guid, out var blobPath)) {
                    // No asset blob -> folder entry.
                    Directory.CreateDirectory(targetAbsolute);
                    result.FolderCount++;
                    continue;
                }

                try {
                    Directory.CreateDirectory(Path.GetDirectoryName(targetAbsolute)!);
                    if (File.Exists(targetAbsolute)) File.Delete(targetAbsolute);
                    File.Move(blobPath, targetAbsolute);
                    result.ExtractedFiles.Add(targetAbsolute);

                    if (metaByGuid.TryGetValue(guid, out var meta) && meta is not null)
                        File.WriteAllBytes(targetAbsolute + ".meta", meta);

                    var ext = Path.GetExtension(targetAbsolute).ToLowerInvariant();
                    if (ext == ".unity") result.Scenes.Add(targetAbsolute);
                    else if (ext == ".prefab") result.Prefabs.Add(targetAbsolute);
                }
                catch (Exception exception) {
                    Debugging.LogWarning($"Unity package: failed to write '{pathName}': {exception.Message}");
                }
            }
        }
        finally {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); }
            catch { /* leftover temp blobs are harmless */ }
        }

        return result;
    }

    static byte[] ReadBytes(TarEntry member) {
        if (member.DataStream is null)
            return null;
        using var buffer = new MemoryStream();
        member.DataStream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
