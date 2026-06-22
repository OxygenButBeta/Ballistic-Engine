
namespace BallisticEngine.Editor;

internal enum PrimitiveKind { Cube, Sphere, Plane }

internal static class Primitives {
    public const string DefaultMaterialPath = "Assets/Default/Materials/Default.mat";
    public const string MeshAssetFolder = "Assets/Default/Meshes";

    static Material fallbackMaterial;

    public static Entity Create(Scene scene, PrimitiveKind kind) {
        Entity entity = scene.CreateEntity(kind.ToString());
        var renderer = (StaticMeshRenderer)entity.AddComponent(typeof(StaticMeshRenderer));
        (Mesh mesh, Material material) = Build(kind);
        renderer.SharedMesh = mesh;
        renderer.SharedMaterial = material;
        return entity;
    }

    public static (Mesh mesh, Material material) Build(PrimitiveKind kind) {
        Mesh mesh = LoadMeshAsset(kind) ?? Mesh.Create(BuildData(kind));
        return (mesh, DefaultMaterial());
    }

    static Mesh LoadMeshAsset(PrimitiveKind kind) =>
        AssetDatabase.Load<Mesh>($"{MeshAssetFolder}/{kind}.obj");

    static MeshData BuildData(PrimitiveKind kind) => kind switch {
        PrimitiveKind.Cube => Cube(),
        PrimitiveKind.Sphere => Sphere(1f, 32, 24),
        PrimitiveKind.Plane => Plane(),
        _ => Cube(),
    };

    public static Material DefaultMaterial() {
        Material asset = AssetDatabase.Load<Material>(DefaultMaterialPath);
        if (asset is not null)
            return asset;

        if (fallbackMaterial is not null)
            return fallbackMaterial;
        var shader = AssetDatabase.LoadRef<StandardShader>("Assets/Default/Shaders/Standard.shader");
        if (shader is null)
            return null;
        fallbackMaterial = BallisticEngine.Material.Create(shader, DefaultTextures.Neutral(TextureType.Diffuse));
        return fallbackMaterial;
    }

    static MeshData Cube() {
        var verts = new List<Vector3>();
        var norms = new List<Vector3>();
        var uvs = new List<Vector2>();
        var indices = new List<uint>();

        void Face(Vector3 origin, Vector3 right, Vector3 up) {
            Vector3 normal = Vector3.Cross(right, up).Normalized();
            uint b = (uint)verts.Count;
            verts.Add(origin);
            verts.Add(origin + right);
            verts.Add(origin + right + up);
            verts.Add(origin + up);
            for (var i = 0; i < 4; i++) norms.Add(normal);
            uvs.Add(new Vector2(0, 0)); uvs.Add(new Vector2(1, 0));
            uvs.Add(new Vector2(1, 1)); uvs.Add(new Vector2(0, 1));
            indices.AddRange([b, b + 1, b + 2, b, b + 2, b + 3]);
        }

        const float h = 0.5f;
        Face(new Vector3(-h, -h, h), new Vector3(1, 0, 0), new Vector3(0, 1, 0));
        Face(new Vector3(h, -h, -h), new Vector3(-1, 0, 0), new Vector3(0, 1, 0));
        Face(new Vector3(h, -h, h), new Vector3(0, 0, -1), new Vector3(0, 1, 0));
        Face(new Vector3(-h, -h, -h), new Vector3(0, 0, 1), new Vector3(0, 1, 0));
        Face(new Vector3(-h, h, h), new Vector3(1, 0, 0), new Vector3(0, 0, -1));
        Face(new Vector3(-h, -h, -h), new Vector3(1, 0, 0), new Vector3(0, 0, 1));

        return Build(verts, indices, uvs, norms);
    }

    static MeshData Plane() {
        const float h = 2.5f;
        var verts = new List<Vector3> {
            new(-h, 0, h), new(h, 0, h), new(h, 0, -h), new(-h, 0, -h),
        };
        var norms = new List<Vector3> { Vector3.UnitY, Vector3.UnitY, Vector3.UnitY, Vector3.UnitY };
        var uvs = new List<Vector2> { new(0, 0), new(1, 0), new(1, 1), new(0, 1) };
        var indices = new List<uint> { 0, 1, 2, 0, 2, 3 };
        return Build(verts, indices, uvs, norms);
    }

    static MeshData Sphere(float radius, int longitudes, int latitudes) {
        var verts = new List<Vector3>();
        var norms = new List<Vector3>();
        var uvs = new List<Vector2>();
        var indices = new List<uint>();

        for (var lat = 0; lat <= latitudes; lat++) {
            float theta = lat * MathF.PI / latitudes;
            float sinT = MathF.Sin(theta), cosT = MathF.Cos(theta);
            for (var lon = 0; lon <= longitudes; lon++) {
                float phi = lon * MathF.Tau / longitudes;
                var n = new Vector3(MathF.Cos(phi) * sinT, cosT, MathF.Sin(phi) * sinT);
                verts.Add(n * radius);
                norms.Add(n);
                uvs.Add(new Vector2(lon / (float)longitudes, lat / (float)latitudes));
            }
        }

        int stride = longitudes + 1;
        for (var lat = 0; lat < latitudes; lat++) {
            for (var lon = 0; lon < longitudes; lon++) {
                uint a = (uint)(lat * stride + lon);
                uint b = (uint)(a + stride);
                indices.AddRange([a, b, a + 1, a + 1, b, b + 1]);
            }
        }

        return Build(verts, indices, uvs, norms);
    }

    static MeshData Build(List<Vector3> verts, List<uint> indices, List<Vector2> uvs, List<Vector3> norms) {
        var tangents = new Vector4[verts.Count];
        Array.Fill(tangents, new Vector4(1f, 0f, 0f, 1f));
        return new MeshData(verts.ToArray(), indices.ToArray(), uvs.ToArray(), norms.ToArray(), tangents);
    }
}
