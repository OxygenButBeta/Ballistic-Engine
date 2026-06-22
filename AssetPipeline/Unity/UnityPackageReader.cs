using System.Formats.Tar;
using System.IO.Compression;

namespace BallisticEngine.AssetPipeline.Unity;

public static class UnityPackageReader {
    public sealed class Entry {
        public string Guid;
        public string PathName;
        public byte[] Asset;
        public byte[] Meta;
    }

    public sealed class Result {
        public readonly List<string> ExtractedFiles = new();
        public readonly List<string> Scenes = new();
        public readonly List<string> Prefabs = new();
        public int FolderCount;
        public int SkippedFiles;
    }

    static readonly HashSet<string> ExcludedExtensions = new(StringComparer.OrdinalIgnoreCase) {
        ".cs", ".asmdef", ".asmref", ".rsp", ".dll", ".pdb", ".mdb",
        ".shader", ".cginc", ".hlsl", ".compute", ".shadergraph", ".shadersubgraph", ".uxml", ".uss",
        ".inputactions", ".unitypackage", ".dummy",
    };

    static bool IsExcluded(string pathName) {
        var ext = Path.GetExtension(pathName).ToLowerInvariant();
        if (ExcludedExtensions.Contains(ext))
            return true;
        var p = pathName.Replace('\\', '/');
        return p.Contains("/Editor/", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith("ProjectSettings/", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase);
    }

    public static List<Entry> Read(string packageAbsolutePath) {
        using FileStream file = File.OpenRead(packageAbsolutePath);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);

        var byGuid = new Dictionary<string, Entry>(StringComparer.Ordinal);

        while (tar.GetNextEntry() is { } member) {
            var name = member.Name.Replace('\\', '/').TrimStart('.', '/');
            var slash = name.IndexOf('/');
            if (slash <= 0)
                continue;

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
                    var text = System.Text.Encoding.UTF8.GetString(ReadBytes(member) ?? []);
                    entry.PathName = text.Split('\n', '\r')[0].Trim();
                    break;
            }
        }

        return [.. byGuid.Values];
    }

    public static Result Extract(string packageAbsolutePath, string destinationDir) {
        var result = new Result();
        var tempDir = Path.Combine(destinationDir, ".bal_unpack");
        Directory.CreateDirectory(tempDir);

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
                                    member.DataStream.CopyTo(outFile);
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

            var destFull = Path.GetFullPath(destinationDir);
            foreach ((var guid, var pathName) in pathByGuid) {
                if (string.IsNullOrWhiteSpace(pathName))
                    continue;

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
            catch {
            }
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
