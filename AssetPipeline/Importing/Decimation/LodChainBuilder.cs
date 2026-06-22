namespace BallisticEngine.AssetPipeline.Importing.Decimation;

public static class LodChainBuilder {
    public readonly struct Settings {
        public readonly int LodCount;
        public readonly float Reduction;
        public readonly int MinTris;

        public Settings(int lodCount, float reduction, int minTris) {
            LodCount = Math.Max(1, lodCount); Reduction = reduction; MinTris = Math.Max(2, minTris);
        }
        public static Settings Default => new(4, 0.5f, 64);
    }

    public static MeshData Build(in MeshData mesh, Settings s) {
        if (s.LodCount <= 1 || !mesh.IsValid || mesh.IsSkinned) return mesh;

        Vector3[] positions = mesh.Vertices;
        SubMeshData[] src = mesh.SubMeshes;
        var newSubs = new SubMeshData[src.Length];
        var extra = new List<uint>();
        int appendBase = mesh.Indices.Length;
        bool anyLod = false;

        for (int si = 0; si < src.Length; si++) {
            SubMeshData sub = src[si];
            int triCount = sub.IndexCount / 3;
            var lods = new List<LodRange>(s.LodCount) { new LodRange(sub.IndexStart, sub.IndexCount) };

            if (triCount > s.MinTris) {
                uint[] baseIdx = new uint[sub.IndexCount];
                Array.Copy(mesh.Indices, sub.IndexStart, baseIdx, 0, sub.IndexCount);

                float ratio = 1f;
                for (int lvl = 1; lvl < s.LodCount; lvl++) {
                    ratio *= s.Reduction;
                    uint[] decimated = QuadricDecimator.Simplify(positions, baseIdx, ratio);
                    if (decimated.Length >= baseIdx.Length || decimated.Length < 3) break;
                    int offset = appendBase + extra.Count;
                    extra.AddRange(decimated);
                    lods.Add(new LodRange(offset, decimated.Length));
                    anyLod = true;
                }
            }

            newSubs[si] = sub.WithLods(lods.Count > 1 ? lods.ToArray() : null);
        }

        if (!anyLod) return mesh;

        uint[] newIndices = new uint[mesh.Indices.Length + extra.Count];
        Array.Copy(mesh.Indices, newIndices, mesh.Indices.Length);
        for (int i = 0; i < extra.Count; i++) newIndices[mesh.Indices.Length + i] = extra[i];

        return new MeshData(mesh.Vertices, newIndices, mesh.UVs, mesh.Normals, mesh.Tangents,
            newSubs, mesh.Nodes);
    }
}
