using Vortice.Direct3D12;

namespace BallisticEngine.DX12;

internal static class Dx12Meshlet {
    const int MaxVerts = 64;
    const int MaxPrims = 124;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct Meshlet { public uint VertOffset, VertCount, PrimOffset, PrimCount; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct MeshletBounds { public Vector4 Sphere; public Vector4 Cone; }

    static readonly Dictionary<(Mesh, int), Dx12MeshletData> cache = new();

    public static Dx12MeshletData Build(Dx12Device dev, Mesh mesh, int submesh) {
        var key = (mesh, submesh);
        if (cache.TryGetValue(key, out var cached)) return cached;

        uint[] indices = mesh.Indices;
        Vector3[] verts = mesh.Vertices;
        SubMeshData sub = mesh.SubMeshes[submesh];
        int idxStart = sub.IndexStart, idxCount = sub.IndexCount;

        var meshlets = new List<Meshlet>();
        var bounds = new List<MeshletBounds>();
        var vertList = new List<uint>();
        var primList = new List<uint>();

        var localMap = new Dictionary<uint, byte>();
        var curVerts = new List<uint>();
        var curPrims = new List<uint>();
        int triCount = idxCount / 3;

        void Flush() {
            if (curPrims.Count == 0) return;
            int vOff = vertList.Count, pOff = primList.Count;
            vertList.AddRange(curVerts);
            primList.AddRange(curPrims);
            meshlets.Add(new Meshlet {
                VertOffset = (uint)vOff, VertCount = (uint)curVerts.Count,
                PrimOffset = (uint)pOff, PrimCount = (uint)curPrims.Count,
            });
            bounds.Add(ComputeBounds(curVerts, curPrims, verts));
            localMap.Clear(); curVerts.Clear(); curPrims.Clear();
        }

        for (int t = 0; t < triCount; t++) {
            uint a = indices[idxStart + t * 3 + 0];
            uint b = indices[idxStart + t * 3 + 1];
            uint c = indices[idxStart + t * 3 + 2];
            int newV = 0;
            if (!localMap.ContainsKey(a)) newV++;
            if (!localMap.ContainsKey(b) && b != a) newV++;
            if (!localMap.ContainsKey(c) && c != a && c != b) newV++;
            if (curVerts.Count + newV > MaxVerts || curPrims.Count >= MaxPrims) Flush();
            byte La = Local(a, localMap, curVerts);
            byte Lb = Local(b, localMap, curVerts);
            byte Lc = Local(c, localMap, curVerts);
            curPrims.Add((uint)(La | (Lb << 8) | (Lc << 16)));
        }
        Flush();

        var data = new Dx12MeshletData { MeshletCount = meshlets.Count };
        if (meshlets.Count == 0) {
            meshlets.Add(default); bounds.Add(default); vertList.Add(0); primList.Add(0);
        }
        data.Meshlets = Upload(dev, meshlets.ToArray());
        data.Bounds = Upload(dev, bounds.ToArray());
        data.Verts = Upload(dev, vertList.ToArray());
        data.Prims = Upload(dev, primList.ToArray());
        data.VertCount = vertList.Count;
        data.PrimCount = primList.Count;
        cache[key] = data;
        return data;
    }

    static byte Local(uint global, Dictionary<uint, byte> map, List<uint> curVerts) {
        if (map.TryGetValue(global, out byte l)) return l;
        l = (byte)curVerts.Count;
        map[global] = l;
        curVerts.Add(global);
        return l;
    }

    static MeshletBounds ComputeBounds(List<uint> vertIdx, List<uint> prims, Vector3[] verts) {
        Vector3 c = Vector3.Zero;
        for (int i = 0; i < vertIdx.Count; i++) c += verts[vertIdx[i]];
        c /= Math.Max(vertIdx.Count, 1);
        float r = 0;
        for (int i = 0; i < vertIdx.Count; i++) r = MathF.Max(r, Vector3.Distance(c, verts[vertIdx[i]]));

        Vector3 axis = Vector3.Zero;
        int triN = prims.Count;
        var faceNormals = new Vector3[triN];
        for (int t = 0; t < triN; t++) {
            uint packed = prims[t];
            Vector3 p0 = verts[vertIdx[(int)(packed & 0xFF)]];
            Vector3 p1 = verts[vertIdx[(int)((packed >> 8) & 0xFF)]];
            Vector3 p2 = verts[vertIdx[(int)((packed >> 16) & 0xFF)]];
            Vector3 n = Vector3.Cross(p1 - p0, p2 - p0);
            float len = n.Length();
            n = len > 1e-12f ? n / len : Vector3.UnitY;
            faceNormals[t] = n;
            axis += n;
        }
        float al = axis.Length();
        Vector4 cone;
        if (triN == 0 || al < 1e-6f) cone = new Vector4(0, 0, 0, -1);
        else {
            axis /= al;
            float minDot = 1f;
            for (int t = 0; t < triN; t++) minDot = MathF.Min(minDot, Vector3.Dot(axis, faceNormals[t]));
            cone = minDot <= 0f ? new Vector4(0, 0, 0, -1) : new Vector4(axis, minDot);
        }
        return new MeshletBounds { Sphere = new Vector4(c, r), Cone = cone };
    }

    static unsafe ID3D12Resource Upload<T>(Dx12Device dev, T[] data) where T : unmanaged =>
        dev.CreateDefaultBuffer<T>(data, ResourceStates.NonPixelShaderResource);

    public static void Clear() {
        foreach (var d in cache.Values) {
            d.Meshlets?.Dispose(); d.Bounds?.Dispose(); d.Verts?.Dispose(); d.Prims?.Dispose();
        }
        cache.Clear();
    }
}
