using System.Text;

namespace BallisticEngine.AssetPipeline;

// A mountable content archive (.pak): one file holding many logical entries (artifacts, scenes,
// materials, ...). The build packs a project's shipped content into content.pak; the player mounts
// it and reads entries by logical path WITHOUT the loose files existing on disk. Designed for the
// future: a patch / DLC / streamed level is just ANOTHER .pak mounted on top (last mount wins per
// path — see ContentMount), so content updates never touch the exe.
//
// Format (little-endian):
//   "BPAK" (4 bytes) | int version | int entryCount
//   entryCount x:  ushort pathByteLen | path (UTF-8) | long offset | long length
//   blob region:   concatenated entry bytes (offset/length point in here, from blob region start)
//
// Logical paths use forward slashes and are project-relative-ish, mirroring how the engine refers to
// content: "Library/Artifacts/<guid>.bmesh", "Assets/Levels/Main.scene", "Library/ArtifactDB.json".
// The index is read fully into memory on mount (a few hundred KB even for thousands of entries); blob
// bytes are read lazily per request so a huge pack doesn't load into RAM up front (streaming-friendly).
public sealed class ContentPack : IDisposable {
    const uint Magic = 0x4B415042;   // "BPAK"
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

    // Opens a pack for reading: parses the index, keeps the stream open for lazy blob reads.
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

    // Reads an entry's bytes. Thread-safe (seeks under a lock — the stream is shared). Returns null
    // if the path isn't in this pack.
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

    // ---- writing (build time) ----------------------------------------------

    // Packs (logicalPath -> source absolute file) into one .pak. Streams each source file straight
    // into the blob region so packing a multi-GB project never holds it all in memory.
    public static void Write(string packPath, IEnumerable<(string LogicalPath, string SourceFile)> items) {
        // Materialize so we can write the index first (needs offsets) then the blobs.
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

        // Index: offsets are relative to the blob region start (which begins right after the index).
        long offset = 0;
        foreach (var it in list) {
            var pathBytes = Encoding.UTF8.GetBytes(it.Logical);
            writer.Write((ushort)pathBytes.Length);
            writer.Write(pathBytes);
            writer.Write(offset);
            writer.Write(it.Length);
            offset += it.Length;
        }

        // Blobs.
        foreach (var it in list) {
            using var src = new FileStream(it.SourceFile, FileMode.Open, FileAccess.Read, FileShare.Read);
            src.CopyTo(fs);
        }
    }
}
