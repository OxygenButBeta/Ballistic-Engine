using Assimp;

namespace BallisticEngine.AssetPipeline;

public sealed class DecodedMaterial {
    public string Name;
    public readonly Dictionary<TextureType, string> TexturePaths = new();

    public Vector4? BaseColor;
    public float? Metallic;
    public float? Roughness;
    public Vector3? EmissiveColor;
    public float? Opacity;
}

public sealed class DecodedModel {
    public MeshData Mesh;
    public DecodedMaterial[] SubMeshMaterials;
}

public static class AssimpMeshDecoder {
    public static MeshData Decode(string path, bool flipUVs = true, int meshIndex = 0) {
        AssimpContext context = new();

        PostProcessSteps steps = PostProcessSteps.Triangulate;
        if (flipUVs)
            steps |= PostProcessSteps.FlipUVs;

        Assimp.Scene scene = context.ImportFile(path, steps);

        if (scene == null || scene.MeshCount == 0)
            throw new IOException($"Mesh import failed or no meshes found in '{path}'.");

        if (meshIndex < 0 || meshIndex >= scene.MeshCount)
            throw new IOException(
                $"Mesh index {meshIndex} is out of range; '{path}' contains {scene.MeshCount} meshes.");

        Assimp.Mesh mesh = scene.Meshes[meshIndex];

        var builder = new SubMeshBuilder();
        builder.Append(mesh, Matrix4.Identity);
        return Combine([builder]).Mesh;
    }

    public static DecodedModel DecodeScene(string path, bool flipUVs = true, bool splitByNodes = false,
        float scaleFactor = 0f) {
        AssimpContext context = new();

        PostProcessSteps steps = PostProcessSteps.Triangulate
                                 | PostProcessSteps.GenerateSmoothNormals
                                 | PostProcessSteps.CalculateTangentSpace
                                 | PostProcessSteps.JoinIdenticalVertices;
        if (flipUVs)
            steps |= PostProcessSteps.FlipUVs;

        Assimp.Scene scene = context.ImportFile(path, steps);

        if (scene == null || scene.MeshCount == 0)
            throw new IOException($"Mesh import failed or no meshes found in '{path}'.");

        float unitScale = scaleFactor > 0f ? scaleFactor : AutoUnitScale(path);
        Matrix4 rootScale = Matrix4.CreateScale(unitScale);

        if (Path.GetExtension(path).Equals(".fbx", StringComparison.OrdinalIgnoreCase) &&
            FbxUnitScaleFactor.ReadUpAxis(path) == 2)
            rootScale = Matrix4.CreateRotationX(-MathF.PI / 2f) * rootScale;

        var builders = new List<SubMeshBuilder>();
        var builderMaterials = new List<int>();
        var builderByMaterial = new Dictionary<int, SubMeshBuilder>();
        var nodes = new List<MeshNodeData>();

        void Traverse(Node node, Matrix4 parentWorld, int parentIndex) {
            Matrix4 local = parentIndex < 0 ? rootScale * ToOpenTK(node.Transform) : ToOpenTK(node.Transform);
            Matrix4 world = local * parentWorld;

            var nodeIndex = -1;
            if (splitByNodes) {
                nodeIndex = nodes.Count;
                nodes.Add(new MeshNodeData(string.IsNullOrEmpty(node.Name) ? null : node.Name,
                    parentIndex, local));
            }

            for (var n = 0; n < node.MeshIndices.Count; n++) {
                Assimp.Mesh mesh = scene.Meshes[node.MeshIndices[n]];
                if (mesh.VertexCount == 0 || mesh.FaceCount == 0)
                    continue;

                SubMeshBuilder builder;
                if (splitByNodes) {
                    builder = new SubMeshBuilder {
                        Name = node.MeshIndices.Count > 1 ? $"{node.Name}.{n}" : node.Name,
                        NodeTransform = world,
                        NodeIndex = nodeIndex,
                    };
                    builders.Add(builder);
                    builderMaterials.Add(mesh.MaterialIndex);
                }
                else if (!builderByMaterial.TryGetValue(mesh.MaterialIndex, out builder)) {
                    builder = new SubMeshBuilder();
                    builderByMaterial[mesh.MaterialIndex] = builder;
                    builders.Add(builder);
                    builderMaterials.Add(mesh.MaterialIndex);
                }

                builder.Append(mesh, world);
            }

            foreach (Node child in node.Children)
                Traverse(child, world, nodeIndex);
        }

        Traverse(scene.RootNode, Matrix4.Identity, -1);

        if (builders.Count == 0)
            throw new IOException($"'{path}' contains meshes, but none are referenced by its node hierarchy.");

        var materialByIndex = new Dictionary<int, DecodedMaterial>();
        var materials = new DecodedMaterial[builders.Count];
        for (var i = 0; i < builders.Count; i++) {
            var materialIndex = builderMaterials[i];
            if (!materialByIndex.TryGetValue(materialIndex, out DecodedMaterial material)) {
                material = DecodeMaterial(scene, materialIndex);
                materialByIndex[materialIndex] = material;
            }

            builders[i].Name ??= material?.Name;
            materials[i] = material;
        }

        DecodedModel model = Combine(builders, nodes.ToArray());
        model.SubMeshMaterials = materials;
        return model;
    }

    static float AutoUnitScale(string path) {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext != ".fbx")
            return 1f;

        double cmPerUnit = FbxUnitScaleFactor.Read(path) ?? 1.0;
        if (cmPerUnit <= 0 || double.IsNaN(cmPerUnit) || double.IsInfinity(cmPerUnit))
            cmPerUnit = 1.0;
        return (float)(cmPerUnit / 100.0);
    }

    internal static DecodedMaterial DecodeMaterialPublic(Assimp.Scene scene, int materialIndex) =>
        DecodeMaterial(scene, materialIndex);

    static DecodedMaterial DecodeMaterial(Assimp.Scene scene, int materialIndex) {
        if (materialIndex < 0 || materialIndex >= scene.MaterialCount)
            return null;

        Assimp.Material source = scene.Materials[materialIndex];
        var decoded = new DecodedMaterial { Name = source.HasName ? source.Name : $"Material {materialIndex}" };

        Map(source, decoded, TextureType.Diffuse, Assimp.TextureType.Diffuse);
        Map(source, decoded, TextureType.Normal, Assimp.TextureType.Normals, Assimp.TextureType.Height);
        Map(source, decoded, TextureType.Metallic, Assimp.TextureType.Specular);
        Map(source, decoded, TextureType.Roughness, Assimp.TextureType.Shininess);
        Map(source, decoded, TextureType.AO, Assimp.TextureType.Lightmap, Assimp.TextureType.Ambient);
        Map(source, decoded, TextureType.Emissive, Assimp.TextureType.Emissive);

        DecodeScalars(source, decoded);
        return decoded;
    }

    static void DecodeScalars(Assimp.Material source, DecodedMaterial decoded) {
        if (source.HasColorDiffuse) {
            Color4D c = source.ColorDiffuse;
            if (c.R < 0.999f || c.G < 0.999f || c.B < 0.999f || c.A < 0.999f)
                decoded.BaseColor = new Vector4(c.R, c.G, c.B, c.A);
        }

        if (source.HasColorEmissive) {
            Color4D e = source.ColorEmissive;
            if (e.R > 0.001f || e.G > 0.001f || e.B > 0.001f)
                decoded.EmissiveColor = new Vector3(e.R, e.G, e.B);
        }

        if (source.HasOpacity && source.Opacity < 0.999f)
            decoded.Opacity = source.Opacity;

        if (TryGetFloat(source, "$mat.metallicFactor", out var metallic) ||
            TryGetFloat(source, "$mat.gltf.pbrMetallicRoughness.metallicFactor", out metallic) ||
            TryGetFloat(source, "$mat.reflectivity", out metallic))
            decoded.Metallic = Math.Clamp(metallic, 0f, 1f);

        if (TryGetFloat(source, "$mat.roughnessFactor", out var roughness) ||
            TryGetFloat(source, "$mat.gltf.pbrMetallicRoughness.roughnessFactor", out roughness))
            decoded.Roughness = Math.Clamp(roughness, 0f, 1f);
        else if (source.HasShininess && source.Shininess > 0f) decoded.Roughness = Math.Clamp(MathF.Sqrt(2f / (source.Shininess + 2f)), 0.02f, 1f);
    }

    static bool TryGetFloat(Assimp.Material source, string key, out float value) {
        value = 0f;
        MaterialProperty property = source.GetProperty($"{key},0,0");
        if (property is null || property.PropertyType != PropertyType.Float)
            return false;
        value = property.GetFloatValue();
        return true;
    }

    static void Map(Assimp.Material source, DecodedMaterial decoded, TextureType slot,
        params Assimp.TextureType[] candidates) {
        foreach (Assimp.TextureType candidate in candidates) {
            if (!source.GetMaterialTexture(candidate, 0, out TextureSlot texture))
                continue;
            if (string.IsNullOrWhiteSpace(texture.FilePath) || texture.FilePath.StartsWith('*'))
                continue;

            decoded.TexturePaths[slot] = texture.FilePath;
            return;
        }
    }

    sealed class SubMeshBuilder {
        public string Name;
        public Matrix4 NodeTransform = Matrix4.Identity;
        public int NodeIndex = -1;
        public readonly List<Vector3> Positions = new();
        public readonly List<Vector3> Normals = new();
        public readonly List<Vector4> Tangents = new();
        public readonly List<Vector2> UVs = new();
        public readonly List<uint> Indices = new();

        public void Append(Assimp.Mesh mesh, Matrix4 world) {
            var baseVertex = (uint)Positions.Count;

            var linear = new Matrix3(world);
            Matrix3 normalMatrix = linear;
            var mirrored = false;
            if (Math.Abs(linear.Determinant) > 1e-12f) {
                mirrored = linear.Determinant < 0f;
                normalMatrix = Matrix3.Transpose(Matrix3.Invert(linear));
            }

            var hasUVs = mesh.TextureCoordinateChannelCount > 0 && mesh.HasTextureCoords(0);
            var hasNormals = mesh.HasNormals;
            var hasTangents = mesh.HasTangentBasis;

            for (var i = 0; i < mesh.VertexCount; i++) {
                Positions.Add(MulPoint(ToVector3(mesh.Vertices[i]), in world));

                Vector3 n = hasNormals
                    ? SafeNormalize(MulVector(ToVector3(mesh.Normals[i]), in normalMatrix), Vector3.UnitY)
                    : Vector3.UnitY;
                Normals.Add(n);

                if (hasTangents) {
                    Vector3 t = SafeNormalize(MulVector(ToVector3(mesh.Tangents[i]), in linear), Vector3.UnitX);
                    Vector3 b = SafeNormalize(MulVector(ToVector3(mesh.BiTangents[i]), in linear), Vector3.UnitY);
                    var w = Vector3.Dot(Vector3.Cross(n, t), b) < 0f ? -1f : 1f;
                    Tangents.Add(new Vector4(t, w));
                }
                else {
                    Tangents.Add(new Vector4(Vector3.UnitX, 1f));
                }

                if (hasUVs) {
                    Vector3D uv = mesh.TextureCoordinateChannels[0][i];
                    UVs.Add(new Vector2(uv.X, uv.Y));
                }
                else {
                    UVs.Add(Vector2.Zero);
                }
            }

            foreach (Face face in mesh.Faces) {
                if (face.IndexCount != 3)
                    continue;

                if (mirrored) {
                    Indices.Add(baseVertex + (uint)face.Indices[2]);
                    Indices.Add(baseVertex + (uint)face.Indices[1]);
                    Indices.Add(baseVertex + (uint)face.Indices[0]);
                }
                else {
                    Indices.Add(baseVertex + (uint)face.Indices[0]);
                    Indices.Add(baseVertex + (uint)face.Indices[1]);
                    Indices.Add(baseVertex + (uint)face.Indices[2]);
                }
            }
        }
    }

    static DecodedModel Combine(List<SubMeshBuilder> builders, MeshNodeData[] nodes = null) {
        var vertexTotal = builders.Sum(b => b.Positions.Count);
        var indexTotal = builders.Sum(b => b.Indices.Count);

        var positions = new Vector3[vertexTotal];
        var normals = new Vector3[vertexTotal];
        var tangents = new Vector4[vertexTotal];
        var uvs = new Vector2[vertexTotal];
        var indices = new uint[indexTotal];
        var subMeshes = new SubMeshData[builders.Count];

        var vertexOffset = 0;
        var indexOffset = 0;
        for (var s = 0; s < builders.Count; s++) {
            SubMeshBuilder builder = builders[s];

            builder.Positions.CopyTo(positions, vertexOffset);
            builder.Normals.CopyTo(normals, vertexOffset);
            builder.Tangents.CopyTo(tangents, vertexOffset);
            builder.UVs.CopyTo(uvs, vertexOffset);

            for (var i = 0; i < builder.Indices.Count; i++)
                indices[indexOffset + i] = builder.Indices[i] + (uint)vertexOffset;

            subMeshes[s] = new SubMeshData(builder.Name, indexOffset, builder.Indices.Count, null,
                builder.NodeTransform, builder.NodeIndex);

            vertexOffset += builder.Positions.Count;
            indexOffset += builder.Indices.Count;
        }

        return new DecodedModel {
            Mesh = new MeshData(positions, indices, uvs, normals, tangents, subMeshes, nodes),
        };
    }

    internal static Matrix4 ToOpenTKMatrix(in Assimp.Matrix4x4 m) => ToOpenTK(m);

    static Matrix4 ToOpenTK(in Assimp.Matrix4x4 m) => new(
        m.A1, m.B1, m.C1, m.D1,
        m.A2, m.B2, m.C2, m.D2,
        m.A3, m.B3, m.C3, m.D3,
        m.A4, m.B4, m.C4, m.D4);

    static Vector3 ToVector3(in Vector3D v) => new(v.X, v.Y, v.Z);

    static Vector3 MulPoint(in Vector3 v, in Matrix4 m) => new(
        v.X * m.M11 + v.Y * m.M21 + v.Z * m.M31 + m.M41,
        v.X * m.M12 + v.Y * m.M22 + v.Z * m.M32 + m.M42,
        v.X * m.M13 + v.Y * m.M23 + v.Z * m.M33 + m.M43);

    static Vector3 MulVector(in Vector3 v, in Matrix3 m) => new(
        v.X * m.M11 + v.Y * m.M21 + v.Z * m.M31,
        v.X * m.M12 + v.Y * m.M22 + v.Z * m.M32,
        v.X * m.M13 + v.Y * m.M23 + v.Z * m.M33);

    static Vector3 SafeNormalize(Vector3 v, Vector3 fallback) {
        var length = v.Length();
        return length > 1e-12f ? v / length : fallback;
    }
}
