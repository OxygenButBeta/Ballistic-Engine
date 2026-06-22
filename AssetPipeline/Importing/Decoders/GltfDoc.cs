using System.Text.Json;

namespace BallisticEngine.AssetPipeline;

internal sealed class GltfDoc {
    readonly JsonElement root;
    readonly byte[] glbBin;
    readonly string baseDir;
    readonly byte[][] buffers;
    readonly string modelStem;

    const int GL_BYTE = 5120, GL_UBYTE = 5121, GL_SHORT = 5122, GL_USHORT = 5123, GL_UINT = 5125, GL_FLOAT = 5126;

    public GltfDoc(JsonElement root, byte[] glbBin, string baseDir, string modelStem) {
        this.root = root;
        this.glbBin = glbBin;
        this.baseDir = baseDir;
        this.modelStem = modelStem;
        buffers = ResolveBuffers();
    }

    byte[][] ResolveBuffers() {
        if (!root.TryGetProperty("buffers", out JsonElement buffersJson))
            return [];
        var result = new byte[buffersJson.GetArrayLength()][];
        int i = 0;
        foreach (JsonElement buffer in buffersJson.EnumerateArray()) {
            if (buffer.TryGetProperty("uri", out JsonElement uri)) {
                var uriStr = uri.GetString();
                if (uriStr.StartsWith("data:")) {
                    int comma = uriStr.IndexOf(',');
                    result[i] = Convert.FromBase64String(uriStr[(comma + 1)..]);
                }
                else {
                    var file = Path.Combine(baseDir, Uri.UnescapeDataString(uriStr));
                    result[i] = File.Exists(file) ? File.ReadAllBytes(file) : [];
                }
            }
            else {
                result[i] = glbBin ?? [];
            }
            i++;
        }
        return result;
    }

    JsonElement Accessor(int index) => root.GetProperty("accessors")[index];
    JsonElement BufferView(int index) => root.GetProperty("bufferViews")[index];

    static int Int(JsonElement e, string name, int fallback = 0) =>
        e.TryGetProperty(name, out JsonElement v) ? v.GetInt32() : fallback;

    (byte[] buffer, int start, int stride, int count, int componentType, int components)
        AccessorLayout(int accessorIndex) {
        JsonElement acc = Accessor(accessorIndex);
        int componentType = Int(acc, "componentType");
        int count = Int(acc, "count");
        string type = acc.GetProperty("type").GetString();
        int components = type switch {
            "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4,
            "MAT2" => 4, "MAT3" => 9, "MAT4" => 16, _ => 1,
        };
        int accByteOffset = Int(acc, "byteOffset");

        JsonElement bv = BufferView(Int(acc, "bufferView"));
        int bufferIndex = Int(bv, "buffer");
        int bvByteOffset = Int(bv, "byteOffset");
        int componentSize = ComponentSize(componentType);
        int defaultStride = components * componentSize;
        int stride = Int(bv, "byteStride", 0);
        if (stride == 0) stride = defaultStride;

        return (buffers[bufferIndex], bvByteOffset + accByteOffset, stride, count, componentType, components);
    }

    static int ComponentSize(int componentType) => componentType switch {
        GL_BYTE or GL_UBYTE => 1,
        GL_SHORT or GL_USHORT => 2,
        GL_UINT or GL_FLOAT => 4,
        _ => 4,
    };

    float[] ReadFloats(int accessorIndex) {
        var (buffer, start, stride, count, componentType, components) = AccessorLayout(accessorIndex);
        var result = new float[count * components];
        for (int i = 0; i < count; i++) {
            int elementStart = start + i * stride;
            for (int c = 0; c < components; c++) {
                int p = elementStart + c * ComponentSize(componentType);
                result[i * components + c] = ReadComponentAsFloat(buffer, p, componentType);
            }
        }
        return result;
    }

    int[] ReadInts(int accessorIndex) {
        var (buffer, start, stride, count, componentType, components) = AccessorLayout(accessorIndex);
        var result = new int[count * components];
        for (int i = 0; i < count; i++) {
            int elementStart = start + i * stride;
            for (int c = 0; c < components; c++) {
                int p = elementStart + c * ComponentSize(componentType);
                result[i * components + c] = ReadComponentAsInt(buffer, p, componentType);
            }
        }
        return result;
    }

    static float ReadComponentAsFloat(byte[] b, int p, int componentType) => componentType switch {
        GL_FLOAT => BitConverter.ToSingle(b, p),
        GL_UBYTE => b[p] / 255f,
        GL_USHORT => BitConverter.ToUInt16(b, p) / 65535f,
        GL_BYTE => Math.Max((sbyte)b[p] / 127f, -1f),
        GL_SHORT => Math.Max(BitConverter.ToInt16(b, p) / 32767f, -1f),
        _ => 0f,
    };

    static int ReadComponentAsInt(byte[] b, int p, int componentType) => componentType switch {
        GL_UBYTE => b[p],
        GL_USHORT => BitConverter.ToUInt16(b, p),
        GL_UINT => (int)BitConverter.ToUInt32(b, p),
        GL_SHORT => BitConverter.ToInt16(b, p),
        GL_BYTE => (sbyte)b[p],
        _ => 0,
    };

    Vector3[] ReadVec3(int accessorIndex) {
        float[] f = ReadFloats(accessorIndex);
        var result = new Vector3[f.Length / 3];
        for (int i = 0; i < result.Length; i++)
            result[i] = new Vector3(f[i * 3], f[i * 3 + 1], f[i * 3 + 2]);
        return result;
    }

    Vector2[] ReadVec2(int accessorIndex) {
        float[] f = ReadFloats(accessorIndex);
        var result = new Vector2[f.Length / 2];
        for (int i = 0; i < result.Length; i++)
            result[i] = new Vector2(f[i * 2], f[i * 2 + 1]);
        return result;
    }

    Matrix4[] ReadMat4(int accessorIndex) {
        float[] f = ReadFloats(accessorIndex);
        var result = new Matrix4[f.Length / 16];
        for (int i = 0; i < result.Length; i++) {
            int o = i * 16;
            result[i] = new Matrix4(
                f[o + 0], f[o + 1], f[o + 2], f[o + 3],
                f[o + 4], f[o + 5], f[o + 6], f[o + 7],
                f[o + 8], f[o + 9], f[o + 10], f[o + 11],
                f[o + 12], f[o + 13], f[o + 14], f[o + 15]);
        }
        return result;
    }

    Matrix4 NodeLocalMatrix(JsonElement node) {
        if (node.TryGetProperty("matrix", out JsonElement matrix)) {
            float[] f = new float[16];
            int i = 0;
            foreach (JsonElement v in matrix.EnumerateArray()) f[i++] = v.GetSingle();
            return new Matrix4(
                f[0], f[1], f[2], f[3], f[4], f[5], f[6], f[7],
                f[8], f[9], f[10], f[11], f[12], f[13], f[14], f[15]);
        }

        Vector3 t = ReadVec3OrDefault(node, "translation", Vector3.Zero);
        Quaternion r = ReadQuatOrDefault(node, "rotation");
        Vector3 s = ReadVec3OrDefault(node, "scale", Vector3.One);
        return Matrix4.CreateScale(s) * Matrix4.CreateFromQuaternion(r) * Matrix4.CreateTranslation(t);
    }

    static Vector3 ReadVec3OrDefault(JsonElement node, string name, Vector3 fallback) {
        if (!node.TryGetProperty(name, out JsonElement arr)) return fallback;
        float[] f = new float[3]; int i = 0;
        foreach (JsonElement v in arr.EnumerateArray()) f[i++] = v.GetSingle();
        return new Vector3(f[0], f[1], f[2]);
    }

    static Quaternion ReadQuatOrDefault(JsonElement node, string name) {
        if (!node.TryGetProperty(name, out JsonElement arr)) return Quaternion.Identity;
        float[] f = new float[4]; int i = 0;
        foreach (JsonElement v in arr.EnumerateArray()) f[i++] = v.GetSingle();
        return new Quaternion(f[0], f[1], f[2], f[3]);
    }

    static Dictionary<int, int> BuildNodeParentMap(JsonElement nodes) {
        var parent = new Dictionary<int, int>();
        for (int i = 0; i < nodes.GetArrayLength(); i++) {
            if (!nodes[i].TryGetProperty("children", out JsonElement children))
                continue;
            foreach (JsonElement child in children.EnumerateArray())
                parent[child.GetInt32()] = i;
        }
        return parent;
    }

    Matrix4 FindSkinnedMeshNodeGlobal(JsonElement nodes, Dictionary<int, int> nodeParent) {
        for (int i = 0; i < nodes.GetArrayLength(); i++) {
            if (nodes[i].TryGetProperty("skin", out _) && nodes[i].TryGetProperty("mesh", out _))
                return GlobalNodeMatrix(nodes, nodeParent, i);
        }
        return Matrix4.Identity;
    }

    Matrix4 GlobalNodeMatrix(JsonElement nodes, Dictionary<int, int> nodeParent, int nodeIndex) {
        Matrix4 m = NodeLocalMatrix(nodes[nodeIndex]);
        int p = nodeParent.GetValueOrDefault(nodeIndex, -1);
        int guard = 0;
        while (p >= 0 && guard++ < 1024) {
            m = m * NodeLocalMatrix(nodes[p]);
            p = nodeParent.GetValueOrDefault(p, -1);
        }
        return m;
    }

    public SkeletonData BuildSkeleton(out Dictionary<int, int> jointNodeToBone) {
        jointNodeToBone = new Dictionary<int, int>();
        JsonElement skin = root.GetProperty("skins")[0];
        JsonElement jointsJson = skin.GetProperty("joints");
        int boneCount = jointsJson.GetArrayLength();

        var jointNodes = new int[boneCount];
        int idx = 0;
        foreach (JsonElement j in jointsJson.EnumerateArray()) {
            jointNodes[idx] = j.GetInt32();
            jointNodeToBone[jointNodes[idx]] = idx;
            idx++;
        }

        JsonElement nodes = root.GetProperty("nodes");
        var names = new string[boneCount];
        var parents = new int[boneCount];
        var bindLocal = new Matrix4[boneCount];

        for (int b = 0; b < boneCount; b++) {
            parents[b] = -1;
            JsonElement node = nodes[jointNodes[b]];
            names[b] = node.TryGetProperty("name", out JsonElement nm) ? nm.GetString() : $"Bone{b}";
        }
        for (int b = 0; b < boneCount; b++) {
            JsonElement node = nodes[jointNodes[b]];
            if (!node.TryGetProperty("children", out JsonElement children))
                continue;
            foreach (JsonElement child in children.EnumerateArray()) {
                if (jointNodeToBone.TryGetValue(child.GetInt32(), out int childBone))
                    parents[childBone] = b;
            }
        }

        Matrix4[] inverseBind;
        if (skin.TryGetProperty("inverseBindMatrices", out JsonElement ibmAcc))
            inverseBind = ReadMat4(ibmAcc.GetInt32());
        else {
            inverseBind = new Matrix4[boneCount];
            for (int b = 0; b < boneCount; b++) inverseBind[b] = Matrix4.Identity;
        }

        var globalBind = new Matrix4[boneCount];
        for (int b = 0; b < boneCount; b++)
            globalBind[b] = inverseBind[b].Inverted();
        for (int b = 0; b < boneCount; b++) {
            bindLocal[b] = parents[b] < 0
                ? globalBind[b]
                : globalBind[b] * globalBind[parents[b]].Inverted();
        }

        return Reorder(new SkeletonData(names, parents, inverseBind, bindLocal), jointNodeToBone, jointNodes);
    }

    static SkeletonData Reorder(SkeletonData s, Dictionary<int, int> jointNodeToBone, int[] jointNodes) {
        int n = s.BoneCount;
        var depth = new int[n];
        for (int i = 0; i < n; i++) {
            int d = 0, p = s.ParentIndices[i], guard = 0;
            while (p >= 0 && guard++ < n) { d++; p = s.ParentIndices[p]; }
            depth[i] = d;
        }

        var order = Enumerable.Range(0, n).OrderBy(i => depth[i]).ToArray();
        var oldToNew = new int[n];
        for (int newIdx = 0; newIdx < n; newIdx++) oldToNew[order[newIdx]] = newIdx;

        var names = new string[n];
        var parents = new int[n];
        var inverseBind = new Matrix4[n];
        var bindLocal = new Matrix4[n];
        for (int newIdx = 0; newIdx < n; newIdx++) {
            int old = order[newIdx];
            names[newIdx] = s.BoneNames[old];
            parents[newIdx] = s.ParentIndices[old] < 0 ? -1 : oldToNew[s.ParentIndices[old]];
            inverseBind[newIdx] = s.InverseBindPose[old];
            bindLocal[newIdx] = s.BindPoseLocal[old];
        }

        var remapped = new Dictionary<int, int>();
        foreach ((int node, int oldBone) in jointNodeToBone)
            remapped[node] = oldToNew[oldBone];
        jointNodeToBone.Clear();
        foreach ((int k, int v) in remapped) jointNodeToBone[k] = v;

        var newJointNodes = new int[n];
        for (int i = 0; i < n; i++) newJointNodes[oldToNew[i]] = jointNodes[i];
        System.Array.Copy(newJointNodes, jointNodes, n);

        return new SkeletonData(names, parents, inverseBind, bindLocal);
    }

    public MeshData BuildSkinnedMesh(SkeletonData skeleton, Dictionary<int, int> jointNodeToBone, bool flipUVs) {
        JsonElement skin = root.GetProperty("skins")[0];
        var jointToBone = new int[skin.GetProperty("joints").GetArrayLength()];
        int ji = 0;
        foreach (JsonElement j in skin.GetProperty("joints").EnumerateArray())
            jointToBone[ji++] = jointNodeToBone[j.GetInt32()];

        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        var tangents = new List<Vector4>();
        var uvs = new List<Vector2>();
        var boneIndices = new List<Vector4i>();
        var boneWeights = new List<Vector4>();
        var indices = new List<uint>();
        var subMeshes = new List<SubMeshData>();
        materialIndexPerSubmesh = new List<int>();

        foreach (JsonElement mesh in root.GetProperty("meshes").EnumerateArray()) {
            foreach (JsonElement prim in mesh.GetProperty("primitives").EnumerateArray()) {
                JsonElement attr = prim.GetProperty("attributes");
                if (!attr.TryGetProperty("JOINTS_0", out _))
                    continue;

                var baseVertex = (uint)positions.Count;
                int indexStart = indices.Count;

                Vector3[] pos = ReadVec3(attr.GetProperty("POSITION").GetInt32());
                Vector3[] nrm = attr.TryGetProperty("NORMAL", out JsonElement nAcc)
                    ? ReadVec3(nAcc.GetInt32()) : null;
                Vector2[] uv = attr.TryGetProperty("TEXCOORD_0", out JsonElement tAcc)
                    ? ReadVec2(tAcc.GetInt32()) : null;
                int[] joints = ReadInts(attr.GetProperty("JOINTS_0").GetInt32());
                float[] weights = ReadFloats(attr.GetProperty("WEIGHTS_0").GetInt32());

                for (int v = 0; v < pos.Length; v++) {
                    positions.Add(pos[v]);
                    normals.Add(nrm != null ? nrm[v] : Vector3.UnitY);
                    tangents.Add(new Vector4(Vector3.UnitX, 1f));
                    Vector2 coord = uv != null ? uv[v] : Vector2.Zero;
                    if (flipUVs) coord.Y = 1f - coord.Y;
                    uvs.Add(coord);

                    int b0 = jointToBone[joints[v * 4 + 0]];
                    int b1 = jointToBone[joints[v * 4 + 1]];
                    int b2 = jointToBone[joints[v * 4 + 2]];
                    int b3 = jointToBone[joints[v * 4 + 3]];
                    float w0 = weights[v * 4 + 0], w1 = weights[v * 4 + 1],
                          w2 = weights[v * 4 + 2], w3 = weights[v * 4 + 3];
                    float sum = w0 + w1 + w2 + w3;
                    if (sum > 1e-6f) { float inv = 1f / sum; w0 *= inv; w1 *= inv; w2 *= inv; w3 *= inv; }
                    else { w0 = 1f; b0 = 0; }
                    boneIndices.Add(new Vector4i(b0, b1, b2, b3));
                    boneWeights.Add(new Vector4(w0, w1, w2, w3));
                }

                if (prim.TryGetProperty("indices", out JsonElement idxAcc)) {
                    int[] meshIndices = ReadInts(idxAcc.GetInt32());
                    foreach (int mi in meshIndices)
                        indices.Add(baseVertex + (uint)mi);
                }
                else {
                    for (uint v = 0; v < pos.Length; v++) indices.Add(baseVertex + v);
                }

                subMeshes.Add(new SubMeshData(mesh.TryGetProperty("name", out JsonElement mn) ? mn.GetString() : null,
                    indexStart, indices.Count - indexStart, null, Matrix4.Identity, -1));
                materialIndexPerSubmesh.Add(prim.TryGetProperty("material", out JsonElement matIdx) ? matIdx.GetInt32() : -1);
            }
        }

        return new MeshData(
            positions.ToArray(), indices.ToArray(), uvs.ToArray(), normals.ToArray(), tangents.ToArray(),
            subMeshes.ToArray(), nodes: [], boneIndices.ToArray(), boneWeights.ToArray(), skeleton);
    }

    List<int> materialIndexPerSubmesh;

    public DecodedMaterial[] BuildMaterials(int subMeshCount) {
        var result = new DecodedMaterial[subMeshCount];
        bool hasMaterials = root.TryGetProperty("materials", out JsonElement materials);
        var extractedByTexture = new Dictionary<int, string>();

        for (int i = 0; i < subMeshCount; i++) {
            int mi = materialIndexPerSubmesh != null && i < materialIndexPerSubmesh.Count ? materialIndexPerSubmesh[i] : -1;
            string name = $"Material {i}";
            var decoded = new DecodedMaterial { Name = name };

            if (hasMaterials && mi >= 0 && mi < materials.GetArrayLength()) {
                JsonElement mat = materials[mi];
                if (mat.TryGetProperty("name", out JsonElement nm))
                    decoded.Name = nm.GetString();
                DecodeMaterial(mat, decoded, extractedByTexture);
            }
            result[i] = decoded;
        }
        return result;
    }

    void DecodeMaterial(JsonElement mat, DecodedMaterial decoded, Dictionary<int, string> extracted) {
        if (mat.TryGetProperty("pbrMetallicRoughness", out JsonElement pbr)) {
            if (pbr.TryGetProperty("baseColorFactor", out JsonElement bcf) && bcf.GetArrayLength() == 4) {
                float[] c = ReadFloatArray(bcf, 4);
                if (c[0] < 0.999f || c[1] < 0.999f || c[2] < 0.999f || c[3] < 0.999f)
                    decoded.BaseColor = new Vector4(c[0], c[1], c[2], c[3]);
            }
            if (pbr.TryGetProperty("metallicFactor", out JsonElement mf))
                decoded.Metallic = Math.Clamp(mf.GetSingle(), 0f, 1f);
            if (pbr.TryGetProperty("roughnessFactor", out JsonElement rf))
                decoded.Roughness = Math.Clamp(rf.GetSingle(), 0f, 1f);

            MapTexture(pbr, "baseColorTexture", TextureType.Diffuse, decoded, extracted);
            MapTexture(pbr, "metallicRoughnessTexture", TextureType.Metallic, decoded, extracted);
            MapTexture(pbr, "metallicRoughnessTexture", TextureType.Roughness, decoded, extracted);
        }

        MapTexture(mat, "normalTexture", TextureType.Normal, decoded, extracted);
        MapTexture(mat, "occlusionTexture", TextureType.AO, decoded, extracted);
        MapTexture(mat, "emissiveTexture", TextureType.Emissive, decoded, extracted);

        if (mat.TryGetProperty("emissiveFactor", out JsonElement ef) && ef.GetArrayLength() == 3) {
            float[] e = ReadFloatArray(ef, 3);
            if (e[0] > 0.001f || e[1] > 0.001f || e[2] > 0.001f)
                decoded.EmissiveColor = new Vector3(e[0], e[1], e[2]);
        }
    }

    void MapTexture(JsonElement owner, string property, TextureType slot, DecodedMaterial decoded,
        Dictionary<int, string> extracted) {
        if (decoded.TexturePaths.ContainsKey(slot))
            return;
        if (!owner.TryGetProperty(property, out JsonElement texRef) || !texRef.TryGetProperty("index", out JsonElement idx))
            return;
        int textureIndex = idx.GetInt32();
        string file = ExtractTextureImage(textureIndex, extracted);
        if (file != null)
            decoded.TexturePaths[slot] = file;
    }

    string ExtractTextureImage(int textureIndex, Dictionary<int, string> cache) {
        if (cache.TryGetValue(textureIndex, out string cached))
            return cached;

        try {
            if (!root.TryGetProperty("textures", out JsonElement textures) || textureIndex >= textures.GetArrayLength())
                return null;
            JsonElement texture = textures[textureIndex];
            if (!texture.TryGetProperty("source", out JsonElement srcJson))
                return null;
            int imageIndex = srcJson.GetInt32();

            JsonElement image = root.GetProperty("images")[imageIndex];

            byte[] bytes;
            string ext;
            if (image.TryGetProperty("uri", out JsonElement uriJson)) {
                string uri = uriJson.GetString();
                if (uri.StartsWith("data:")) {
                    int comma = uri.IndexOf(',');
                    bytes = Convert.FromBase64String(uri[(comma + 1)..]);
                    ext = uri.Contains("image/png") ? ".png" : ".jpg";
                }
                else {
                    string ext0 = Path.Combine(baseDir, Uri.UnescapeDataString(uri));
                    string result = File.Exists(ext0) ? Path.GetFullPath(ext0) : null;
                    cache[textureIndex] = result;
                    return result;
                }
            }
            else if (image.TryGetProperty("bufferView", out JsonElement bvJson)) {
                bytes = ReadBufferView(bvJson.GetInt32());
                string mime = image.TryGetProperty("mimeType", out JsonElement mt) ? mt.GetString() : "image/png";
                ext = mime.Contains("jpeg") || mime.Contains("jpg") ? ".jpg" : ".png";
            }
            else {
                return null;
            }

            string dir = Path.Combine(baseDir, $"{modelStem}_Textures");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"{modelStem}_tex{textureIndex}{ext}");
            File.WriteAllBytes(path, bytes);
            cache[textureIndex] = path;
            return path;
        }
        catch (Exception e) {
            Debugging.LogWarning($"glTF texture {textureIndex} extraction failed: {e.Message}");
            cache[textureIndex] = null;
            return null;
        }
    }

    byte[] ReadBufferView(int bufferViewIndex) {
        JsonElement bv = BufferView(bufferViewIndex);
        int bufferIndex = Int(bv, "buffer");
        int offset = Int(bv, "byteOffset");
        int length = Int(bv, "byteLength");
        return buffers[bufferIndex].AsSpan(offset, length).ToArray();
    }

    static float[] ReadFloatArray(JsonElement arr, int count) {
        var f = new float[count];
        int i = 0;
        foreach (JsonElement v in arr.EnumerateArray()) {
            if (i >= count) break;
            f[i++] = v.GetSingle();
        }
        return f;
    }

    public AnimationClipData[] BuildAnimations(Dictionary<int, int> jointNodeToBone, string[] boneNames = null) {
        if (!root.TryGetProperty("animations", out JsonElement animations))
            return [];

        var clips = new List<AnimationClipData>();
        int clipNumber = 0;
        foreach (JsonElement anim in animations.EnumerateArray()) {
            var byBone = new Dictionary<int, (List<VectorKey> pos, List<QuaternionKey> rot, List<VectorKey> scale)>();
            float maxTime = 0f;

            JsonElement samplers = anim.GetProperty("samplers");
            foreach (JsonElement channel in anim.GetProperty("channels").EnumerateArray()) {
                JsonElement target = channel.GetProperty("target");
                if (!target.TryGetProperty("node", out JsonElement nodeJson))
                    continue;
                if (!jointNodeToBone.TryGetValue(nodeJson.GetInt32(), out int bone))
                    continue;
                string path = target.GetProperty("path").GetString();

                JsonElement sampler = samplers[channel.GetProperty("sampler").GetInt32()];
                float[] times = ReadFloats(sampler.GetProperty("input").GetInt32());
                float[] values = ReadFloats(sampler.GetProperty("output").GetInt32());
                if (times.Length > 0) maxTime = Math.Max(maxTime, times[^1]);

                if (!byBone.TryGetValue(bone, out var tracks)) {
                    tracks = (new List<VectorKey>(), new List<QuaternionKey>(), new List<VectorKey>());
                    byBone[bone] = tracks;
                }

                switch (path) {
                    case "translation":
                        for (int k = 0; k < times.Length; k++)
                            tracks.pos.Add(new VectorKey(times[k], new Vector3(values[k * 3], values[k * 3 + 1], values[k * 3 + 2])));
                        break;
                    case "rotation":
                        for (int k = 0; k < times.Length; k++)
                            tracks.rot.Add(new QuaternionKey(times[k], new Quaternion(values[k * 4], values[k * 4 + 1], values[k * 4 + 2], values[k * 4 + 3])));
                        break;
                    case "scale":
                        for (int k = 0; k < times.Length; k++)
                            tracks.scale.Add(new VectorKey(times[k], new Vector3(values[k * 3], values[k * 3 + 1], values[k * 3 + 2])));
                        break;
                }
            }

            if (byBone.Count == 0)
                continue;

            var channels = new List<BoneChannel>();
            foreach ((int bone, var tracks) in byBone) {
                string boneName = boneNames is not null && (uint)bone < (uint)boneNames.Length ? boneNames[bone] : "";
                channels.Add(new BoneChannel(bone, boneName, tracks.pos.ToArray(), tracks.rot.ToArray(), tracks.scale.ToArray()));
            }

            var name = anim.TryGetProperty("name", out JsonElement an) ? an.GetString() : $"Clip{clipNumber}";
            clips.Add(new AnimationClipData(name, maxTime, 1f, channels.ToArray()));
            clipNumber++;
        }
        return clips.ToArray();
    }
}
