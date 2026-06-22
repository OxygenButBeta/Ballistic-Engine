
namespace BallisticEngine;

[Component("Capsule Shadow Caster", "Rendering")]
public class CapsuleShadowCaster : Behaviour {
    [Header("Capsule")]
    [Tooltip("Capsule radius in world units (before transform scale).")]
    [Range(0.01f, 10f)]
    public float Radius { get; set; } = 0.4f;

    [Tooltip("Total capsule height including both hemispherical caps (Unity-style), along the entity's local Y.")]
    [Range(0.02f, 20f)]
    public float Height { get; set; } = 1.8f;

    [Tooltip("Local-space offset of the capsule centre from the entity origin.")]
    public Vector3 Center { get; set; } = Vector3.Zero;

    public void GetWorldSegment(out Vector3 a, out Vector3 b, out float worldRadius) {
        Vector3 scale = transform.WorldMatrix.ExtractScale();
        worldRadius = Radius * MathF.Max(MathF.Abs(scale.X), MathF.Abs(scale.Z));
        float halfSegment = MathF.Max(0f, Height * MathF.Abs(scale.Y) - 2f * worldRadius) * 0.5f;

        Vector3 center = Vector3.Transform(Center, transform.WorldMatrix);
        Vector3 up = Vector3.Transform(Vector3.UnitY, transform.WorldRotation);
        a = center + up * halfSegment;
        b = center - up * halfSegment;
    }

    protected internal override void OnAttach() {
        if (!RuntimeSet<CapsuleShadowCaster>.Contains(this))
            RuntimeSet<CapsuleShadowCaster>.Add(this);
    }

    protected internal override void OnDetach() {
        RuntimeSet<CapsuleShadowCaster>.Remove(this);
    }

    public override void OnDrawGizmosSelected(IGizmos gizmos) {
        gizmos.Color = new Vector3(0.3f, 0.7f, 1f);
        GetWorldSegment(out Vector3 top, out Vector3 bottom, out float radius);

        Vector3 right = Vector3.Transform(Vector3.UnitX, transform.WorldRotation);
        Vector3 forward = Vector3.Transform(Vector3.UnitZ, transform.WorldRotation);

        gizmos.DrawWireSphere(top, radius);
        gizmos.DrawWireSphere(bottom, radius);
        gizmos.DrawLine(top + right * radius, bottom + right * radius);
        gizmos.DrawLine(top - right * radius, bottom - right * radius);
        gizmos.DrawLine(top + forward * radius, bottom + forward * radius);
        gizmos.DrawLine(top - forward * radius, bottom - forward * radius);
    }
}
