
namespace BallisticEngine;

[Component("Wheel Collider", "Physics")]
public class WheelCollider : Behaviour {
    [Header("Wheel")]
    [Tooltip("Wheel radius in metres — also the ray length below the suspension travel.")]
    [Range(0.05f, 2f)]
    public float Radius { get; set; } = 0.35f;

    [Header("Suspension")]
    [Tooltip("How far the suspension can extend below the mount, in metres.")]
    [Range(0.01f, 1f)]
    public float SuspensionTravel { get; set; } = 0.45f;

    [Tooltip("Spring stiffness holding the chassis up (N per metre of compression). Higher = stiffer ride; " +
             "lower = more visible squat/dive/roll (the GTA-sport default has a little body movement). " +
             "Ride height is decoupled from this (the spring is preloaded to the car's weight).")]
    [Range(0f, 200000f)]
    public float SuspensionStiffness { get; set; } = 30000f;

    [Tooltip("Suspension damping (N per m/s of compression speed) — kills bounce. Higher = more planted/" +
             "less wallowy.")]
    [Range(0f, 20000f)]
    public float SuspensionDamping { get; set; } = 4000f;

    [Tooltip("Where the spring rests when the car sits still, as a fraction of travel (0 = fully extended, " +
             "0.5 = mid-travel). Resting at mid-travel leaves room to both squat and droop, so the " +
             "suspension visibly works in both directions over bumps.")]
    [Range(0.1f, 0.9f)]
    public float SuspensionRestFraction { get; set; } = 0.5f;

    [Tooltip("Extra distance below full droop that the wheel probes for ground, in metres. The margin " +
             "catches a rising hill a frame early so the wheel doesn't punch through the slope at speed. " +
             "Raise it if wheels still clip into steep ground when driving fast; 0 = exact (can tunnel).")]
    [Range(0f, 2f)]
    public float GroundProbeMargin { get; set; } = 0.5f;

    [NotSerialized] public bool IsGrounded { get; private set; }
    [NotSerialized] public float Compression { get; private set; }
    [NotSerialized] public Vector3 ContactPoint { get; private set; }
    [NotSerialized] public Vector3 ContactNormal { get; private set; }
    [NotSerialized] public float ForwardSpeed { get; private set; }

    [NotSerialized] public float SuspensionDrop { get; private set; }

    Rigidbody chassis;

    internal float SteerAngle;

    internal int SharedWheelCount = 4;

    protected internal override void OnAttach() {
        chassis = GetComponent<Rigidbody>() ?? entity.GetComponentInParent<Rigidbody>();
    }

    int WheelCount() => Math.Max(1, SharedWheelCount);

    Transform wheelMesh;
    bool wheelMeshResolved;
    float rollAngle;

    protected internal override void Tick(in float delta) {
        if (!SceneManager.IsPlaying || delta <= 0f)
            return;
        if (!wheelMeshResolved) {
            foreach (Entity child in entity.DirectChildren()) {
                if (child.GetComponent<StaticMeshRenderer>() is not null) {
                    wheelMesh = child.transform;
                    break;
                }
            }
            wheelMeshResolved = true;
        }
        if (wheelMesh is null)
            return;

        Transform t = transform;
        Vector3 up = t.Up;

        Vector3 worldTarget;
        if (IsGrounded) {
            worldTarget = ContactPoint + up * Radius;
            float maxRise = Radius * 0.5f;
            float aboveMount = Vector3.Dot(worldTarget - t.WorldPosition, up);
            if (aboveMount > maxRise)
                worldTarget = t.WorldPosition + up * maxRise;
        } else {
            worldTarget = t.WorldPosition - up * SuspensionTravel;
        }
        wheelMesh.WorldPosition = worldTarget;

        rollAngle += ForwardSpeed / MathF.Max(0.05f, Radius) * delta;
        rollAngle -= MathF.Tau * MathF.Round(rollAngle / MathF.Tau);

        Quaternion baseRot = t.WorldRotation;
        Quaternion steer = Quaternion.CreateFromAxisAngle(Vector3.UnitY, SteerAngle);
        Quaternion roll = Quaternion.CreateFromAxisAngle(Vector3.UnitX, rollAngle);
        wheelMesh.WorldRotation = baseRot * steer * roll;
    }

    protected internal override void FixedTick(in float dt) {
        if (!SceneManager.IsPlaying)
            return;
        if (chassis is null || chassis.InternalBody is null) {
            chassis ??= entity.GetComponentInParent<Rigidbody>();
            return;
        }
        if (dt <= 0f)
            return;

        Transform t = transform;
        Vector3 up = t.Up;
        Vector3 mount = t.WorldPosition;

        Quaternion steerRot = Quaternion.CreateFromAxisAngle(up, SteerAngle);
        Vector3 forward = Vector3.Transform(t.Forward, steerRot);

        float restLength = SuspensionTravel + Radius;
        float castLength = restLength + GroundProbeMargin;
        bool hitGround = CastIgnoringChassis(mount, -up, castLength, out RaycastHit hit);

        ForwardSpeed = Vector3.Dot(chassis.Velocity, forward);

        float groundDistance = hitGround ? hit.Distance : float.MaxValue;
        IsGrounded = hitGround && groundDistance <= restLength + 0.02f;

        if (!IsGrounded) {
            Compression = 0f;
            SuspensionDrop = SuspensionTravel;
            ContactPoint = mount - up * restLength;
            ContactNormal = up;
            return;
        }

        ContactPoint = hit.Point;
        ContactNormal = hit.Normal;
        SuspensionDrop = MathHelper.Clamp(groundDistance - Radius, 0f, SuspensionTravel);
        float compressionMetres = SuspensionTravel - SuspensionDrop;
        Compression = compressionMetres / SuspensionTravel;

        Vector3 pointVelocity = VelocityAt(ContactPoint);
        float upSpeed = Vector3.Dot(pointVelocity, up);

        float restMetres = SuspensionTravel * SuspensionRestFraction;
        float staticLoad = chassis.Mass * Physics.Gravity.Length() / WheelCount();
        float springForce = staticLoad
                          + SuspensionStiffness * (compressionMetres - restMetres)
                          - SuspensionDamping * upSpeed;
        springForce = MathF.Max(0f, springForce);
        chassis.AddForceAtPosition(up * springForce, ContactPoint);

        float overCompression = MathF.Max(0f, Radius - groundDistance);
        if (overCompression > 0f) {
            const float bumpStopSpeed = 4f;
            float targetUp = MathF.Min(overCompression / dt, bumpStopSpeed);
            if (upSpeed < targetUp) {
                float bumpForce = (targetUp - upSpeed) * (chassis.Mass / WheelCount()) / dt;
                chassis.AddForceAtPosition(up * bumpForce, ContactPoint);
            }
        }
    }

    Vector3 VelocityAt(Vector3 worldPoint) {
        Vector3 r = worldPoint - chassis.transform.WorldPosition;
        return chassis.Velocity + Vector3.Cross(chassis.AngularVelocity, r);
    }

    bool CastIgnoringChassis(Vector3 origin, Vector3 dir, float maxDistance, out RaycastHit hit) {
        Vector3 start = origin;
        float remaining = maxDistance;
        for (var attempt = 0; attempt < 3; attempt++) {
            if (!Physics.SphereCast(start, Radius * 0.5f, dir, out hit, remaining, Physics.DefaultRaycastLayers))
                return false;
            if (!ReferenceEquals(hit.Rigidbody, chassis)) {
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

    public override void OnDrawGizmosSelected(IGizmos gizmos) {
        Transform t = transform;
        Vector3 up = t.Up;
        Vector3 mount = t.WorldPosition;
        Vector3 forward = t.Forward;
        Vector3 right = t.Right;

        Quaternion steer = Quaternion.CreateFromAxisAngle(up, SteerAngle);
        Vector3 wheelFwd = Vector3.Transform(forward, steer);

        float drop = SceneManager.IsPlaying ? SuspensionDrop : SuspensionTravel * SuspensionRestFraction;
        Vector3 centre = mount - up * drop;

        gizmos.Color = IsGrounded || !SceneManager.IsPlaying ? new Vector3(0.4f, 0.9f, 1f) : new Vector3(1f, 0.6f, 0.2f);
        DrawCircle(gizmos, centre, wheelFwd, up, Radius);

        Vector3 droopEnd = mount - up * SuspensionTravel;
        gizmos.Color = new Vector3(0.6f, 0.6f, 0.65f);
        gizmos.DrawLine(mount, droopEnd);
        Vector3 restPos = mount - up * (SuspensionTravel * SuspensionRestFraction);
        DrawTick(gizmos, restPos, right, 0.12f);
        DrawTick(gizmos, mount, right, 0.08f);
        DrawTick(gizmos, droopEnd, right, 0.08f);

        if (SceneManager.IsPlaying && IsGrounded) {
            gizmos.Color = new Vector3(0.3f, 1f, 0.4f);
            DrawTick(gizmos, ContactPoint, right, 0.15f);
            DrawTick(gizmos, ContactPoint, wheelFwd, 0.15f);
        }
    }

    static void DrawCircle(IGizmos gizmos, Vector3 centre, Vector3 axisA, Vector3 axisB, float radius) {
        const int seg = 24;
        Vector3 prev = centre + axisA * radius;
        for (var i = 1; i <= seg; i++) {
            float a = i / (float)seg * MathF.Tau;
            Vector3 p = centre + (axisA * MathF.Cos(a) + axisB * MathF.Sin(a)) * radius;
            gizmos.DrawLine(prev, p);
            prev = p;
        }
    }

    static void DrawTick(IGizmos gizmos, Vector3 at, Vector3 dir, float half) =>
        gizmos.DrawLine(at - dir * half, at + dir * half);
}
