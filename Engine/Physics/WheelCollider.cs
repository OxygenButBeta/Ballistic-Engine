
namespace BallisticEngine;

// A raycast (arcade) suspension wheel — the workhorse of the vehicle demo (P7). Each wheel casts a
// ray/sphere down from its mount, and when grounded applies, at the contact point on the chassis
// Rigidbody (via AddForceAtPosition, P2):
//   * a suspension SPRING + DAMPER force along the wheel's up axis (holds the body off the ground),
//   * a lateral GRIP force opposing sideways slip (so the car corners instead of skating),
//   * a longitudinal MOTOR/BRAKE force from the controller (drive and stop).
// It owns no body of its own; it reads the chassis Rigidbody from its parent (or this entity). This
// is the industry-standard approach (Unity's WheelCollider) — stable, fast, tunable.
[Component("Wheel Collider", "Physics")]
public class WheelCollider : Behaviour {
    [Header("Wheel")]
    [Tooltip("Wheel radius in metres — also the ray length below the suspension travel.")]
    [Range(0.05f, 2f)]
    public float Radius { get; set; } = 0.35f;

    [Header("Suspension")]
    [Tooltip("How far the suspension can extend below the mount, in metres.")]
    [Range(0.01f, 1f)]
    public float SuspensionTravel { get; set; } = 0.3f;

    [Tooltip("Spring stiffness holding the chassis up (N per metre of compression).")]
    [Range(0f, 100000f)]
    public float SuspensionStiffness { get; set; } = 30000f;

    [Tooltip("Suspension damping (N per m/s of compression speed) — kills bounce.")]
    [Range(0f, 10000f)]
    public float SuspensionDamping { get; set; } = 4000f;

    [Header("Grip")]
    [Tooltip("Sideways grip: how hard the tyre resists lateral slip. 0 = ice, 1 = glued.")]
    [Range(0f, 1f)]
    public float SidewaysGrip { get; set; } = 0.8f;

    [Tooltip("Rolling resistance applied to forward velocity when coasting (no throttle/brake).")]
    [Range(0f, 1f)]
    public float RollingResistance { get; set; } = 0.05f;

    // ---- Runtime readouts (set each FixedTick; useful for wheel-mesh animation / VFX) -----
    [NotSerialized] public bool IsGrounded { get; private set; }
    [NotSerialized] public float Compression { get; private set; } // 0 = extended, 1 = bottomed out
    [NotSerialized] public Vector3 ContactPoint { get; private set; }

    Rigidbody chassis;

    // Drive (+forward) / brake torque set by the VehicleController each step; consumed in FixedTick.
    internal float MotorForce;
    internal float BrakeForce;
    internal float SteerAngle; // radians, applied to this wheel's forward/right basis
    // How many wheels share the chassis load — set by the VehicleController; defaults to 4 (a car).
    internal int SharedWheelCount = 4;

    protected internal override void OnAttach() {
        // The chassis body is on this entity or an ancestor (wheels are usually child entities).
        chassis = GetComponent<Rigidbody>() ?? entity.GetComponentInParent<Rigidbody>();
    }

    int WheelCount() => SharedWheelCount;

    protected internal override void FixedTick(in float dt) {
        if (!SceneManager.IsPlaying)
            return;
        if (chassis is null || chassis.InternalBody is null) {
            chassis ??= entity.GetComponentInParent<Rigidbody>();
            return;
        }

        Transform t = transform;
        Vector3 up = t.Up;
        Vector3 mount = t.WorldPosition;

        // Steering rotates the wheel's heading about the up axis.
        Quaternion steer = Quaternion.CreateFromAxisAngle(up, SteerAngle);
        Vector3 forward = Vector3.Transform(t.Forward, steer);
        Vector3 right = Vector3.Transform(t.Right, steer);

        // Cast a sphere straight down from the mount over the full travel + radius. The sphere has
        // thickness, so a thin ray can't slip past an edge (P2 sweep). The mount usually sits inside
        // the chassis collider, so the cast would hit the car's OWN body first — skip any hit on the
        // chassis Rigidbody and re-cast from just below it.
        float castLength = SuspensionTravel + Radius;
        IsGrounded = CastIgnoringChassis(mount, -up, castLength, out RaycastHit hit);

        if (!IsGrounded) {
            Compression = 0f;
            ContactPoint = mount - up * castLength;
            MotorForce = BrakeForce = 0f;
            return;
        }

        ContactPoint = hit.Point;
        // Compression: how far the suspension is pushed up from full extension.
        float distance = hit.Distance;
        float compressionMetres = MathHelper.Clamp(castLength - distance, 0f, SuspensionTravel);
        Compression = compressionMetres / SuspensionTravel;

        // Suspension velocity (compression speed) along the up axis, from the chassis velocity at the
        // contact point. body velocity at a point = linear + angular x r.
        Vector3 pointVelocity = VelocityAt(ContactPoint);
        float upSpeed = Vector3.Dot(pointVelocity, up);

        // Spring force up, damper opposes compression speed. Clamped to push (never pull the car down).
        float springForce = SuspensionStiffness * compressionMetres - SuspensionDamping * upSpeed;
        springForce = MathF.Max(0f, springForce);
        chassis.AddForceAtPosition(up * springForce, ContactPoint);

        // Lateral grip: oppose the sideways slip. A per-wheel share of the chassis weight makes the
        // grip critically damped without a stiff per-step velocity-cancel (which oscillated). The
        // force is clamped to the available friction (grip * supported load) so it can't launch the
        // car sideways — a simplified linear tyre, not Pacejka, but stable.
        float lateralSpeed = Vector3.Dot(pointVelocity, right);
        float loadPerWheel = chassis.Mass * 9.81f / MathF.Max(1, WheelCount());
        float maxGrip = SidewaysGrip * loadPerWheel;
        float gripForce = MathHelper.Clamp(-lateralSpeed * loadPerWheel, -maxGrip, maxGrip);
        chassis.AddForceAtPosition(right * gripForce, ContactPoint);

        // Longitudinal: drive/brake from the controller, plus rolling resistance when coasting.
        float forwardSpeed = Vector3.Dot(pointVelocity, forward);
        float longForce = MotorForce;
        if (BrakeForce > 0f)
            longForce -= MathF.Sign(forwardSpeed) * MathF.Min(BrakeForce, MathF.Abs(forwardSpeed) * loadPerWheel);
        else if (MathF.Abs(MotorForce) < 1e-3f)
            longForce -= forwardSpeed * RollingResistance * loadPerWheel;
        chassis.AddForceAtPosition(forward * longForce, ContactPoint);

        MotorForce = BrakeForce = 0f; // consumed
    }

    Vector3 VelocityAt(Vector3 worldPoint) {
        Vector3 r = worldPoint - chassis.transform.WorldPosition;
        return chassis.Velocity + Vector3.Cross(chassis.AngularVelocity, r);
    }

    // Sphere-cast down for the ground, skipping the chassis's own body (the mount sits inside it).
    // If the first hit is the chassis, restart the cast just past that hit so the wheel finds the
    // actual ground below, not the car it's bolted to.
    bool CastIgnoringChassis(Vector3 origin, Vector3 dir, float maxDistance, out RaycastHit hit) {
        Vector3 start = origin;
        float remaining = maxDistance;
        for (var attempt = 0; attempt < 3; attempt++) {
            if (!Physics.SphereCast(start, Radius * 0.5f, dir, out hit, remaining, Physics.DefaultRaycastLayers))
                return false;
            if (!ReferenceEquals(hit.Rigidbody, chassis)) {
                // Report the hit distance measured from the ORIGINAL origin so suspension maths line up.
                hit.Distance = Vector3.Dot(hit.Point - origin, dir);
                return true;
            }
            float step = hit.Distance + 0.01f;
            start += dir * step;
            remaining -= step;
            if (remaining <= 0f)
                break;
        }
        hit = default;
        return false;
    }
}
