namespace BallisticEngine;

// Renders an assigned mesh with an assigned material. Does no loading of its own:
//   var renderer = entity.AddComponent<StaticMeshRenderer>();
//   renderer.SharedMesh = AssetDatabase.Load<Mesh>("Assets/.../Model.fbx");
//   renderer.SharedMaterial = AssetDatabase.Load<Material>("Assets/.../Model.mat");
public class StaticMeshRenderer : Renderer {
    public override Mesh SharedMesh { get; set; }
    public override Material SharedMaterial { get; set; }

    // Which submesh of SharedMesh to draw; -1 = all. Set by model instantiation so each child
    // entity renders just its own part of the shared mesh.
    [HideInInspector]
    public override int SubMeshIndex { get; set; } = -1;

    // Per-submesh material overrides (Unity's sharedMaterials). Serialized here (a base-declared member
    // is excluded from serialization, like SubMeshIndex). Hidden from the attribute inspector because the
    // RendererPreview draws the per-slot list itself; an all-null/absent array is byte-identical to before.
    [HideInInspector]
    public Material[] SharedMaterials { get => MaterialOverrides; set => MaterialOverrides = value; }

    // Register for drawing as soon as we're attached (edit mode too), so the editor viewport
    // shows the mesh without entering play.
    protected internal override void OnAttach() {
        if (!RuntimeSet<IStaticMeshRenderer>.Contains(this))
            RuntimeSet<IStaticMeshRenderer>.Add(this);
    }

    protected internal override void OnDetach() {
        RuntimeSet<IStaticMeshRenderer>.Remove(this);
    }
}
