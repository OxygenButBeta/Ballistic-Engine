
namespace BallisticEngine;

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
    public float LookHeight { get; set; } = 1.2f;

    [Header("Look-ahead")]
    [Tooltip("How far AHEAD of the car (along its heading) the camera aims, in metres — so you see where " +
             "you're going. 0 = look straight at the car.")]
    [Range(0f, 20f)]
    public float LookAhead { get; set; } = 6f;

    [Tooltip("Extra look-ahead toward the INSIDE of a turn (metres at full lock) — leads the camera into " +
             "corners so the apex is on-screen. 0 = no steer lead.")]
    [Range(0f, 12f)]
    public float SteerLookAhead { get; set; } = 4f;

    [Header("Smoothing")]
    [Tooltip("Position follow speed. Higher = snappier (sticks to the car); lower = floatier. Per second.")]
    [Range(1f, 30f)]
    public float PositionSmooth { get; set; } = 9f;

    [Tooltip("Look/heading follow speed. A touch snappier than position so the car stays centred.")]
    [Range(1f, 40f)]
    public float RotationSmooth { get; set; } = 13f;

    [Tooltip("How fast the camera SWINGS AROUND behind the car when it turns, per second. This is the " +
             "fix for 'the camera turns weirdly': the camera TRAILS the car's rotation instead of rigidly " +
             "staying behind it, so a sharp turn shows the car from the side for a moment, then the camera " +
             "eases in behind. Lower = lazier/more cinematic swing; higher = sticks behind more tightly.")]
    [Range(0.5f, 12f)]
    public float OrbitSmooth { get; set; } = 3.5f;

    [Header("Speed feel")]
    [Tooltip("Extra metres the camera pulls back at the target's top speed (sense of speed). 0 = off.")]
    [Range(0f, 15f)]
    public float SpeedPullback { get; set; } = 3f;

    [Tooltip("Extra metres of look-ahead at top speed (the aim leads further the faster you go). 0 = off.")]
    [Range(0f, 20f)]
    public float SpeedLookAhead { get; set; } = 5f;

    [Tooltip("Target speed (m/s) at which the full speed-feel effects are reached.")]
    [Range(1f, 120f)]
    public float ReferenceSpeed { get; set; } = 38f;

    Transform target;
    Rigidbody targetBody;
    VehicleController targetCar;
    Vector3 smoothedPosition;
    Vector3 smoothedLookAt;
    float followYaw;
    bool initialised;

    protected internal override void OnAttach() => ResolveTarget();

    void ResolveTarget() {
        target = null;
        targetBody = null;
        targetCar = null;
        if (!string.IsNullOrEmpty(TargetName)) {
            Entity e = BObjects.Find(TargetName);
            if (e is not null) {
                target = e.transform;
                targetBody = e.GetComponent<Rigidbody>();
                targetCar = e.GetComponent<VehicleController>();
            }
        }
        if (target is null && BObjects.FindObjectOfType<VehicleController>(includeInactive: true) is { } vc) {
            target = vc.transform;
            targetBody = vc.Entity.GetComponent<Rigidbody>();
            targetCar = vc;
        }
        initialised = false;
    }

    protected internal override void Tick(in float delta) {
        if (target is null) {
            ResolveTarget();
            if (target is null)
                return;
        }
        if (delta <= 0f)
            return;

        Vector3 targetPos = target.WorldPosition;
        Vector3 fwd = target.Forward;
        Vector3 flatFwd = new Vector3(fwd.X, 0f, fwd.Z);
        flatFwd = flatFwd.LengthSquared() > 1e-6f ? flatFwd.Normalized() : Vector3.UnitZ;
        float carYaw = MathF.Atan2(flatFwd.X, flatFwd.Z);

        if (!initialised) {
            followYaw = carYaw;
        } else {
            float t = 1f - MathF.Exp(-OrbitSmooth * delta);
            followYaw = Mathf.LerpAngle(followYaw * Mathf.Rad2Deg, carYaw * Mathf.Rad2Deg, t) * Mathf.Deg2Rad;
        }
        Vector3 camFwd = new Vector3(MathF.Sin(followYaw), 0f, MathF.Cos(followYaw));
        Vector3 flatBack = -camFwd;

        float speedFraction = 0f;
        if (targetBody is not null && ReferenceSpeed > 0f) {
            var v = targetBody.Velocity;
            float groundSpeed = MathF.Sqrt(v.X * v.X + v.Z * v.Z);
            speedFraction = MathHelper.Clamp(groundSpeed / ReferenceSpeed, 0f, 1f);
        }
        float pullback = SpeedPullback * speedFraction;
        float lookAhead = LookAhead + SpeedLookAhead * speedFraction;

        Vector3 steerLead = Vector3.Zero;
        if (targetCar is not null && SteerLookAhead > 0f && targetCar.MaxSteerAngle > 0f && speedFraction > 0.01f) {
            Vector3 right = target.Right;
            var flatRight = new Vector3(right.X, 0f, right.Z);
            if (flatRight.LengthSquared() > 1e-6f) {
                flatRight = flatRight.Normalized();
                float steerNorm = MathHelper.Clamp(targetCar.CurrentSteerNormalized, -1f, 1f);
                steerLead = flatRight * (steerNorm * SteerLookAhead * speedFraction);
            }
        }

        Vector3 aimFwd = camFwd;
        Vector3 desiredPos = targetPos + flatBack * (Distance + pullback) + Vector3.UnitY * Height;
        Vector3 desiredLook = targetPos + aimFwd * lookAhead + steerLead + Vector3.UnitY * LookHeight;

        if (!initialised) {
            smoothedPosition = desiredPos;
            smoothedLookAt = desiredLook;
            initialised = true;
        } else {
            float posT = 1f - MathF.Exp(-PositionSmooth * delta);
            float rotT = 1f - MathF.Exp(-RotationSmooth * delta);
            smoothedPosition = Vector3.Lerp(smoothedPosition, desiredPos, posT);
            smoothedLookAt = Vector3.Lerp(smoothedLookAt, desiredLook, rotT);
        }

        transform.WorldPosition = smoothedPosition;
        transform.WorldRotation = LookRotation(smoothedLookAt - smoothedPosition, Vector3.UnitY);
    }

    static Quaternion LookRotation(Vector3 forward, Vector3 up) {
        if (forward.LengthSquared() < 1e-12f)
            return Quaternion.Identity;

        forward = forward.Normalized();
        if (MathF.Abs(Vector3.Dot(forward, up)) > 0.999f)
            up = MathF.Abs(forward.Y) > 0.999f ? Vector3.UnitZ : Vector3.UnitY;

        Vector3 right = Vector3.Cross(up, forward).Normalized();
        Vector3 trueUp = Vector3.Cross(forward, right);

        var m = new Matrix4(
            right.X, right.Y, right.Z, 0,
            trueUp.X, trueUp.Y, trueUp.Z, 0,
            forward.X, forward.Y, forward.Z, 0,
            0, 0, 0, 1);
        return Quaternion.Normalize(m.ExtractRotation());
    }
}
