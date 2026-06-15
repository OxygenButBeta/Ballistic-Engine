using BallisticEngine.AssetPipeline;

namespace BallisticEngine.Editor;

// Creates scene entities for a model asset, Unity-style: models imported with splitByNodes
// reproduce the source file's node hierarchy — one entity per authored node (grouping nodes
// included), each child rendering just its own submesh of the SHARED mesh, so there is no
// geometry duplication. Merged/legacy imports get a single entity with the whole mesh, exactly
// as assigning it by hand did. Callers push undo and handle selection.
internal static class ModelInstantiation {
    // True for assets the model importer owns (the only ones Load<Mesh> can instantiate).
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

        // Split artifact without a node table (BMSH v4): flat children, node pivots only.
        return InstantiateFlat(scene, name, mesh);
    }

    // Mirrors ModelImporter's read: missing key = true (split is the default).
    static bool SplitByNodes(Guid guid) =>
        AssetDatabase.TryGetMeta(guid, out MetaFile meta) &&
        (meta.Settings?["splitByNodes"]?.GetValue<bool>() ?? true);

    static bool HasNodeTree(Mesh mesh) =>
        mesh.Nodes.Length > 0 &&
        mesh.SubMeshes.Any(s => s.NodeIndex >= 0 && s.NodeIndex < mesh.Nodes.Length);

    // ---- Node-tree instantiation (BMSH v5+) ----------------------------------

    static Entity InstantiateNodeTree(Scene scene, string name, Mesh mesh) {
        MeshNodeData[] nodes = mesh.Nodes;

        // Group submeshes by owning node.
        var subsByNode = new List<int>[nodes.Length];
        for (var i = 0; i < mesh.SubMeshes.Length; i++) {
            var n = mesh.SubMeshes[i].NodeIndex;
            if (n < 0 || n >= nodes.Length)
                continue;
            (subsByNode[n] ??= new List<int>()).Add(i);
        }

        // Only materialize nodes that lead to geometry — source files also carry camera,
        // light, and empty locator nodes that would just clutter the hierarchy.
        var needed = new bool[nodes.Length];
        for (var n = 0; n < nodes.Length; n++) {
            if (subsByNode[n] is null)
                continue;
            for (var a = n; a >= 0 && !needed[a]; a = nodes[a].ParentIndex)
                needed[a] = true;
        }

        // Pre-order guarantees a parent is created before its children.
        var entities = new Entity[nodes.Length];
        Entity root = null;
        for (var n = 0; n < nodes.Length; n++) {
            if (!needed[n])
                continue;

            var parentIndex = nodes[n].ParentIndex;
            var isRoot = parentIndex < 0 || entities[parentIndex] is null;

            // The source root carries the file's unit/axis conversion; it becomes the model's
            // root entity, named after the asset.
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
                // Multi-material source objects (Assimp splits them per material): one child
                // per submesh so each part keeps its own material slot and visibility toggle.
                foreach (var s in subs) {
                    Entity part = scene.CreateEntity(SubMeshName(mesh, s, entity.Name));
                    part.transform.SetParent(entity.transform);
                    AttachRenderer(part, mesh, s);
                }
            }
        }

        return root;
    }

    // ---- Flat fallback (BMSH v4 split artifacts, no node table) --------------

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

    // ---- Shared helpers -------------------------------------------------------

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

    // Decomposition loses shear, which TRS transforms can't represent; everything else
    // (including negative scale) survives.
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
