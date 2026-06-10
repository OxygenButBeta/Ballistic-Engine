namespace BallisticEngine;

// Renders an assigned mesh with an assigned material. Does no loading of its own:
//   var renderer = entity.AddComponent<StaticMeshRenderer>();
//   renderer.SharedMesh = AssetDatabase.Load<Mesh>("Assets/.../Model.fbx");
//   renderer.SharedMaterial = AssetDatabase.Load<Material>("Assets/.../Model.mat");
public class StaticMeshRenderer : Renderer {
    public override Mesh SharedMesh { get; set; }
    public override Material SharedMaterial { get; set; }

    protected internal override void OnEnabled() {
        RuntimeSet<IStaticMeshRenderer>.Add(this);
    }

    protected internal override void OnDisabled() {
        RuntimeSet<IStaticMeshRenderer>.Remove(this);
    }
}
