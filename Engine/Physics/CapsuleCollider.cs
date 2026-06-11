using OpenTK.Mathematics;

namespace BallisticEngine;

// Capsule along the entity's local Y axis (character-shaped). Height is the TOTAL height
// including both hemispherical caps, Unity-style; it clamps to at least 2 * Radius.
[Component("Capsule Collider", "Physics")]
public class CapsuleCollider : Collider {
    [Range(0.001f, 100f)]
    public float Radius { get; set; } = 0.5f;

    [Range(0.001f, 100f)]
    public float Height { get; set; } = 2f;

    internal override PhysicsShape BuildShape(Vector3 worldScale) {
        float radius = Radius * MathF.Max(MathF.Abs(worldScale.X), MathF.Abs(worldScale.Z));
        float cylinderLength = MathF.Max(0f, Height * MathF.Abs(worldScale.Y) - 2f * radius);
        return new CapsuleShape(radius, cylinderLength);
    }

    private protected override void AutoFitToRenderMesh() {
        if (Radius != 0.5f || Height != 2f || Center != Vector3.Zero)
            return;
        if (!TryGetRenderMeshBounds(out Vector3 min, out Vector3 max))
            return;
        Center = (min + max) * 0.5f;
        Vector3 extents = (max - min) * 0.5f;
        Radius = MathF.Max(MathF.Max(extents.X, extents.Z), 0.001f);
        Height = MathF.Max(max.Y - min.Y, 2f * Radius);
    }

    public override void OnDrawGizmosSelected(IGizmos gizmos) {
        gizmos.Color = new Vector3(0.35f, 1f, 0.4f);

        Vector3 scale = transform.WorldMatrix.ExtractScale();
        float radius = Radius * MathF.Max(MathF.Abs(scale.X), MathF.Abs(scale.Z));
        float halfSegment = MathF.Max(0f, Height * MathF.Abs(scale.Y) - 2f * radius) * 0.5f;

        Vector3 center = GizmoCenter;
        Vector3 up = transform.WorldRotation * Vector3.UnitY;
        Vector3 right = transform.WorldRotation * Vector3.UnitX;
        Vector3 forward = transform.WorldRotation * Vector3.UnitZ;
        Vector3 top = center + up * halfSegment;
        Vector3 bottom = center - up * halfSegment;

        gizmos.DrawWireSphere(top, radius);
        gizmos.DrawWireSphere(bottom, radius);
        gizmos.DrawLine(top + right * radius, bottom + right * radius);
        gizmos.DrawLine(top - right * radius, bottom - right * radius);
        gizmos.DrawLine(top + forward * radius, bottom + forward * radius);
        gizmos.DrawLine(top - forward * radius, bottom - forward * radius);
    }
}
