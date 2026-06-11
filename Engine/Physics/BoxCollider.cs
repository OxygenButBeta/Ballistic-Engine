using OpenTK.Mathematics;

namespace BallisticEngine;

[Component("Box Collider", "Physics")]
public class BoxCollider : Collider {
    [Tooltip("Full extents in local units (scaled by the entity's world scale).")]
    public Vector3 Size { get; set; } = Vector3.One;

    internal override PhysicsShape BuildShape(Vector3 worldScale) =>
        new BoxShape(Size * worldScale);

    private protected override void AutoFitToRenderMesh() {
        if (Size != Vector3.One || Center != Vector3.Zero)
            return;
        if (!TryGetRenderMeshBounds(out Vector3 min, out Vector3 max))
            return;
        Center = (min + max) * 0.5f;
        Size = Vector3.ComponentMax(max - min, new Vector3(0.001f));
    }

    public override void OnDrawGizmosSelected(IGizmos gizmos) {
        gizmos.Color = new Vector3(0.35f, 1f, 0.4f);
        gizmos.DrawWireCube(GizmoCenter, Size * transform.WorldMatrix.ExtractScale(), transform.WorldRotation);
    }
}
