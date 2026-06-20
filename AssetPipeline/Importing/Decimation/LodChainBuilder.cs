using System;
using System.Collections.Generic;
using System.Numerics;

namespace BallisticEngine.AssetPipeline.Importing.Decimation;

// Builds a geometric LOD chain for a mesh by decimating EACH submesh independently and packing every level into
// the mesh's ONE shared index buffer.
//
// Layout (the render contract): the vertex buffer is untouched (decimation is index-only, BaseVertexLocation=0).
// The index buffer keeps every submesh's LOD0 range exactly where it was (IndexStart/IndexCount preserved →
// pre-LOD artifacts stay byte-identical), then ALL the extra LOD ranges are APPENDED after the original indices.
// Each submesh gets a Lods[] = { LOD0=(IndexStart,IndexCount), LOD1=(appendedOffset,count), ... }.
//
// Per-submesh (not whole-mesh) decimation is mandatory: split-by-nodes import draws each submesh as its own
// entity (SubMeshIndex), and the GPU-driven material table stamps per submesh — collapsing across submesh
// boundaries would destroy that partition. QuadricDecimator pins boundary/seam vertices so submesh borders and
// UV islands never tear.
public static class LodChainBuilder {
    public readonly struct Settings {
        public readonly int LodCount;        // total levels incl. LOD0 (e.g. 4 → LOD0..3)
        public readonly float Reduction;     // triangle multiplier per level (0.5 → 50/25/12.5%)
        public readonly int MinTris;         // submeshes at/below this stay full-detail at every level
        public Settings(int lodCount, float reduction, int minTris) {
            LodCount = Math.Max(1, lodCount); Reduction = reduction; MinTris = Math.Max(2, minTris);
        }
        public static Settings Default => new(4, 0.5f, 64);
    }

    // Returns a MeshData identical to `mesh` except the index buffer is extended with the extra LOD ranges and
    // each SubMeshData carries a Lods[] table. LodCount<=1 (or every submesh too small) returns `mesh` UNCHANGED
    // (the importer then writes a v6-equivalent, byte-identical artifact). Skinned meshes are returned unchanged
    // (skinned LOD is a follow-up — the bone-influence transfer needs its own care).
    public static MeshData Build(in MeshData mesh, Settings s) {
        if (s.LodCount <= 1 || !mesh.IsValid || mesh.IsSkinned) return mesh;

        Vector3[] positions = mesh.Vertices;
        SubMeshData[] src = mesh.SubMeshes;
        var newSubs = new SubMeshData[src.Length];
        // Start the appended region right after the original index buffer; LOD0 ranges keep their place.
        var extra = new List<uint>();
        int appendBase = mesh.Indices.Length;
        bool anyLod = false;

        for (int si = 0; si < src.Length; si++) {
            SubMeshData sub = src[si];
            int triCount = sub.IndexCount / 3;
            var lods = new List<LodRange>(s.LodCount) { new LodRange(sub.IndexStart, sub.IndexCount) };

            if (triCount > s.MinTris) {
                // Copy this submesh's LOD0 index range out (absolute vertex indices, shared vertex buffer).
                uint[] baseIdx = new uint[sub.IndexCount];
                Array.Copy(mesh.Indices, sub.IndexStart, baseIdx, 0, sub.IndexCount);

                float ratio = 1f;
                for (int lvl = 1; lvl < s.LodCount; lvl++) {
                    ratio *= s.Reduction;
                    uint[] decimated = QuadricDecimator.Simplify(positions, baseIdx, ratio);
                    // Stop adding levels once decimation can't reduce further (tiny submesh / fully collapsed).
                    if (decimated.Length >= baseIdx.Length || decimated.Length < 3) break;
                    int offset = appendBase + extra.Count;
                    extra.AddRange(decimated);
                    lods.Add(new LodRange(offset, decimated.Length));
                    anyLod = true;
                }
            }

            newSubs[si] = sub.WithLods(lods.Count > 1 ? lods.ToArray() : null);
        }

        if (!anyLod) return mesh;   // nothing decimated → leave the mesh (and its artifact) untouched

        // Extend the shared index buffer: original + appended LOD ranges.
        uint[] newIndices = new uint[mesh.Indices.Length + extra.Count];
        Array.Copy(mesh.Indices, newIndices, mesh.Indices.Length);
        for (int i = 0; i < extra.Count; i++) newIndices[mesh.Indices.Length + i] = extra[i];

        return new MeshData(mesh.Vertices, newIndices, mesh.UVs, mesh.Normals, mesh.Tangents,
            newSubs, mesh.Nodes);
    }
}
