using System.Collections.Concurrent;

namespace BallisticEngine.AssetPipeline.Loaders;

public static class AssetDataCache {
    static readonly ConcurrentDictionary<Guid, MeshData> meshes = new();
    static readonly ConcurrentDictionary<Guid, TextureData> textures = new();
    static long residentBytes;

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
