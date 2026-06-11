using System.Collections.Concurrent;

namespace BallisticEngine.AssetPipeline.Loaders;

// A short-lived cache of DECODED CPU artifact data (MeshData / TextureData), keyed by asset GUID.
//
// Scene loading uploads meshes and textures to the GPU, which must happen on the render thread —
// but the expensive part is reading the .bmesh/.btex off disk and (for textures) inflating the
// Deflate payload, which is pure CPU work. The scene prefetcher decodes referenced artifacts on
// worker threads and stuffs the results here; then the main-thread loaders take the warm data and
// only do the cheap GL upload, so opening a heavy scene no longer freezes the window.
//
// Entries are CONSUMED on read (TryTakeXxx removes them): the CPU copy exists only to bridge the
// worker decode to the next main-thread upload, so we drop it immediately to avoid holding a second
// full copy of every mesh/texture in memory. A cache miss is harmless — the loader just decodes
// synchronously as before.
public static class AssetDataCache {
    static readonly ConcurrentDictionary<Guid, MeshData> meshes = new();
    static readonly ConcurrentDictionary<Guid, TextureData> textures = new();
    static long residentBytes;

    // Decoded CPU bytes currently held. The prefetcher checks this against its memory budget —
    // a heavy scene's textures can decode to many GB of raw RGBA8, far more than the compressed
    // artifacts suggest, and the cache holds everything between prefetch end and apply.
    public static long ResidentBytes => Interlocked.Read(ref residentBytes);

    public static void PutMesh(Guid guid, in MeshData data) {
        meshes[guid] = data;
        Interlocked.Add(ref residentBytes, SizeOf(in data));
    }

    public static void PutTexture(Guid guid, in TextureData data) {
        textures[guid] = data;
        Interlocked.Add(ref residentBytes, data.Pixels?.LongLength ?? 0);
    }

    public static bool TryTakeMesh(Guid guid, out MeshData data) {
        if (!meshes.TryRemove(guid, out data))
            return false;
        Interlocked.Add(ref residentBytes, -SizeOf(in data));
        return true;
    }

    public static bool TryTakeTexture(Guid guid, out TextureData data) {
        if (!textures.TryRemove(guid, out data))
            return false;
        Interlocked.Add(ref residentBytes, -(data.Pixels?.LongLength ?? 0));
        return true;
    }

    public static bool HasMesh(Guid guid) => meshes.ContainsKey(guid);
    public static bool HasTexture(Guid guid) => textures.ContainsKey(guid);

    // Drops anything left over (e.g. a prefetched asset the scene ended up not loading) so stale CPU
    // data isn't held across loads.
    public static void Clear() {
        meshes.Clear();
        textures.Clear();
        Interlocked.Exchange(ref residentBytes, 0);
    }

    static long SizeOf(in MeshData data) =>
        (data.Vertices?.LongLength ?? 0) * 12 + (data.Normals?.LongLength ?? 0) * 12 +
        (data.Tangents?.LongLength ?? 0) * 16 + (data.UVs?.LongLength ?? 0) * 8 +
        (data.Indices?.LongLength ?? 0) * 4;
}
