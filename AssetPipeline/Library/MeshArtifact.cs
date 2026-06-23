using System.Runtime.InteropServices;

namespace BallisticEngine.AssetPipeline;

public static class MeshArtifact {
    const uint Magic = 0x48534D42;
    const uint FormatVersion = 10;

    public static void Write(string path, in MeshData data) {
        using FileStream stream = File.Create(path);
        using BinaryWriter writer = new(stream);

        bool hasLods = false;
        foreach (SubMeshData sm in data.SubMeshes) if (sm.Lods is { Length: > 1 }) { hasLods = true; break; }
        bool hasSdf = data.Sdf is { IsValid: true };
        bool hasCards = data.Cards is { IsValid: true };
        bool hasSubCards = HasAnySubMeshCards(data);
        // v8 = v7 payload + trailing SDF block; v9 = v8 + trailing CARD block; v10 = v9 + trailing PER-SUBMESH
        // card block. We only stamp the higher version when that block is actually present, so older meshes keep
        // the v6/v7/v8/v9 byte layout (existing readers + diffs unchanged). Per-submesh cards are INDEPENDENT of
        // the whole-mesh card/SDF blocks (a split mesh stores per-submesh cards but no whole-mesh SDF), so v10
        // does NOT require v8/v9 content — the v10 reader still emits the v8/v9 absence flags before it.
        uint writeVersion = hasSubCards ? 10u : hasCards ? 9u : hasSdf ? 8u : hasLods ? 7u : 6u;

        writer.Write(Magic);
        writer.Write(writeVersion);
        writer.Write(data.Vertices.Length);
        writer.Write(data.Indices.Length);
        writer.Write(data.SubMeshes.Length);

        writer.Write(MemoryMarshal.AsBytes<Vector3>(data.Vertices));
        writer.Write(MemoryMarshal.AsBytes<Vector3>(data.Normals));
        writer.Write(MemoryMarshal.AsBytes<Vector4>(data.Tangents));
        writer.Write(MemoryMarshal.AsBytes<Vector2>(data.UVs));
        writer.Write(MemoryMarshal.AsBytes<uint>(data.Indices));

        foreach (SubMeshData subMesh in data.SubMeshes) {
            writer.Write(subMesh.IndexStart);
            writer.Write(subMesh.IndexCount);
            writer.Write(subMesh.Name ?? "");
            writer.Write(subMesh.MaterialRef ?? "");
            WriteMatrix(writer, subMesh.NodeTransform);
            writer.Write(subMesh.NodeIndex);
        }

        MeshNodeData[] nodes = data.Nodes ?? [];
        writer.Write(nodes.Length);
        foreach (MeshNodeData node in nodes) {
            writer.Write(node.Name ?? "");
            writer.Write(node.ParentIndex);
            WriteMatrix(writer, node.LocalTransform);
        }

        if (data.IsSkinned) {
            writer.Write((byte)1);
            writer.Write(MemoryMarshal.AsBytes<Vector4i>(data.BoneIndices));
            writer.Write(MemoryMarshal.AsBytes<Vector4>(data.BoneWeights));

            SkeletonData skeleton = data.Skeleton;
            writer.Write(skeleton.BoneCount);
            for (var i = 0; i < skeleton.BoneCount; i++) {
                writer.Write(skeleton.BoneNames[i] ?? "");
                writer.Write(skeleton.ParentIndices[i]);
                WriteMatrix(writer, skeleton.InverseBindPose[i]);
                WriteMatrix(writer, skeleton.BindPoseLocal[i]);
            }
        }
        else {
            writer.Write((byte)0);
        }

        if (writeVersion >= 7) {
            foreach (SubMeshData sm in data.SubMeshes) {
                LodRange[] lods = sm.Lods is { Length: > 1 } ? sm.Lods : null;
                writer.Write(lods?.Length ?? 0);
                if (lods != null)
                    foreach (LodRange lr in lods) { writer.Write(lr.FirstIndex); writer.Write(lr.IndexCount); }
            }
        }

        if (writeVersion >= 8) {
            MeshSdf sdf = data.Sdf;
            if (sdf is { IsValid: true }) {
                writer.Write((byte)1);
                WriteVector3(writer, sdf.GridOrigin);
                WriteVector3(writer, sdf.GridExtent);
                writer.Write(sdf.ResX);
                writer.Write(sdf.ResY);
                writer.Write(sdf.ResZ);
                writer.Write(MemoryMarshal.AsBytes<float>(sdf.Distances));
            }
            else {
                writer.Write((byte)0);
            }
        }

        if (writeVersion >= 9) {
            MeshCards cards = data.Cards;
            if (cards is { IsValid: true }) {
                writer.Write((byte)1);
                writer.Write(cards.Count);
                foreach (MeshCard card in cards.Cards) {
                    WriteVector3(writer, card.Origin);
                    WriteVector3(writer, card.AxisX);
                    WriteVector3(writer, card.AxisY);
                    WriteVector3(writer, card.AxisZ);
                    WriteVector3(writer, card.Extent);
                    writer.Write(card.DirectionIndex);
                }
            }
            else {
                writer.Write((byte)0);
            }
        }

        if (writeVersion >= 10) {
            // PER-SUBMESH card block (Lumen FAZ 8.6). One byte: 1 = present. When present, write a count per
            // submesh (parallel to data.SubMeshes) followed by that submesh's cards (0 = none for that submesh).
            if (hasSubCards) {
                writer.Write((byte)1);
                MeshCards[] sub = data.SubMeshCards;
                int subCount = data.SubMeshes.Length;
                writer.Write(subCount);
                for (int i = 0; i < subCount; i++) {
                    MeshCards mc = (sub is not null && i < sub.Length) ? sub[i] : null;
                    int n = mc is { IsValid: true } ? mc.Count : 0;
                    writer.Write(n);
                    if (n > 0)
                        foreach (MeshCard card in mc.Cards)
                            WriteCard(writer, card);
                }
            }
            else {
                writer.Write((byte)0);
            }
        }
    }

    static bool HasAnySubMeshCards(in MeshData data) {
        MeshCards[] sub = data.SubMeshCards;
        if (sub is null) return false;
        foreach (MeshCards mc in sub) if (mc is { IsValid: true }) return true;
        return false;
    }

    static void WriteCard(BinaryWriter writer, in MeshCard card) {
        WriteVector3(writer, card.Origin);
        WriteVector3(writer, card.AxisX);
        WriteVector3(writer, card.AxisY);
        WriteVector3(writer, card.AxisZ);
        WriteVector3(writer, card.Extent);
        writer.Write(card.DirectionIndex);
    }

    static MeshCard ReadCard(BinaryReader reader) {
        Vector3 origin = ReadVector3(reader);
        Vector3 axisX = ReadVector3(reader);
        Vector3 axisY = ReadVector3(reader);
        Vector3 axisZ = ReadVector3(reader);
        Vector3 extent = ReadVector3(reader);
        int directionIndex = reader.ReadInt32();
        return new MeshCard(origin, axisX, axisY, axisZ, extent, directionIndex);
    }

    static void WriteVector3(BinaryWriter writer, Vector3 v) {
        writer.Write(v.X); writer.Write(v.Y); writer.Write(v.Z);
    }

    static Vector3 ReadVector3(BinaryReader reader) =>
        new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

    static void WriteMatrix(BinaryWriter writer, Matrix4 matrix) =>
        writer.Write(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref matrix, 1)));

    static Matrix4 ReadMatrix(BinaryReader reader) {
        Matrix4 matrix = default;
        reader.BaseStream.ReadExactly(MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref matrix, 1)));
        return matrix;
    }

    public static MeshData Read(string path) {
        using FileStream stream = File.OpenRead(path);
        return Read(stream, path);
    }

    public static MeshData Read(Stream stream, string sourceName = "<stream>") {
        using BinaryReader reader = new(stream);

        if (reader.ReadUInt32() != Magic)
            throw new InvalidDataException($"'{sourceName}' is not a mesh artifact (bad magic).");
        var version = reader.ReadUInt32();
        if (version is < 1 or > FormatVersion)
            throw new InvalidDataException($"Mesh artifact '{sourceName}' has unsupported version {version}.");

        var vertexCount = reader.ReadInt32();
        var indexCount = reader.ReadInt32();
        var subMeshCount = 0;
        if (version >= 2)
            subMeshCount = reader.ReadInt32();
        else
            reader.ReadUInt32();

        Vector3[] vertices = ReadArray<Vector3>(reader, vertexCount);
        Vector3[] normals = ReadArray<Vector3>(reader, vertexCount);
        Vector4[] tangents;
        if (version >= 3) {
            tangents = ReadArray<Vector4>(reader, vertexCount);
        }
        else {
            Vector3[] legacy = ReadArray<Vector3>(reader, vertexCount);
            tangents = new Vector4[vertexCount];
            for (var i = 0; i < vertexCount; i++)
                tangents[i] = new Vector4(legacy[i], 1f);
        }
        Vector2[] uvs = ReadArray<Vector2>(reader, vertexCount);
        uint[] indices = ReadArray<uint>(reader, indexCount);

        var subMeshes = new SubMeshData[subMeshCount];
        for (var i = 0; i < subMeshCount; i++) {
            var indexStart = reader.ReadInt32();
            var count = reader.ReadInt32();
            var name = reader.ReadString();
            var materialRef = reader.ReadString();
            Matrix4 nodeTransform = version >= 4 ? ReadMatrix(reader) : Matrix4.Identity;
            var nodeIndex = version >= 5 ? reader.ReadInt32() : -1;
            subMeshes[i] = new SubMeshData(
                name.Length > 0 ? name : null,
                indexStart, count,
                materialRef.Length > 0 ? materialRef : null,
                nodeTransform, nodeIndex);
        }

        MeshNodeData[] nodes = [];
        if (version >= 5) {
            nodes = new MeshNodeData[reader.ReadInt32()];
            for (var i = 0; i < nodes.Length; i++) {
                var name = reader.ReadString();
                var parentIndex = reader.ReadInt32();
                nodes[i] = new MeshNodeData(name.Length > 0 ? name : null, parentIndex, ReadMatrix(reader));
            }
        }

        Vector4i[] boneIndices = null; Vector4[] boneWeights = null; SkeletonData skeleton = default;
        bool skinned = false;
        if (version >= 6 && reader.ReadByte() == 1) {
            skinned = true;
            boneIndices = ReadArray<Vector4i>(reader, vertexCount);
            boneWeights = ReadArray<Vector4>(reader, vertexCount);

            var boneCount = reader.ReadInt32();
            var names = new string[boneCount];
            var parents = new int[boneCount];
            var inverseBind = new Matrix4[boneCount];
            var bindLocal = new Matrix4[boneCount];
            for (var i = 0; i < boneCount; i++) {
                var name = reader.ReadString();
                names[i] = name.Length > 0 ? name : null;
                parents[i] = reader.ReadInt32();
                inverseBind[i] = ReadMatrix(reader);
                bindLocal[i] = ReadMatrix(reader);
            }
            skeleton = new SkeletonData(names, parents, inverseBind, bindLocal);
        }

        if (version >= 7) {
            for (var i = 0; i < subMeshCount; i++) {
                int lodCount = reader.ReadInt32();
                if (lodCount <= 0) continue;
                var lods = new LodRange[lodCount];
                for (var l = 0; l < lodCount; l++)
                    lods[l] = new LodRange(reader.ReadInt32(), reader.ReadInt32());
                if (lodCount > 1) subMeshes[i] = subMeshes[i].WithLods(lods);
            }
        }

        MeshSdf sdf = null;
        if (version >= 8 && reader.ReadByte() == 1) {
            Vector3 gridOrigin = ReadVector3(reader);
            Vector3 gridExtent = ReadVector3(reader);
            int resX = reader.ReadInt32();
            int resY = reader.ReadInt32();
            int resZ = reader.ReadInt32();
            float[] distances = ReadArray<float>(reader, resX * resY * resZ);
            sdf = new MeshSdf(gridOrigin, gridExtent, resX, resY, resZ, distances);
        }

        MeshCards cards = null;
        if (version >= 9 && reader.ReadByte() == 1) {
            int cardCount = reader.ReadInt32();
            var arr = new MeshCard[cardCount];
            for (var i = 0; i < cardCount; i++)
                arr[i] = ReadCard(reader);
            cards = new MeshCards(arr);
        }

        // v10 — PER-SUBMESH cards (Lumen FAZ 8.6). Parallel to subMeshes; absent entries (count 0) stay null.
        MeshCards[] subMeshCards = null;
        if (version >= 10 && reader.ReadByte() == 1) {
            int subCount = reader.ReadInt32();
            subMeshCards = new MeshCards[subCount];
            for (var i = 0; i < subCount; i++) {
                int n = reader.ReadInt32();
                if (n <= 0) continue;
                var arr = new MeshCard[n];
                for (var k = 0; k < n; k++)
                    arr[k] = ReadCard(reader);
                subMeshCards[i] = new MeshCards(arr);
            }
        }

        if (skinned)
            return new MeshData(vertices, indices, uvs, normals, tangents, subMeshes, nodes,
                boneIndices, boneWeights, skeleton, sdf, cards, subMeshCards);

        MeshData result = new(vertices, indices, uvs, normals, tangents, subMeshes, nodes);
        if (sdf is not null) result = result.WithSdf(sdf);
        if (cards is not null) result = result.WithCards(cards);
        if (subMeshCards is not null) result = result.WithSubMeshCards(subMeshCards);
        return result;
    }

    static T[] ReadArray<T>(BinaryReader reader, int count) where T : unmanaged {
        var result = new T[count];
        reader.BaseStream.ReadExactly(MemoryMarshal.AsBytes<T>(result));
        return result;
    }
}
