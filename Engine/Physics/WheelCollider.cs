
namespace BallisticEngine;

// A raycast (arcade) suspension wheel for the vehicle controller. Each wheel casts a sphere down from
// its mount and, when grounded, applies a suspension SPRING + DAMPER force along its up axis (holds the
// chassis off the ground and gives visible squat/dive/roll), reports its ground contact, and animates
// the visual wheel mesh (sit on the ground, roll by speed, steer). It owns no body of its own; it reads
// the chassis Rigidbody from its parent (or this entity).
//
// HORIZONTAL dynamics (drive, brake, grip, steering) are owned by the VehicleController, which uses a
// velocity-steering ARCADE model: the heading turns at a controlled rate and the car's velocity is
// rotated to follow the nose while its SPEED is preserved. That is what keeps the car on rails AND
// keeps its speed through a corner — the structural cure for the scrub-to-stall and snap-spin failure
// modes that a per-wheel tyre-force model is prone to. So the wheel deliberately does NOT apply drive
// or lateral forces; it does suspension + grounding only. The controller reads IsGrounded/ContactPoint.
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

    // ---- Runtime readouts (set each FixedTick; read by the controller + used for wheel-mesh / VFX) ----
    [NotSerialized] public bool IsGrounded { get; private set; }
    [NotSerialized] public float Compression { get; private set; } // 0 = extended, 1 = bottomed out
    [NotSerialized] public Vector3 ContactPoint { get; private set; }
    [NotSerialized] public Vector3 ContactNormal { get; private set; }
    [NotSerialized] public float ForwardSpeed { get; private set; } // signed roll speed (for visual roll)
    // How far the wheel CENTRE is dropped below the mount along the suspension axis, in metres, clamped
    // to [0, SuspensionTravel]. The single source of truth for the visual wheel position so it can never
    // clip up into the body or sink below full droop. 0 = bottomed out (wheel at the mount), travel = droop.
    [NotSerialized] public float SuspensionDrop { get; private set; }

    Rigidbody chassis;

    // Set by the VehicleController each step (consumed by the visual): the steer angle of this wheel.
    internal float SteerAngle; // radians
    // How many wheels share the chassis load — set by the controller; defaults to 4 (a car).
    internal int SharedWheelCount = 4;

    protected internal override void OnAttach() {
        // The chassis body is on this entity or an ancestor (wheels are usually child entities).
        chassis = GetComponent<Rigidbody>() ?? entity.GetComponentInParent<Rigidbody>();
    }

    int WheelCount() => Math.Max(1, SharedWheelCount);

    // Visual spin/steer of the wheel MESH (a child entity, e.g. "WheelFLMesh"). Cached on first Tick.
    Transform wheelMesh;
    bool wheelMeshResolved;
    float rollAngle; // accumulated rolling angle (rad) about the wheel's spin axis

    // Render-frame visual: place the wheel mesh ON the ground (it follows the suspension travel instead
    // of being stuck at the fixed mount, so wheels don't float or sink as the suspension moves), roll it
    // by ground speed, and steer the steered wheels. Pure cosmetic (no physics).
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

        // POSITION: when grounded, sit the wheel centre exactly ONE RADIUS above the ground contact so
        // the tyre rests ON the surface — never sinking into it. (The previous "mount − drop" form let the
        // wheel bottom punch below the ground whenever the suspension bottomed out, because the drop was
        // clamped at the mount while the ground was closer than the radius — that was the visible
        // penetration.) The centre is allowed to rise a little ABOVE the mount when the suspension is
        // fully compressed (the tyre tucking up into the wheel well), but not unboundedly. When airborne
        // the wheel hangs at full droop from the mount.
        Vector3 worldTarget;
        if (IsGrounded) {
            worldTarget = ContactPoint + up * Radius;
            // Cap the upward travel so a momentary deep contact can't shoot the wheel through the roof:
            // it may rise up to (full compression) above the mount's rest, no further.
            float maxRise = Radius * 0.5f;
            float aboveMount = Vector3.Dot(worldTarget - t.WorldPosition, up);
            if (aboveMount > maxRise)
                worldTarget = t.WorldPosition + up * maxRise;
        } else {
            worldTarget = t.WorldPosition - up * SuspensionTravel; // full droop
        }
        wheelMesh.WorldPosition = worldTarget;

        // ROLL about the spin AXLE (the wheel's right axis → local X): angular speed = ground speed / r.
        rollAngle += ForwardSpeed / MathF.Max(0.05f, Radius) * delta;
        rollAngle -= MathF.Tau * MathF.Round(rollAngle / MathF.Tau); // keep in [-π, π]

        // Steer about the wheel's up (Y), THEN roll about its right (X), off the wheel entity's world
        // rotation so it matches the car's orientation on slopes.
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

        // The visual roll uses the chassis forward speed (steering only turns the mesh, not the roll axis).
        Quaternion steerRot = Quaternion.CreateFromAxisAngle(up, SteerAngle);
        Vector3 forward = Vector3.Transform(t.Forward, steerRot);

        // Cast a sphere straight down from the mount looking for the ground. The sphere has thickness so
        // a thin ray can't slip past an edge (P2 sweep). The cast probes a generous margin BELOW full
        // droop (travel + radius + a margin): the extra reach is what catches the ground a frame early
        // when climbing a hill at speed, instead of the wheel punching through before the next step.
        // The mount usually sits inside the chassis collider, so skip any hit on the car's OWN body.
        float restLength = SuspensionTravel + Radius;            // mount→ground distance at full droop
        float castLength = restLength + GroundProbeMargin;       // probe a bit further to avoid tunneling
        bool hitGround = CastIgnoringChassis(mount, -up, castLength, out RaycastHit hit);

        ForwardSpeed = Vector3.Dot(chassis.Velocity, forward);

        // GROUNDED means the wheel is actually within suspension range of the ground (groundDistance ≤
        // full droop) — NOT merely that the probe (which reaches a margin further to avoid tunneling)
        // found something. This is the difference between "driving on the ground" and "the ground is
        // still 0.4 m below me after a jump". Conflating them pinned the car to the ground off a ramp
        // (the controller kept applying its on-rails grip while the car should have been flying).
        float groundDistance = hitGround ? hit.Distance : float.MaxValue;
        IsGrounded = hitGround && groundDistance <= restLength + 0.02f;

        if (!IsGrounded) {
            Compression = 0f;
            SuspensionDrop = SuspensionTravel;                   // hang at full droop
            ContactPoint = mount - up * restLength;
            ContactNormal = up;
            return;
        }

        // groundDistance = mount→surface along the cast. The wheel CENTRE sits one radius above the
        // surface, so its drop below the mount is (groundDistance - radius), clamped to [0, travel].
        // Clamping is what makes penetration impossible: a ground higher than full compression pins the
        // drop at 0 (wheel at the mount) and the spring saturates to push the body up, rather than the
        // wheel mesh sliding up into the body.
        ContactPoint = hit.Point;
        ContactNormal = hit.Normal;
        SuspensionDrop = MathHelper.Clamp(groundDistance - Radius, 0f, SuspensionTravel);
        float compressionMetres = SuspensionTravel - SuspensionDrop; // 0 = extended, travel = bottomed
        Compression = compressionMetres / SuspensionTravel;

        // Body velocity at the contact = linear + angular × r; its up-component is the compression speed.
        Vector3 pointVelocity = VelocityAt(ContactPoint);
        float upSpeed = Vector3.Dot(pointVelocity, up);

        // Suspension force = a PRELOAD that exactly balances this wheel's static weight share at the rest
        // length, plus the spring's deviation from rest, minus the damper. Preloading to the static load
        // means the car settles at SuspensionRestFraction REGARDLESS of stiffness — so ride height is
        // decoupled from firmness, and the spring always has headroom both ways (it isn't sitting near
        // bottomed-out just because the stiffness is low, which is what made the wheels bottom on bumps).
        // Stiffness now only controls how FIRM the ride is; clamped to push only (never pull the car down).
        float restMetres = SuspensionTravel * SuspensionRestFraction;
        float staticLoad = chassis.Mass * Physics.Gravity.Length() / WheelCount();
        float springForce = staticLoad
                          + SuspensionStiffness * (compressionMetres - restMetres)
                          - SuspensionDamping * upSpeed;
        springForce = MathF.Max(0f, springForce);
        chassis.AddForceAtPosition(up * springForce, ContactPoint);
    }

    Vector3 VelocityAt(Vector3 worldPoint) {
        Vector3 r = worldPoint - chassis.transform.WorldPosition;
        return chassis.Velocity + Vector3.Cross(chassis.AngularVelocity, r);
    }

    // Sphere-cast down for the ground, skipping the chassis's own body (the mount sits inside it). If the
    // first hit is the chassis, restart just past it so the wheel finds the actual ground below.
    bool CastIgnoringChassis(Vector3 origin, Vector3 dir, float maxDistance, out RaycastHit hit) {
        Vector3 start = origin;
        float remaining = maxDistance;
        for (var attempt = 0; attempt < 3; attempt++) {
            if (!Physics.SphereCast(start, Radius * 0.5f, dir, out hit, remaining, Physics.DefaultRaycastLayers))
                return false;
            if (!ReferenceEquals(hit.Rigidbody, chassis)) {
                hit.Distance = Vector3.Dot(hit.Point - origin, dir); // measure from the ORIGINAL origin
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

    // ---- Editor gizmos ------------------------------------------------------------------------------
    // Drawn for the selected entity (Unity's OnDrawGizmosSelected). Shows the wheel radius circle, the
    // suspension travel line (mount → full droop, with the rest mark), and — in play mode — the live
    // ground contact + where the wheel currently sits. Lets you see the suspension geometry at a glance
    // (and is what the inspector drag-handles edit). Pure IGizmos lines, no editor dependency.
    public override void OnDrawGizmosSelected(IGizmos gizmos) {
        Transform t = transform;
        Vector3 up = t.Up;
        Vector3 mount = t.WorldPosition;
        Vector3 forward = t.Forward;
        Vector3 right = t.Right;

        // Steer the drawn circle so it visually matches the wheel heading.
        Quaternion steer = Quaternion.CreateFromAxisAngle(up, SteerAngle);
        Vector3 wheelFwd = Vector3.Transform(forward, steer);

        // Wheel centre: where the mesh sits (mount dropped by the suspension; rest position in edit mode).
        float drop = SceneManager.IsPlaying ? SuspensionDrop : SuspensionTravel * SuspensionRestFraction;
        Vector3 centre = mount - up * drop;

        // The wheel radius circle, in the wheel's plane (spanned by forward and up), drawn as segments.
        gizmos.Color = IsGrounded || !SceneManager.IsPlaying ? new Vector3(0.4f, 0.9f, 1f) : new Vector3(1f, 0.6f, 0.2f);
        DrawCircle(gizmos, centre, wheelFwd, up, Radius);

        // Suspension travel line: from the mount (full compression) down to full droop, with a tick at
        // the rest length so you can see how much squat/droom room there is.
        Vector3 droopEnd = mount - up * SuspensionTravel;
        gizmos.Color = new Vector3(0.6f, 0.6f, 0.65f);
        gizmos.DrawLine(mount, droopEnd);
        Vector3 restPos = mount - up * (SuspensionTravel * SuspensionRestFraction);
        DrawTick(gizmos, restPos, right, 0.12f);
        DrawTick(gizmos, mount, right, 0.08f);
        DrawTick(gizmos, droopEnd, right, 0.08f);

        // Live ground contact (play mode): a small marker at the contact point.
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
