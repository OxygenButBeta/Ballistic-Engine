
namespace BallisticEngine;

// An arcade chase camera (GTA / Need-for-Speed): rides behind and above a target vehicle, smoothly
// following its position and heading, always looking at it. Put it on the camera entity (next to the
// HDCamera). By default it auto-targets the scene's VehicleController, so the demo needs zero wiring;
// set TargetName to follow a specific entity instead.
//
// It runs in Tick (the render frame), NOT FixedTick, so the follow is as smooth as the framerate —
// physics steps at 60 Hz but the camera glides between them. All smoothing is exponential damping
// (framerate-correct: the same Smooth value feels identical at 60 fps or 240 fps), so there is no
// stutter and no overshoot. The camera writes its own transform; it must NOT be a child of the car.
[Component("Chase Camera", "Physics")]
public class ChaseCamera : Behaviour {
    [Header("Target")]
    [Tooltip("Entity to follow by name. Empty = auto-find the scene's VehicleController (the car).")]
    public string TargetName { get; set; } = "";

    [Header("Framing")]
    [Tooltip("How far BEHIND the target to sit, in metres (along the target's backward axis).")]
    [Range(1f, 30f)]
    public float Distance { get; set; } = 8f;

    [Tooltip("How far ABOVE the target to sit, in metres.")]
    [Range(0f, 20f)]
    public float Height { get; set; } = 3.5f;

    [Tooltip("Look at a point this far ABOVE the target (aim slightly over the roof, not the wheels).")]
    [Range(0f, 5f)]
    public float LookHeight { get; set; } = 1f;

    [Header("Smoothing")]
    [Tooltip("Position follow speed. Higher = snappier (sticks to the car); lower = floatier. Per second.")]
    [Range(1f, 30f)]
    public float PositionSmooth { get; set; } = 8f;

    [Tooltip("Look/heading follow speed. Usually a touch snappier than position so the car stays centred.")]
    [Range(1f, 40f)]
    public float RotationSmooth { get; set; } = 12f;

    [Header("Speed feel")]
    [Tooltip("Extra metres the camera pulls back at the target's top speed (sense of speed). 0 = off.")]
    [Range(0f, 15f)]
    public float SpeedPullback { get; set; } = 3f;

    [Tooltip("Target speed (m/s) at which the full pullback is reached.")]
    [Range(1f, 120f)]
    public float PullbackAtSpeed { get; set; } = 35f;

    Transform target;          // the vehicle's transform
    Rigidbody targetBody;       // for speed-based pullback (optional)
    Vector3 smoothedPosition;   // exponentially-damped camera position
    Vector3 smoothedLookAt;     // exponentially-damped look target
    bool initialised;

    protected internal override void OnAttach() => ResolveTarget();

    void ResolveTarget() {
        target = null;
        targetBody = null;
        if (!string.IsNullOrEmpty(TargetName)) {
            Entity e = BObjects.Find(TargetName);
            if (e is not null) {
                target = e.transform;
                targetBody = e.GetComponent<Rigidbody>();
            }
        }
        if (target is null && BObjects.FindObjectOfType<VehicleController>(includeInactive: true) is { } vc) {
            target = vc.transform;
            targetBody = vc.Entity.GetComponent<Rigidbody>();
        }
        initialised = false; // snap to the new target on the next Tick instead of sweeping across the map
    }

    protected internal override void Tick(in float delta) {
        if (target is null) {
            ResolveTarget();
            if (target is null)
                return;
        }
        if (delta <= 0f)
            return;

        // Desired pose, derived from the target's CURRENT pose. Behind = the target's backward axis
        // (Transform.Forward is +Z in this engine), flattened to the ground so the camera doesn't dip
        // when the car pitches over a bump; height is added in world up.
        Vector3 targetPos = target.WorldPosition;
        Vector3 fwd = target.Forward;
        Vector3 flatBack = new Vector3(-fwd.X, 0f, -fwd.Z);
        flatBack = flatBack.LengthSquared() > 1e-6f ? flatBack.Normalized() : -Vector3.UnitZ;

        float pullback = 0f;
        if (targetBody is not null && SpeedPullback > 0f) {
            float speedFraction = MathHelper.Clamp(targetBody.Velocity.Length() / PullbackAtSpeed, 0f, 1f);
            pullback = SpeedPullback * speedFraction;
        }

        Vector3 desiredPos = targetPos + flatBack * (Distance + pullback) + Vector3.UnitY * Height;
        Vector3 desiredLook = targetPos + Vector3.UnitY * LookHeight;

        if (!initialised) {
            // First frame on a (new) target: snap, don't sweep.
            smoothedPosition = desiredPos;
            smoothedLookAt = desiredLook;
            initialised = true;
        } else {
            // Exponential damping: t = 1 - exp(-k*dt) is framerate-correct (same feel at any fps).
            float posT = 1f - MathF.Exp(-PositionSmooth * delta);
            float rotT = 1f - MathF.Exp(-RotationSmooth * delta);
            smoothedPosition = Vector3.Lerp(smoothedPosition, desiredPos, posT);
            smoothedLookAt = Vector3.Lerp(smoothedLookAt, desiredLook, rotT);
        }

        transform.WorldPosition = smoothedPosition;
        transform.WorldRotation = LookRotation(smoothedLookAt - smoothedPosition, Vector3.UnitY);
    }

    // A rotation whose +Z (Transform.Forward) points along `forward` and whose +Y leans toward `up`.
    // Built from an orthonormal basis directly — Matrix4.LookAt uses OpenGL's look-down-(-Z) view
    // convention and would invert the engine's +Z-forward, so it can't be used here.
    static Quaternion LookRotation(Vector3 forward, Vector3 up) {
        if (forward.LengthSquared() < 1e-12f)
            return Quaternion.Identity;

        forward = forward.Normalized();
        if (MathF.Abs(Vector3.Dot(forward, up)) > 0.999f)
            up = MathF.Abs(forward.Y) > 0.999f ? Vector3.UnitZ : Vector3.UnitY;

        Vector3 right = Vector3.Cross(up, forward).Normalized();
        Vector3 trueUp = Vector3.Cross(forward, right);

        // Row-vector (OpenTK) convention: the rotation mapping UnitX->right, UnitY->trueUp,
        // UnitZ->forward has those vectors as its ROWS.
        var m = new Matrix4(
            right.X, right.Y, right.Z, 0,
            trueUp.X, trueUp.Y, trueUp.Z, 0,
            forward.X, forward.Y, forward.Z, 0,
            0, 0, 0, 1);
        return Quaternion.Normalize(m.ExtractRotation());
    }
}
