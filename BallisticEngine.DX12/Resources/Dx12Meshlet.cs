using System;
using System.Collections.Generic;
using System.Numerics;
using Vortice.Direct3D12;
using BallisticEngine;

namespace BallisticEngine.DX12;

// R4 — meshlet generation + GPU buffers for the mesh-shader pipeline. Splits a mesh's triangle soup into
// meshlets (<=64 unique vertices, <=124 primitives each), with per-meshlet bounds (sphere) + a normal cone for
// backface culling. Built ONCE per mesh (cached by Mesh ref); the amplification shader culls meshlets (frustum +
// cone + Hi-Z) and dispatches the mesh shader only for survivors.
//
// Layout matches MeshletGBuffer.hlsl:
//   Meshlet { uint VertOffset, VertCount, PrimOffset, PrimCount; }  (16B)
//   MeshletBounds { float4 Sphere(xyz=center,w=radius); float4 Cone(xyz=axis,w=-cos(angle)); }  (32B)
//   MeshletVerts: uint[] — global vertex indices, VertOffset..+VertCount per meshlet
//   MeshletPrims: uint[] — packed 3x 8-bit LOCAL vertex indices (within the meshlet) per primitive
internal sealed class Dx12MeshletData {
    public ID3D12Resource Meshlets;        // Meshlet[]
    public ID3D12Resource Bounds;          // MeshletBounds[]
    public ID3D12Resource Verts;           // uint[]
    public ID3D12Resource Prims;           // uint[] (packed local tri indices)
    public int MeshletCount;
}

internal static class Dx12Meshlet {
    const int MaxVerts = 64;
    const int MaxPrims = 124;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct Meshlet { public uint VertOffset, VertCount, PrimOffset, PrimCount; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct MeshletBounds { public Vector4 Sphere; public Vector4 Cone; }

    // Cache per (mesh, submesh) — the renderer draws per submesh, and meshlets are submesh-local so the index
    // ranges line up with the GPU-driven per-submesh meta.
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

        // Greedy build: accumulate triangles into the current meshlet until it would exceed MaxVerts/MaxPrims.
        var localMap = new Dictionary<uint, byte>();   // global vert index -> local index within the meshlet
        var curVerts = new List<uint>();
        var curPrims = new List<uint>();   // packed 3x local
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
            bounds.Add(ComputeBounds(curVerts, verts));
            localMap.Clear(); curVerts.Clear(); curPrims.Clear();
        }

        for (int t = 0; t < triCount; t++) {
            uint a = indices[idxStart + t * 3 + 0];
            uint b = indices[idxStart + t * 3 + 1];
            uint c = indices[idxStart + t * 3 + 2];
            // How many NEW vertices would this tri add to the current meshlet?
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
        if (meshlets.Count == 0) {   // empty submesh — 1-element placeholders so the buffers are valid
            meshlets.Add(default); bounds.Add(default); vertList.Add(0); primList.Add(0);
        }
        data.Meshlets = Upload(dev, meshlets.ToArray());
        data.Bounds = Upload(dev, bounds.ToArray());
        data.Verts = Upload(dev, vertList.ToArray());
        data.Prims = Upload(dev, primList.ToArray());
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

    static MeshletBounds ComputeBounds(List<uint> vertIdx, Vector3[] verts) {
        // Bounding sphere (centroid + max radius) — coarse but fine for frustum cull. Cone left neutral (w=-1 =>
        // never backface-culled) for now; a proper normal cone is a follow-up (needs per-vertex normals).
        Vector3 c = Vector3.Zero;
        for (int i = 0; i < vertIdx.Count; i++) c += verts[vertIdx[i]];
        c /= Math.Max(vertIdx.Count, 1);
        float r = 0;
        for (int i = 0; i < vertIdx.Count; i++) r = MathF.Max(r, Vector3.Distance(c, verts[vertIdx[i]]));
        return new MeshletBounds { Sphere = new Vector4(c, r), Cone = new Vector4(0, 0, 0, -1) };
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
