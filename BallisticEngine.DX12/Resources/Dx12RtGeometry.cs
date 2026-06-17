using System;
using System.Collections.Generic;
using System.Numerics;
using Vortice.Direct3D12;
using Vortice.DXGI;
using GLVector3 = System.Numerics.Vector3;

namespace BallisticEngine.DX12;

// Per-instance geometry attributes for the DXR hit shaders (RT-GI P1, reused by RT reflections + the future
// surface cache). The BLAS carries only positions, so a closest-hit shader can't see normals/UVs/material.
// This exposes, for every TLAS instance (same iteration order as Dx12SceneAS, so InstanceID() lines up):
//   - the mesh's INDEX buffer       (typed R32_UInt SRV)   → fetch the 3 indices of PrimitiveIndex()
//   - the mesh's NORMAL buffer       (StructuredBuffer<float3>) → interpolate the smooth shading normal
//   - the mesh's UV buffer           (StructuredBuffer<float2>) → interpolate the texcoord for albedo
//   - a PER-TRIANGLE MaterialId buf  (StructuredBuffer<uint>)   → which GpuMaterials[] entry shades this tri
// all registered in the shared bindless heap (Dx12Backend.BindlessHeap); the hit shader reads
// ResourceDescriptorHeap[idx]. A per-instance record {NormalIdx,UvIdx,IndexIdx,TriMatIdx} (root SRV) is
// indexed by InstanceID(). The per-triangle MaterialId resolves the SAME id GBufferBindless uses (from the
// GPU-driven renderer's Material→id map), so RT hit shading decodes the material byte-identically to raster.
//
// Cached by a (instance-set + material-table) stamp: a static scene builds once. Rebuilt with the bindless
// heap (it lives in the same heap the GPU-driven material table resets), so EnsureMaterialTable runs FIRST.
public sealed class Dx12RtGeometry : IDisposable {
    readonly Dx12Device dev;

    // One record per TLAS instance — the 4 bindless indices the hit shader needs. Matches HLSL RtInstance.
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct RtInstance { public uint NormalIdx, UvIdx, IndexIdx, TriMatIdx; }

    // Per-unique-mesh bindless indices (so two instances of one mesh share the SRVs + tri-material buffer).
    sealed class MeshEntry { public int NormalIdx, UvIdx, IndexIdx, TriMatIdx; public ID3D12Resource TriMatBuf; }
    readonly Dictionary<Mesh, MeshEntry> byMesh = new();

    ID3D12Resource instanceBuf;     // RtInstance[] — root SRV indexed by InstanceID()
    public ulong InstancesGpuAddress => instanceBuf?.GPUVirtualAddress ?? 0;
    public int InstanceCount { get; private set; }
    public bool Valid => instanceBuf != null && InstanceCount > 0;

    int stamp = -1;

    public Dx12RtGeometry(Dx12Device device) { dev = device; }

    // Rebuild per-instance geometry records if the instance set or the material table changed. `gpu` supplies
    // the Material→id map (byte-identical to GBufferBindless). MUST run AFTER gpu.EnsureMaterialTable (which
    // resets the bindless heap our SRVs live in).
    public unsafe void Ensure(IEnumerable<IStaticMeshRenderer> renderers, Dx12GpuDrivenRenderer gpu) {
        var insts = new List<(Mesh mesh, IStaticMeshRenderer r)>();
        var h = new HashCode();
        h.Add(gpu.MaterialTableStamp);   // a new material set means baked tri-materials are stale
        foreach (IStaticMeshRenderer r in renderers) {
            if (r is null || !r.IsActive || !r.IsRenderable) continue;
            Mesh mesh = r.SharedMesh;
            if (mesh?.VertexBuffer is not Dx12Buffer<GLVector3> || mesh.IndexBuffer is not Dx12IndexBuffer ib || ib.Resource is null)
                continue;
            if (mesh.NormalBuffer is not Dx12Buffer<GLVector3> nb || nb.Resource is null) continue;
            insts.Add((mesh, r));
            h.Add(mesh.GetHashCode());
        }
        int s = h.ToHashCode();
        if (s == stamp && instanceBuf != null) return;
        stamp = s;
        Rebuild(insts, gpu);
    }

    unsafe void Rebuild(List<(Mesh mesh, IStaticMeshRenderer r)> insts, Dx12GpuDrivenRenderer gpu) {
        // The bindless heap was reset by EnsureMaterialTable this frame (on a material change) — our cached
        // SRV indices are invalid, so drop the per-mesh cache and re-register. Free the old tri-material bufs.
        foreach (MeshEntry e in byMesh.Values) e.TriMatBuf?.Dispose();
        byMesh.Clear();

        var records = new RtInstance[insts.Count];
        for (int i = 0; i < insts.Count; i++) {
            var (mesh, r) = insts[i];
            MeshEntry e = EntryFor(mesh, r, gpu);
            records[i] = new RtInstance {
                NormalIdx = (uint)e.NormalIdx, UvIdx = (uint)e.UvIdx,
                IndexIdx = (uint)e.IndexIdx, TriMatIdx = (uint)e.TriMatIdx,
            };
        }

        instanceBuf?.Dispose();
        InstanceCount = records.Length;
        instanceBuf = records.Length > 0
            ? dev.CreateUavBuffer<RtInstance>(records, ResourceStates.GenericRead)
            : null;
    }

    MeshEntry EntryFor(Mesh mesh, IStaticMeshRenderer r, Dx12GpuDrivenRenderer gpu) {
        if (byMesh.TryGetValue(mesh, out MeshEntry cached)) return cached;

        var ib = (Dx12IndexBuffer)mesh.IndexBuffer;
        var nb = (Dx12Buffer<GLVector3>)mesh.NormalBuffer;
        var ub = mesh.UvBuffer as Dx12Buffer<Vector2>;

        var e = new MeshEntry {
            // Index buffer as a typed R32_UInt buffer SRV (fetch 3 indices of a triangle).
            IndexIdx = RegisterTypedSrv(ib.Resource, Format.R32_UInt, ib.ElementCount),
            // Normal buffer as StructuredBuffer<float3> (12B stride).
            NormalIdx = RegisterStructuredSrv(nb.Resource, nb.ElementCount, 12),
            // UV buffer as StructuredBuffer<float2> (8B). Some meshes may lack UVs → reuse normals as a stand-in
            // (the shader's albedo just samples garbage UVs → BaseColorFactor still tints; acceptable fallback).
            UvIdx = ub?.Resource is not null
                ? RegisterStructuredSrv(ub.Resource, ub.ElementCount, 8)
                : RegisterStructuredSrv(nb.Resource, nb.ElementCount, 12),
        };
        BuildTriMaterials(mesh, r, gpu, out e.TriMatBuf, out e.TriMatIdx);
        byMesh[mesh] = e;
        return e;
    }

    // Per-triangle MaterialId: for each submesh, every triangle in [IndexStart/3, +IndexCount/3) gets the
    // submesh material's GBuffer id, resolved the SAME way the raster G-buffer does — gpu.ResolveOrRegister-
    // MaterialId (R1.0). EnsureMaterialTable registers only WHOLE-MESH (SubMeshIndex<0) renderers, but this
    // build runs for EVERY active renderer the TLAS traces (incl. SubMeshIndex>=0 split-import children).
    // Resolve-or-register makes each RT-traced submesh's material present in the table instead of the old
    // silent `matId=0` fallback, which shaded those triangles with the FIRST whole-mesh material — the cause
    // of wrong/empty RT-GI/emissive/reflection bounce off color-only & split content (the raster G-buffer was
    // correct, the RT trace was not). Transparent/null materials resolve to -1 → triangle stays id 0 (the
    // bounce off a transparent surface is negligible, and the raster path skips it too).
    unsafe void BuildTriMaterials(Mesh mesh, IStaticMeshRenderer r, Dx12GpuDrivenRenderer gpu,
                                  out ID3D12Resource buf, out int bindlessIdx) {
        int triCount = mesh.IndexBuffer.ElementCount / 3;
        var triMat = new uint[Math.Max(triCount, 1)];
        for (int sm = 0; sm < mesh.SubMeshes.Length; sm++) {
            SubMeshData sub = mesh.SubMeshes[sm];
            if (sub.IndexCount <= 0) continue;
            int matId = gpu.ResolveOrRegisterMaterialId(r.MaterialFor(sm));
            if (matId < 0) matId = 0;   // null/transparent/table-full → material 0 (negligible bounce)
            int triStart = sub.IndexStart / 3;
            int triEnd = Math.Min((sub.IndexStart + sub.IndexCount) / 3, triCount);
            for (int t = triStart; t < triEnd; t++) triMat[t] = (uint)matId;
        }
        buf = dev.CreateUavBuffer<uint>(triMat, ResourceStates.GenericRead);
        bindlessIdx = RegisterStructuredSrv(buf, triMat.Length, 4);
    }

    // Register a TYPED buffer SRV in the shared bindless heap; returns the heap index for ResourceDescriptorHeap[].
    int RegisterTypedSrv(ID3D12Resource res, Format format, int elementCount) {
        int idx = Dx12Backend.BindlessHeap.Allocate();
        dev.Device.CreateShaderResourceView(res, new ShaderResourceViewDescription {
            Format = format, ViewDimension = ShaderResourceViewDimension.Buffer,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Buffer = new BufferShaderResourceView {
                FirstElement = 0, NumElements = (uint)elementCount, StructureByteStride = 0,
                Flags = BufferShaderResourceViewFlags.None,
            },
        }, Dx12Backend.BindlessHeap.Cpu(idx));
        return idx;
    }

    // Register a STRUCTURED buffer SRV (StructureByteStride) in the shared bindless heap.
    int RegisterStructuredSrv(ID3D12Resource res, int elementCount, int stride) {
        int idx = Dx12Backend.BindlessHeap.Allocate();
        dev.Device.CreateShaderResourceView(res, new ShaderResourceViewDescription {
            Format = Format.Unknown, ViewDimension = ShaderResourceViewDimension.Buffer,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Buffer = new BufferShaderResourceView {
                FirstElement = 0, NumElements = (uint)elementCount, StructureByteStride = (uint)stride,
                Flags = BufferShaderResourceViewFlags.None,
            },
        }, Dx12Backend.BindlessHeap.Cpu(idx));
        return idx;
    }

    public void Dispose() {
        foreach (MeshEntry e in byMesh.Values) e.TriMatBuf?.Dispose();
        byMesh.Clear();
        instanceBuf?.Dispose(); instanceBuf = null;
    }
}
