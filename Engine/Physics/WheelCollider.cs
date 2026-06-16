
namespace BallisticEngine;

// A raycast (arcade) suspension wheel — the workhorse of the vehicle demo (P7). Each wheel casts a
// ray/sphere down from its mount, and when grounded applies, at the contact point on the chassis
// Rigidbody (via AddForceAtPosition, P2):
//   * a suspension SPRING + DAMPER force along the wheel's up axis (holds the body off the ground),
//   * a lateral GRIP force that CANCELS sideways slip (so the car corners on rails, not skates),
//   * a longitudinal MOTOR/BRAKE force from the controller (drive and stop).
// It owns no body of its own; it reads the chassis Rigidbody from its parent (or this entity). This
// is the industry-standard approach (Unity's WheelCollider) — stable, fast, tunable.
//
// ARCADE rewrite (GTA / Need-for-Speed feel): the lateral model is no longer a soft force
// proportional to slip — it computes the IMPULSE that would zero the sideways velocity at the
// contact this step and applies a (grip-scaled) fraction of it, clamped to a generous friction
// budget. At Grip 1.0 the tyre is glued: the car goes exactly where it's pointed, no ice-skating.
// Longitudinal traction is handled the same way so power lands instead of spinning out.
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
    [Range(0f, 200000f)]
    public float SuspensionStiffness { get; set; } = 45000f;

    [Tooltip("Suspension damping (N per m/s of compression speed) — kills bounce. Stiff = planted.")]
    [Range(0f, 20000f)]
    public float SuspensionDamping { get; set; } = 6000f;

    [Header("Grip")]
    [Tooltip("Sideways grip: how much of the per-step lateral slip is cancelled. " +
             "0 = ice (drifts forever), 1 = glued (no slide at all). Arcade default is sticky.")]
    [Range(0f, 1f)]
    public float SidewaysGrip { get; set; } = 1f;

    [Tooltip("Friction budget as a multiple of the wheel's vertical load. Higher = the tyre can " +
             "hold harder cornering before it ever lets go. Arcade cars run high.")]
    [Range(0.5f, 6f)]
    public float GripBudget { get; set; } = 3f;

    [Tooltip("How fast lateral grip builds: fraction of sideways slip cancelled per step (0..1). " +
             "1 = instant (stiff, but a steered wheel can whip the car into a spin); ~0.3 = the tyre " +
             "relaxes over a few steps — planted and stable. Lower if the car feels twitchy/spinny.")]
    [Range(0.05f, 1f)]
    public float GripRelax { get; set; } = 0.3f;

    [Tooltip("Forward traction: how much of the drive/brake force the tyre can put down (1 = full " +
             "grip, lower = wheelspin/longer stops). The friction circle still caps the total.")]
    [Range(0.1f, 1f)]
    public float ForwardGrip { get; set; } = 1f;

    [Tooltip("Rolling resistance applied to forward velocity when coasting (no throttle/brake).")]
    [Range(0f, 1f)]
    public float RollingResistance { get; set; } = 0.04f;

    // ---- Runtime readouts (set each FixedTick; useful for wheel-mesh animation / VFX) -----
    [NotSerialized] public bool IsGrounded { get; private set; }
    [NotSerialized] public float Compression { get; private set; } // 0 = extended, 1 = bottomed out
    [NotSerialized] public Vector3 ContactPoint { get; private set; }
    [NotSerialized] public float LateralSlip { get; private set; } // |sideways m/s| at the contact

    Rigidbody chassis;

    // Drive (+forward) / brake torque set by the VehicleController each step; consumed in FixedTick.
    internal float MotorForce;
    internal float BrakeForce;
    internal float SteerAngle; // radians, applied to this wheel's forward/right basis
    internal bool Handbrake;   // this wheel is hand-braked (locks longitudinal, lets the rear step out)
    // How many wheels share the chassis load — set by the VehicleController; defaults to 4 (a car).
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

    // Render-frame visual: roll the wheel mesh by ground speed and turn it by the steer angle, so the
    // wheels visibly spin and the front wheels point where you steer. Pure cosmetic (no physics).
    protected internal override void Tick(in float delta) {
        if (!SceneManager.IsPlaying || delta <= 0f)
            return;
        if (!wheelMeshResolved) {
            // First child with a StaticMeshRenderer is the visual wheel.
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

        chassis ??= entity.GetComponentInParent<Rigidbody>();
        // Rolling: angular speed = forward ground speed / radius. Sign from the wheel's forward axis.
        float forwardSpeed = chassis is not null
            ? Vector3.Dot(chassis.Velocity, transform.Forward)
            : 0f;
        rollAngle += forwardSpeed / MathF.Max(0.05f, Radius) * delta;
        // Keep the accumulated angle bounded to [-π, π] so the float never drifts large.
        rollAngle -= MathF.Tau * MathF.Round(rollAngle / MathF.Tau);

        // Compose: steer about local up (Y), then roll about local right (X). The mesh's local rotation
        // is fully driven here, so it reads the live steer + roll without touching the collider transform.
        Quaternion steer = Quaternion.CreateFromAxisAngle(Vector3.UnitY, SteerAngle);
        Quaternion roll = Quaternion.CreateFromAxisAngle(Vector3.UnitX, rollAngle);
        wheelMesh.Rotation = steer * roll;
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
            LateralSlip = 0f;
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

        // The friction budget is based on the wheel's STABLE STATIC LOAD (its share of the car's
        // weight), NOT the instantaneous springForce. springForce includes the damper term
        // (-SuspensionDamping*upSpeed): on tilting terrain the suspension bobs, upSpeed spikes, and the
        // damper drove springForce — and thus the budget — to ~0, clamping drive to a crawl that never
        // recovered (the post-turn "stall"). Using the static load keeps a grounded wheel's grip/drive
        // budget steady; springForce only adds a little EXTRA budget for genuine weight transfer (a
        // squatting wheel grips a bit more), never subtracts below the stable floor.
        float loadPerWheel = chassis.Mass * Physics.Gravity.Length() / WheelCount();
        float normalLoad = loadPerWheel + MathF.Max(0f, springForce - loadPerWheel) * 0.5f;
        float frictionBudget = GripBudget * normalLoad;

        // --- Lateral grip: cancel the sideways slip toward zero, RELAXED over a few steps. ---------
        // Working in impulse space (force * dt) lets us aim at "kill the slip" then clamp to the
        // friction circle. But cancelling the WHOLE slip every step is an infinitely-stiff tyre: on a
        // STEERED wheel that makes a violent yaw moment and the car's nose out-rotates its velocity (a
        // spin). So we cancel only a FRACTION per step (SidewaysGrip × GripRelax) — the tyre builds its
        // force over ~3-4 steps, which is both more realistic and stable. GripRelax keeps the front
        // wheels from whipping the body around while the grip is still very sticky (slip decays fast).
        float lateralSpeed = Vector3.Dot(pointVelocity, right);
        LateralSlip = MathF.Abs(lateralSpeed);
        float effMass = chassis.Mass / WheelCount();
        float desiredLatImpulse = -lateralSpeed * effMass * SidewaysGrip * GripRelax;

        // --- Longitudinal traction: drive/brake plus a slip-cancel so power tracks straight. ------
        float forwardSpeed = Vector3.Dot(pointVelocity, forward);
        // Drive/brake/handbrake produce a target longitudinal impulse. ForwardGrip scales how much of
        // the drive force the tyre puts down (lower = wheelspin); the friction circle still caps it.
        float driveImpulse = MotorForce * dt * ForwardGrip;
        float longCancel = 0f;
        if (Handbrake) {
            // Lock the wheel: cancel forward roll entirely (rear handbrake → the tail can rotate out
            // while the lateral grip there is also gone-ish; here we keep lateral so it's controllable).
            longCancel = -forwardSpeed * effMass;
            driveImpulse = 0f;
        } else if (BrakeForce > 0f) {
            // Brake toward a stop, never past it (no reverse-launch from over-braking).
            float maxStop = -forwardSpeed * effMass;
            float brakeImp = -MathF.Sign(forwardSpeed) * BrakeForce * dt;
            longCancel = MathF.Abs(brakeImp) > MathF.Abs(maxStop) ? maxStop : brakeImp;
            driveImpulse = 0f;
        } else if (MathF.Abs(MotorForce) < 1e-3f) {
            // Coasting: gentle rolling resistance only. RollingResistance is the fraction of forward
            // momentum shed PER SECOND (so it's framerate-independent and small) — a rolling wheel
            // must NOT cancel forward speed per-step or the car crawls. Forward traction is about not
            // SLIDING (lateral), so it deliberately doesn't drag a freely-rolling wheel here.
            longCancel = -forwardSpeed * effMass * RollingResistance * dt;
        }
        // Under power: the drive impulse lands directly; traction is limited by the friction circle below.
        float desiredLongImpulse = driveImpulse + longCancel;

        // --- Friction circle: the combined tyre impulse can't exceed the budget this step. --------
        // DRIVE traction is reserved FIRST so the car can always power through a corner (arcade feel —
        // a strict lateral-first priority starved the drive in hard turns and the car bogged to a
        // crawl). Longitudinal takes its share, lateral gets the rest of the circle. The budget is
        // generous (GripBudget × load), so lateral grip is still very sticky in normal cornering.
        float maxImpulse = frictionBudget * dt;
        float longImpulse = MathHelper.Clamp(desiredLongImpulse, -maxImpulse, maxImpulse);
        float remaining = MathF.Sqrt(MathF.Max(0f, maxImpulse * maxImpulse - longImpulse * longImpulse));
        float latImpulse = MathHelper.Clamp(desiredLatImpulse, -remaining, remaining);

        // Apply lateral + longitudinal grip at a RAISED point — the contact lifted to the chassis centre
        // of mass (the "roll centre"). A grip force at the ground contact (0.5 m below the COM) makes a
        // big moment: lateral force barrel-rolls the car, and a hard BRAKE force pitches/yaws it so the
        // car lurches sideways when you slam the brakes. Applying both at COM height removes those
        // moments while keeping the exact same linear accel/brake/grip — the planted arcade feel. (We
        // give up squat/dive pitch realism, which an arcade car doesn't need and which caused the lurch.)
        Vector3 com = chassis.transform.WorldPosition;
        var gripPoint = new Vector3(ContactPoint.X, com.Y, ContactPoint.Z);
        chassis.AddForceAtPosition(right * (latImpulse / dt), gripPoint);
        chassis.AddForceAtPosition(forward * (longImpulse / dt), gripPoint);

        MotorForce = BrakeForce = 0f; // consumed
        Handbrake = false;
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
