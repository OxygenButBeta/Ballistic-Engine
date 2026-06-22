using System.Text;

namespace BallisticEngine.AssetPipeline;

public sealed class ContentPack : IDisposable {
    const uint Magic = 0x4B415042;
    const int Version = 1;

    public sealed record Entry(long Offset, long Length);

    readonly Dictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);
    readonly FileStream stream;
    readonly long blobStart;
    readonly object readGate = new();

    public string Path { get; }
    public IReadOnlyDictionary<string, Entry> Entries => entries;

    ContentPack(string path, FileStream stream, Dictionary<string, Entry> entries, long blobStart) {
        Path = path;
        this.stream = stream;
        this.entries = entries;
        this.blobStart = blobStart;
    }

    public static ContentPack Open(string path) {
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var reader = new BinaryReader(fs, Encoding.UTF8, leaveOpen: true);

        if (reader.ReadUInt32() != Magic)
            throw new InvalidDataException($"'{path}' is not a Ballistic content pack (bad magic).");
        int version = reader.ReadInt32();
        if (version != Version)
            throw new InvalidDataException($"'{path}' pack version {version} unsupported (expected {Version}).");

        int count = reader.ReadInt32();
        var entries = new Dictionary<string, ContentPack.Entry>(count, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < count; i++) {
            ushort len = reader.ReadUInt16();
            var pathBytes = reader.ReadBytes(len);
            var entryPath = Encoding.UTF8.GetString(pathBytes);
            long offset = reader.ReadInt64();
            long length = reader.ReadInt64();
            entries[Normalize(entryPath)] = new ContentPack.Entry(offset, length);
        }

        long blobStart = fs.Position;
        return new ContentPack(path, fs, entries, blobStart);
    }

    public bool Contains(string logicalPath) => entries.ContainsKey(Normalize(logicalPath));

    public byte[] Read(string logicalPath) {
        if (!entries.TryGetValue(Normalize(logicalPath), out Entry entry))
            return null;

        var buffer = new byte[entry.Length];
        lock (readGate) {
            stream.Seek(blobStart + entry.Offset, SeekOrigin.Begin);
            int read = 0;
            while (read < buffer.Length) {
                int n = stream.Read(buffer, read, buffer.Length - read);
                if (n == 0) break;
                read += n;
            }
        }
        return buffer;
    }

    public void Dispose() => stream?.Dispose();

    static string Normalize(string path) => path.Replace('\\', '/');

    public static void Write(string packPath, IEnumerable<(string LogicalPath, string SourceFile)> items) {
        var list = items
            .Where(it => File.Exists(it.SourceFile))
            .Select(it => (Logical: Normalize(it.LogicalPath), it.SourceFile,
                           Length: new FileInfo(it.SourceFile).Length))
            .ToList();

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(packPath)!);
        using var fs = new FileStream(packPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: true);

        writer.Write(Magic);
        writer.Write(Version);
        writer.Write(list.Count);

        long offset = 0;
        foreach (var it in list) {
            var pathBytes = Encoding.UTF8.GetBytes(it.Logical);
            writer.Write((ushort)pathBytes.Length);
            writer.Write(pathBytes);
            writer.Write(offset);
            writer.Write(it.Length);
            offset += it.Length;
        }

        foreach (var it in list) {
            using var src = new FileStream(it.SourceFile, FileMode.Open, FileAccess.Read, FileShare.Read);
            src.CopyTo(fs);
        }
    }
}
