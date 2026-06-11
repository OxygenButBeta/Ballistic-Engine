using Assimp;
using OpenTK.Mathematics;

namespace BallisticEngine.AssetPipeline;

// A source material's texture bindings, as authored in the model file. Paths are the raw
// strings Assimp reports (absolute, relative, or bare filenames) — the importer resolves them.
public sealed class DecodedMaterial {
    public string Name;
    public readonly Dictionary<TextureType, string> TexturePaths = new();
}

// Whole-model decode result: merged geometry (one submesh per used source material, node
// transforms baked into the vertices) plus the materials those submeshes reference.
// SubMeshMaterials is parallel to Mesh.SubMeshes; Mesh.SubMeshes[i].MaterialRef is left null —
// the importer fills it after it has generated .mat assets.
public sealed class DecodedModel {
    public MeshData Mesh;
    public DecodedMaterial[] SubMeshMaterials;
}

// The only place in the engine that talks to Assimp.
public static class AssimpMeshDecoder {
    // Legacy single-mesh decode (meshIndex >= 0 in the importer settings). Geometry only,
    // mesh-local space, no materials — exactly the pre-submesh pipeline behavior.
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

    // Whole-scene decode: walks the node hierarchy, bakes each node's world transform into its
    // vertices, and merges everything into one mesh with a submesh per used source material.
    public static DecodedModel DecodeScene(string path, bool flipUVs = true) {
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

        // One builder per source material that geometry actually uses, in first-use order.
        var builderByMaterial = new Dictionary<int, SubMeshBuilder>();
        var materialOrder = new List<int>();

        void Traverse(Node node, Matrix4 parentWorld) {
            Matrix4 world = ToOpenTK(node.Transform) * parentWorld;

            foreach (var meshIndex in node.MeshIndices) {
                Assimp.Mesh mesh = scene.Meshes[meshIndex];
                if (mesh.VertexCount == 0 || mesh.FaceCount == 0)
                    continue;

                if (!builderByMaterial.TryGetValue(mesh.MaterialIndex, out SubMeshBuilder builder)) {
                    builder = new SubMeshBuilder();
                    builderByMaterial[mesh.MaterialIndex] = builder;
                    materialOrder.Add(mesh.MaterialIndex);
                }

                builder.Append(mesh, world);
            }

            foreach (Node child in node.Children)
                Traverse(child, world);
        }

        Traverse(scene.RootNode, Matrix4.Identity);

        if (materialOrder.Count == 0)
            throw new IOException($"'{path}' contains meshes, but none are referenced by its node hierarchy.");

        var builders = new List<SubMeshBuilder>(materialOrder.Count);
        var materials = new List<DecodedMaterial>(materialOrder.Count);
        foreach (var materialIndex in materialOrder) {
            builders.Add(builderByMaterial[materialIndex]);
            DecodedMaterial material = DecodeMaterial(scene, materialIndex);
            builderByMaterial[materialIndex].Name = material?.Name;
            materials.Add(material);
        }

        DecodedModel model = Combine(builders);
        model.SubMeshMaterials = materials.ToArray();
        return model;
    }

    // ---- Materials ---------------------------------------------------------

    static DecodedMaterial DecodeMaterial(Assimp.Scene scene, int materialIndex) {
        if (materialIndex < 0 || materialIndex >= scene.MaterialCount)
            return null;

        Assimp.Material source = scene.Materials[materialIndex];
        var decoded = new DecodedMaterial { Name = source.HasName ? source.Name : $"Material {materialIndex}" };

        // Assimp 4.x has no PBR slots; Specular/Shininess are the closest FBX carriers for
        // metallic/roughness maps. Embedded textures ("*0" paths) are not supported.
        Map(source, decoded, TextureType.Diffuse, Assimp.TextureType.Diffuse);
        Map(source, decoded, TextureType.Normal, Assimp.TextureType.Normals, Assimp.TextureType.Height);
        Map(source, decoded, TextureType.Metallic, Assimp.TextureType.Specular);
        Map(source, decoded, TextureType.Roughness, Assimp.TextureType.Shininess);
        Map(source, decoded, TextureType.AO, Assimp.TextureType.Lightmap, Assimp.TextureType.Ambient);

        return decoded;
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

    // ---- Geometry ----------------------------------------------------------

    // Accumulates transformed geometry for one output submesh.
    sealed class SubMeshBuilder {
        public string Name;
        public readonly List<Vector3> Positions = new();
        public readonly List<Vector3> Normals = new();
        public readonly List<Vector3> Tangents = new();
        public readonly List<Vector2> UVs = new();
        public readonly List<uint> Indices = new();

        public void Append(Assimp.Mesh mesh, Matrix4 world) {
            var baseVertex = (uint)Positions.Count;

            // Row-vector convention: normals transform by the inverse-transpose of the linear part.
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

                Normals.Add(hasNormals
                    ? SafeNormalize(MulVector(ToVector3(mesh.Normals[i]), in normalMatrix), Vector3.UnitY)
                    : Vector3.UnitY);

                Tangents.Add(hasTangents
                    ? SafeNormalize(MulVector(ToVector3(mesh.Tangents[i]), in linear), Vector3.UnitX)
                    : Vector3.UnitX);

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
                    // A mirroring transform flips winding; reverse it to keep faces front-facing.
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

    static DecodedModel Combine(List<SubMeshBuilder> builders) {
        var vertexTotal = builders.Sum(b => b.Positions.Count);
        var indexTotal = builders.Sum(b => b.Indices.Count);

        var positions = new Vector3[vertexTotal];
        var normals = new Vector3[vertexTotal];
        var tangents = new Vector3[vertexTotal];
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

            subMeshes[s] = new SubMeshData(builder.Name, indexOffset, builder.Indices.Count, null);

            vertexOffset += builder.Positions.Count;
            indexOffset += builder.Indices.Count;
        }

        return new DecodedModel {
            Mesh = new MeshData(positions, indices, uvs, normals, tangents, subMeshes),
        };
    }

    // ---- Math helpers ------------------------------------------------------

    // Assimp matrices are column-vector (v' = M * v); OpenTK composes row-vector (v' = v * M).
    // Transposing converts between the conventions.
    static Matrix4 ToOpenTK(in Matrix4x4 m) => new(
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
        var length = v.Length;
        return length > 1e-12f ? v / length : fallback;
    }
}
