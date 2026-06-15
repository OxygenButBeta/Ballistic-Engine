
namespace BallisticEngine;

[Component("Sphere Collider", "Physics")]
public class SphereCollider : Collider {
    [Range(0.001f, 100f)]
    public float Radius { get; set; } = 0.5f;

    // Spheres can't scale non-uniformly; take the largest axis like Unity does.
    static float MaxAxis(Vector3 scale) =>
        MathF.Max(MathF.Abs(scale.X), MathF.Max(MathF.Abs(scale.Y), MathF.Abs(scale.Z)));

    internal override PhysicsShape BuildShape(Vector3 worldScale) =>
        new SphereShape(Radius * MaxAxis(worldScale));

    private protected override void AutoFitToRenderMesh() {
        if (Radius != 0.5f || Center != Vector3.Zero)
            return;
        if (!TryGetRenderMeshBounds(out Vector3 min, out Vector3 max))
            return;
        Center = (min + max) * 0.5f;
        Vector3 extents = (max - min) * 0.5f;
        Radius = MathF.Max(MathF.Max(extents.X, MathF.Max(extents.Y, extents.Z)), 0.001f);
    }

    public override void OnDrawGizmosSelected(IGizmos gizmos) {
        gizmos.Color = new Vector3(0.35f, 1f, 0.4f);
        gizmos.DrawWireSphere(GizmoCenter, Radius * MaxAxis(transform.WorldMatrix.ExtractScale()));
    }
}
