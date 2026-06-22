using Assimp;

namespace BallisticEngine.AssetPipeline;

public static class AssimpSkinDecoder {
    const int MaxInfluences = 4;

    public sealed class DecodedSkinnedModel {
        public MeshData Mesh;
        public DecodedMaterial[] SubMeshMaterials;
        public AnimationClipData[] Animations;
    }

    public static bool SceneHasSkin(string path, bool flipUVs = true) {
        try {
            AssimpContext context = new();
            Assimp.Scene scene = context.ImportFile(path, PostProcessSteps.None);
            return SceneHasSkin(scene);
        }
        catch {
            return false;
        }
    }

    public static bool SceneHasSkin(Assimp.Scene scene) {
        if (scene?.Meshes is null)
            return false;
        foreach (Assimp.Mesh mesh in scene.Meshes)
            if (mesh.HasBones)
                return true;
        return false;
    }

    public static DecodedSkinnedModel Decode(string path, bool flipUVs = true) {
        AssimpContext context = new();
        PostProcessSteps steps = PostProcessSteps.Triangulate
                                 | PostProcessSteps.GenerateSmoothNormals
                                 | PostProcessSteps.CalculateTangentSpace
                                 | PostProcessSteps.LimitBoneWeights;
        if (flipUVs)
            steps |= PostProcessSteps.FlipUVs;

        Assimp.Scene scene = context.ImportFile(path, steps);
        if (scene is null || scene.MeshCount == 0)
            throw new IOException($"Skinned mesh import failed or no meshes found in '{path}'.");

        SkeletonData skeleton = BuildSkeleton(scene, out Dictionary<string, int> boneIndexByName);

        DecodedSkinnedModel model = MergeSkinned(scene, skeleton, boneIndexByName, out DecodedMaterial[] materials);
        model.SubMeshMaterials = materials;

        model.Animations = DecodeAnimations(scene, boneIndexByName);
        return model;
    }

    static SkeletonData BuildSkeleton(Assimp.Scene scene, out Dictionary<string, int> indexByName) {
        var offsetByName = new Dictionary<string, Matrix4>();
        foreach (Assimp.Mesh mesh in scene.Meshes) {
            if (!mesh.HasBones) continue;
            foreach (Bone bone in mesh.Bones)
                offsetByName[bone.Name] = AssimpMeshDecoder.ToOpenTKMatrix(bone.OffsetMatrix);
        }

        var keep = new HashSet<string>();
        void MarkAncestors(Node node) {
            if (node is null) return;
            if (offsetByName.ContainsKey(node.Name)) {
                for (Node n = node; n is not null; n = n.Parent)
                    keep.Add(n.Name);
            }
            foreach (Node child in node.Children)
                MarkAncestors(child);
        }
        MarkAncestors(scene.RootNode);

        var names = new List<string>();
        var parents = new List<int>();
        var bindLocal = new List<Matrix4>();
        var index = new Dictionary<string, int>();

        void Visit(Node node, int parentIndex) {
            int myIndex = parentIndex;
            if (keep.Contains(node.Name)) {
                myIndex = names.Count;
                index[node.Name] = myIndex;
                names.Add(node.Name);
                parents.Add(parentIndex);
                bindLocal.Add(AssimpMeshDecoder.ToOpenTKMatrix(node.Transform));
            }
            foreach (Node child in node.Children)
                Visit(child, myIndex);
        }
        Visit(scene.RootNode, -1);
        indexByName = index;

        var inverseBind = new Matrix4[names.Count];
        for (var i = 0; i < names.Count; i++)
            inverseBind[i] = offsetByName.TryGetValue(names[i], out Matrix4 offset) ? offset : Matrix4.Identity;

        return new SkeletonData(names.ToArray(), parents.ToArray(), inverseBind, bindLocal.ToArray());
    }

    sealed class Accum {
        public readonly List<Vector3> Positions = new();
        public readonly List<Vector3> Normals = new();
        public readonly List<Vector4> Tangents = new();
        public readonly List<Vector2> UVs = new();
        public readonly List<Vector4i> BoneIndices = new();
        public readonly List<Vector4> BoneWeights = new();
        public readonly List<uint> Indices = new();
    }

    static DecodedSkinnedModel MergeSkinned(Assimp.Scene scene, SkeletonData skeleton,
        Dictionary<string, int> boneIndexByName, out DecodedMaterial[] materials) {
        var accumByMaterial = new Dictionary<int, Accum>();
        var order = new List<int>();

        foreach (Assimp.Mesh mesh in scene.Meshes) {
            if (mesh.VertexCount == 0 || mesh.FaceCount == 0)
                continue;

            if (!accumByMaterial.TryGetValue(mesh.MaterialIndex, out Accum accum)) {
                accum = new Accum();
                accumByMaterial[mesh.MaterialIndex] = accum;
                order.Add(mesh.MaterialIndex);
            }
            AppendMesh(mesh, accum, boneIndexByName);
        }

        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        var tangents = new List<Vector4>();
        var uvs = new List<Vector2>();
        var boneIdx = new List<Vector4i>();
        var boneWt = new List<Vector4>();
        var indices = new List<uint>();
        var subMeshes = new List<SubMeshData>();
        var decodedMats = new List<DecodedMaterial>();

        foreach (int materialIndex in order) {
            Accum a = accumByMaterial[materialIndex];
            var vertexOffset = (uint)positions.Count;
            int indexStart = indices.Count;

            positions.AddRange(a.Positions);
            normals.AddRange(a.Normals);
            tangents.AddRange(a.Tangents);
            uvs.AddRange(a.UVs);
            boneIdx.AddRange(a.BoneIndices);
            boneWt.AddRange(a.BoneWeights);
            foreach (uint index in a.Indices)
                indices.Add(index + vertexOffset);

            DecodedMaterial material = AssimpMeshDecoder.DecodeMaterialPublic(scene, materialIndex);
            subMeshes.Add(new SubMeshData(material?.Name, indexStart, a.Indices.Count, null,
                Matrix4.Identity, -1));
            decodedMats.Add(material);
        }

        materials = decodedMats.ToArray();
        var meshData = new MeshData(
            positions.ToArray(), indices.ToArray(), uvs.ToArray(), normals.ToArray(), tangents.ToArray(),
            subMeshes.ToArray(), nodes: [],
            boneIdx.ToArray(), boneWt.ToArray(), skeleton);
        return new DecodedSkinnedModel { Mesh = meshData };
    }

    static void AppendMesh(Assimp.Mesh mesh, Accum accum, Dictionary<string, int> boneIndexByName) {
        var baseVertex = (uint)accum.Positions.Count;
        bool hasUVs = mesh.TextureCoordinateChannelCount > 0 && mesh.HasTextureCoords(0);
        bool hasTangents = mesh.HasTangentBasis;

        int vertexCount = mesh.VertexCount;
        var influences = new List<(int bone, float weight)>[vertexCount];
        for (var i = 0; i < vertexCount; i++)
            influences[i] = new List<(int, float)>(MaxInfluences);

        foreach (Bone bone in mesh.Bones) {
            if (!boneIndexByName.TryGetValue(bone.Name, out int boneIndex))
                continue;
            foreach (VertexWeight vw in bone.VertexWeights) {
                if (vw.VertexID >= 0 && vw.VertexID < vertexCount && vw.Weight > 0f)
                    influences[vw.VertexID].Add((boneIndex, vw.Weight));
            }
        }

        for (var i = 0; i < vertexCount; i++) {
            accum.Positions.Add(ToVec3(mesh.Vertices[i]));
            accum.Normals.Add(mesh.HasNormals ? ToVec3(mesh.Normals[i]) : Vector3.UnitY);

            if (hasTangents) {
                Vector3 t = ToVec3(mesh.Tangents[i]);
                Vector3 n = mesh.HasNormals ? ToVec3(mesh.Normals[i]) : Vector3.UnitY;
                Vector3 b = ToVec3(mesh.BiTangents[i]);
                float w = Vector3.Dot(Vector3.Cross(n, t), b) < 0f ? -1f : 1f;
                accum.Tangents.Add(new Vector4(t, w));
            }
            else {
                accum.Tangents.Add(new Vector4(Vector3.UnitX, 1f));
            }

            if (hasUVs) {
                Vector3D uv = mesh.TextureCoordinateChannels[0][i];
                accum.UVs.Add(new Vector2(uv.X, uv.Y));
            }
            else {
                accum.UVs.Add(Vector2.Zero);
            }

            PackInfluences(influences[i], out Vector4i idx, out Vector4 wt);
            accum.BoneIndices.Add(idx);
            accum.BoneWeights.Add(wt);
        }

        foreach (Face face in mesh.Faces) {
            if (face.IndexCount != 3) continue;
            accum.Indices.Add(baseVertex + (uint)face.Indices[0]);
            accum.Indices.Add(baseVertex + (uint)face.Indices[1]);
            accum.Indices.Add(baseVertex + (uint)face.Indices[2]);
        }
    }

    static void PackInfluences(List<(int bone, float weight)> list, out Vector4i indices, out Vector4 weights) {
        list.Sort((a, b) => b.weight.CompareTo(a.weight));
        Span<int> idx = stackalloc int[MaxInfluences];
        Span<float> wt = stackalloc float[MaxInfluences];
        int n = Math.Min(list.Count, MaxInfluences);
        float sum = 0f;
        for (var i = 0; i < n; i++) { idx[i] = list[i].bone; wt[i] = list[i].weight; sum += list[i].weight; }

        if (n == 0 || sum <= 0f) {
            indices = new Vector4i(0, 0, 0, 0);
            weights = new Vector4(1f, 0f, 0f, 0f);
            return;
        }
        float inv = 1f / sum;
        indices = new Vector4i(idx[0], idx[1], idx[2], idx[3]);
        weights = new Vector4(wt[0] * inv, wt[1] * inv, wt[2] * inv, wt[3] * inv);
    }

    static AnimationClipData[] DecodeAnimations(Assimp.Scene scene, Dictionary<string, int> boneIndexByName) {
        if (!scene.HasAnimations)
            return System.Array.Empty<AnimationClipData>();

        var clips = new List<AnimationClipData>();
        foreach (Animation anim in scene.Animations) {
            if (!anim.HasNodeAnimations)
                continue;

            var channels = new List<BoneChannel>();
            foreach (NodeAnimationChannel channel in anim.NodeAnimationChannels) {
                if (!boneIndexByName.TryGetValue(channel.NodeName, out int boneIndex))
                    continue;

                var posKeys = new VectorKey[channel.PositionKeyCount];
                for (var i = 0; i < posKeys.Length; i++)
                    posKeys[i] = new VectorKey((float)channel.PositionKeys[i].Time, ToVec3(channel.PositionKeys[i].Value));

                var rotKeys = new QuaternionKey[channel.RotationKeyCount];
                for (var i = 0; i < rotKeys.Length; i++)
                    rotKeys[i] = new QuaternionKey((float)channel.RotationKeys[i].Time, ToQuat(channel.RotationKeys[i].Value));

                var scaleKeys = new VectorKey[channel.ScalingKeyCount];
                for (var i = 0; i < scaleKeys.Length; i++)
                    scaleKeys[i] = new VectorKey((float)channel.ScalingKeys[i].Time, ToVec3(channel.ScalingKeys[i].Value));

                channels.Add(new BoneChannel(boneIndex, channel.NodeName, posKeys, rotKeys, scaleKeys));
            }

            if (channels.Count == 0)
                continue;

            var name = string.IsNullOrEmpty(anim.Name) ? $"Clip{clips.Count}" : anim.Name;
            clips.Add(new AnimationClipData(name, (float)anim.DurationInTicks,
                (float)anim.TicksPerSecond, channels.ToArray()));
        }
        return clips.ToArray();
    }

    static Vector3 ToVec3(in Vector3D v) => new(v.X, v.Y, v.Z);
    static System.Numerics.Quaternion ToQuat(in Assimp.Quaternion q) => new(q.X, q.Y, q.Z, q.W);
}
