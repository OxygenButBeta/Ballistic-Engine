using System.Runtime.InteropServices;
using BallisticEngine.GI;

namespace BallisticEngine.AssetPipeline;

// Engine-native baked mesh-SDF, Library\Artifacts\<guid>.bsdf — a SIBLING of the model's .bmesh
// (same guid stem). The model importer doesn't own a second ArtifactExtension, so the SDF is a
// co-artifact resolved by guid rather than a tracked ArtifactDatabase entry; it is (re)baked lazily
// and cached, keyed by the .bmesh content so a stale .bsdf is detected and rebuilt.
//
// Layout:
//   u32 magic 'BSDF' | u32 version | i64 sourceStamp
//   i32 resX resY resZ
//   vec3 boundsMin | vec3 boundsMax
//   f32 distances[resX*resY*resZ]   (x-fastest, mesh-local units)
//
// `sourceStamp` is a cheap fingerprint of the mesh the field was baked from (vertex+index counts
// folded together) so a reimport that changes the geometry invalidates the cached field.
public static class SdfArtifact {
    const uint Magic = 0x46445342; // "BSDF"
    const uint FormatVersion = 1;

    public static void Write(string path, MeshSdf sdf, long sourceStamp) {
        using FileStream stream = File.Create(path);
        using BinaryWriter writer = new(stream);
        writer.Write(Magic);
        writer.Write(FormatVersion);
        writer.Write(sourceStamp);
        writer.Write(sdf.Res.X);
        writer.Write(sdf.Res.Y);
        writer.Write(sdf.Res.Z);
        WriteVec3(writer, sdf.BoundsMin);
        WriteVec3(writer, sdf.BoundsMax);
        writer.Write(MemoryMarshal.AsBytes<float>(sdf.Distances));
    }

    // Reads a .bsdf. Returns null (rather than throwing) on a missing file, bad magic/version, or a
    // sourceStamp mismatch — every one of those means "no usable cached field, rebake".
    public static MeshSdf Read(string path, long expectedStamp) {
        if (!File.Exists(path))
            return null;
        try {
            using FileStream stream = File.OpenRead(path);
            using BinaryReader reader = new(stream);
            if (reader.ReadUInt32() != Magic) return null;
            if (reader.ReadUInt32() != FormatVersion) return null;
            if (reader.ReadInt64() != expectedStamp) return null;
            var res = new Vector3i(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
            Vector3 min = ReadVec3(reader), max = ReadVec3(reader);
            int n = res.X * res.Y * res.Z;
            if (n <= 0 || n > 64 * 1024 * 1024) return null; // sanity guard
            var distances = new float[n];
            reader.BaseStream.ReadExactly(MemoryMarshal.AsBytes<float>(distances));
            return new MeshSdf(res, min, max, distances);
        }
        catch {
            return null; // corrupt/truncated cache — rebake
        }
    }

    // A cheap geometry fingerprint: enough to catch a reimport that changes the mesh, without
    // hashing every vertex. Counts plus a few sampled vertex bits.
    public static long StampFor(in MeshData mesh) {
        long stamp = 1469598103934665603L; // FNV offset basis
        void Fold(long v) { stamp = (stamp ^ v) * 1099511628211L; }
        Fold(mesh.Vertices.Length);
        Fold(mesh.Indices.Length);
        int step = Math.Max(1, mesh.Vertices.Length / 64);
        for (int i = 0; i < mesh.Vertices.Length; i += step) {
            Vector3 v = mesh.Vertices[i];
            Fold(BitConverter.SingleToInt32Bits(v.X));
            Fold(BitConverter.SingleToInt32Bits(v.Y));
            Fold(BitConverter.SingleToInt32Bits(v.Z));
        }
        return stamp;
    }

    static void WriteVec3(BinaryWriter w, Vector3 v) {
        w.Write(v.X); w.Write(v.Y); w.Write(v.Z);
    }
    static Vector3 ReadVec3(BinaryReader r) => new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
}
