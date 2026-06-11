using OpenTK.Mathematics;

namespace BallisticEngine;

// An entity component that contributes a VolumeProfile to the blended post-process stack
// (Unity's Volume). Global volumes apply everywhere; local volumes apply inside an oriented
// box around the entity (BoxSize scaled by the transform), fading in over BlendDistance
// metres outside it. Higher Priority wins where volumes overlap; Weight scales the whole
// contribution. The renderer evaluates the result through VolumeManager every frame.
[Component("Volume")]
public class Volume : Behaviour {
    [Tooltip("Global volumes affect rendering everywhere; local volumes only within their box.")]
    public bool IsGlobal { get; set; } = true;

    [Tooltip("Volumes with higher priority override lower ones where they overlap.")]
    public float Priority { get; set; }

    [Range(0f, 1f)]
    [Tooltip("Master strength of this volume's contribution (1 = full effect).")]
    public float Weight { get; set; } = 1f;

    [Tooltip("Local volumes only: metres outside the box over which the volume fades in.")]
    public float BlendDistance { get; set; }

    [Tooltip("Local volumes only: size of the volume's box, scaled by the transform.")]
    public Vector3 BoxSize { get; set; } = new(10f, 10f, 10f);

    [Tooltip("The shared .volume asset holding this volume's overrides.")]
    public VolumeProfile Profile { get; set; }

    // OnAttach/OnDetach (not OnEnabled) so volumes work in the editor outside play mode.
    protected internal override void OnAttach() => VolumeManager.Register(this);

    protected internal override void OnDetach() => VolumeManager.Unregister(this);

    // 1 inside the box (or always, when global), 0 beyond BlendDistance outside it.
    internal float ComputeInterpFactor(Vector3 cameraPosition) {
        if (IsGlobal)
            return 1f;

        Matrix4 world = transform.WorldMatrix;
        Vector3 scale = world.ExtractScale();
        var half = new Vector3(
            MathF.Abs(BoxSize.X * scale.X),
            MathF.Abs(BoxSize.Y * scale.Y),
            MathF.Abs(BoxSize.Z * scale.Z)) * 0.5f;

        Vector3 local = Vector3.Transform(
            cameraPosition - world.ExtractTranslation(),
            Quaternion.Invert(transform.WorldRotation));
        Vector3 outside = Vector3.ComponentMax(
            new Vector3(MathF.Abs(local.X), MathF.Abs(local.Y), MathF.Abs(local.Z)) - half,
            Vector3.Zero);

        float distance = outside.Length;
        if (distance <= 0f)
            return 1f;

        return BlendDistance > 0f ? Math.Clamp(1f - distance / BlendDistance, 0f, 1f) : 0f;
    }

    public override void OnDrawGizmosSelected(IGizmos gizmos) {
        if (IsGlobal)
            return;

        Vector3 center = transform.WorldPosition;
        Vector3 scale = transform.WorldMatrix.ExtractScale();
        Quaternion rotation = transform.WorldRotation;
        var size = new Vector3(BoxSize.X * scale.X, BoxSize.Y * scale.Y, BoxSize.Z * scale.Z);

        gizmos.Color = new Vector3(0.4f, 0.9f, 0.5f);
        gizmos.DrawWireCube(center, size, rotation);

        if (BlendDistance > 0f) {
            gizmos.Color = new Vector3(0.4f, 0.9f, 0.5f) * 0.5f;
            gizmos.DrawWireCube(center, size + new Vector3(BlendDistance * 2f), rotation);
        }
    }
}
