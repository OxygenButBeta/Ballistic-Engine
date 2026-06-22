namespace BallisticEngine;

public class StaticMeshRenderer : Renderer {
    public override Mesh SharedMesh { get; set; }
    public override Material SharedMaterial { get; set; }

    [HideInInspector]
    public override int SubMeshIndex { get; set; } = -1;

    [HideInInspector]
    public Material[] SharedMaterials { get => MaterialOverrides; set => MaterialOverrides = value; }

    protected internal override void OnAttach() {
        if (!RuntimeSet<IStaticMeshRenderer>.Contains(this))
            RuntimeSet<IStaticMeshRenderer>.Add(this);
    }

    protected internal override void OnDetach() {
        RuntimeSet<IStaticMeshRenderer>.Remove(this);
    }
}
