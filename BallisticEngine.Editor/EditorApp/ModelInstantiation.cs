using BallisticEngine.AssetPipeline;

namespace BallisticEngine.Editor;

internal static class ModelInstantiation {
    public static bool IsModel(Guid guid) =>
        AssetDatabase.TryGetMeta(guid, out MetaFile meta) && meta.Importer == "ModelImporter";

    public static Entity Instantiate(Scene scene, Guid guid) {
        Mesh mesh = AssetDatabase.Load<Mesh>(guid);
        if (mesh is null)
            return null;

        var assetPath = AssetDatabase.GuidToAssetPath(guid);
        var name = string.IsNullOrEmpty(assetPath) ? "Model" : Path.GetFileNameWithoutExtension(assetPath);

        if (!SplitByNodes(guid) || mesh.SubMeshes.Length == 0)
            return CreateWholeMeshEntity(scene, name, mesh);

        if (HasNodeTree(mesh))
            return InstantiateNodeTree(scene, name, mesh);

        return InstantiateFlat(scene, name, mesh);
    }

    static bool SplitByNodes(Guid guid) =>
        AssetDatabase.TryGetMeta(guid, out MetaFile meta) &&
        (meta.Settings?["splitByNodes"]?.GetValue<bool>() ?? true);

    static bool HasNodeTree(Mesh mesh) =>
        mesh.Nodes.Length > 0 &&
        mesh.SubMeshes.Any(s => s.NodeIndex >= 0 && s.NodeIndex < mesh.Nodes.Length);

    static Entity InstantiateNodeTree(Scene scene, string name, Mesh mesh) {
        MeshNodeData[] nodes = mesh.Nodes;

        var subsByNode = new List<int>[nodes.Length];
        for (var i = 0; i < mesh.SubMeshes.Length; i++) {
            var n = mesh.SubMeshes[i].NodeIndex;
            if (n < 0 || n >= nodes.Length)
                continue;
            (subsByNode[n] ??= new List<int>()).Add(i);
        }

        var needed = new bool[nodes.Length];
        for (var n = 0; n < nodes.Length; n++) {
            if (subsByNode[n] is null)
                continue;
            for (var a = n; a >= 0 && !needed[a]; a = nodes[a].ParentIndex)
                needed[a] = true;
        }

        var entities = new Entity[nodes.Length];
        Entity root = null;
        for (var n = 0; n < nodes.Length; n++) {
            if (!needed[n])
                continue;

            var parentIndex = nodes[n].ParentIndex;
            var isRoot = parentIndex < 0 || entities[parentIndex] is null;

            Entity entity = scene.CreateEntity(isRoot ? name : NodeName(nodes[n].Name, n));
            if (!isRoot)
                entity.transform.SetParent(entities[parentIndex].transform);
            ApplyTransform(entity.transform, nodes[n].LocalTransform);
            entities[n] = entity;
            root ??= entity;

            if (subsByNode[n] is not { } subs)
                continue;

            if (subs.Count == 1) {
                AttachRenderer(entity, mesh, subs[0]);
            }
            else {
                foreach (var s in subs) {
                    Entity part = scene.CreateEntity(SubMeshName(mesh, s, entity.Name));
                    part.transform.SetParent(entity.transform);
                    AttachRenderer(part, mesh, s);
                }
            }
        }

        return root;
    }

    static Entity InstantiateFlat(Scene scene, string name, Mesh mesh) {
        if (mesh.SubMeshes.Length == 1) {
            Entity only = scene.CreateEntity(SubMeshName(mesh, 0, name));
            ApplyTransform(only.transform, mesh.SubMeshes[0].NodeTransform);
            AttachRenderer(only, mesh, 0);
            return only;
        }

        Entity root = scene.CreateEntity(name);
        for (var i = 0; i < mesh.SubMeshes.Length; i++) {
            Entity child = scene.CreateEntity(SubMeshName(mesh, i, name));
            child.transform.SetParent(root.transform);
            ApplyTransform(child.transform, mesh.SubMeshes[i].NodeTransform);
            AttachRenderer(child, mesh, i);
        }
        return root;
    }

    static Entity CreateWholeMeshEntity(Scene scene, string name, Mesh mesh) {
        Entity entity = scene.CreateEntity(name);
        AttachRenderer(entity, mesh, -1);
        return entity;
    }

    static string NodeName(string name, int index) =>
        string.IsNullOrEmpty(name) ? $"Node {index}" : name;

    static string SubMeshName(Mesh mesh, int index, string fallback) {
        var name = mesh.SubMeshes[index].Name;
        return string.IsNullOrEmpty(name) ? $"{fallback} {index}" : name;
    }

    static void ApplyTransform(Transform transform, Matrix4 matrix) {
        transform.Scale = matrix.ExtractScale();
        transform.Rotation = matrix.ExtractRotation();
        transform.Position = matrix.ExtractTranslation();
    }

    static void AttachRenderer(Entity entity, Mesh mesh, int subMeshIndex) {
        var renderer = (StaticMeshRenderer)entity.AddComponent(typeof(StaticMeshRenderer));
        renderer.SharedMesh = mesh;
        renderer.SubMeshIndex = subMeshIndex;
    }
}
