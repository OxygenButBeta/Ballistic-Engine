using OpenTK.Windowing.GraphicsLibraryFramework; // Keys enum, same source the Input facade uses

namespace BallisticEngine;

// Arcade vehicle controller (Unreal-starter-content quality). Drives a chassis Rigidbody whose wheels
// are WheelCollider components on child entities. The wheels do SUSPENSION + grounding; this controller
// owns the HORIZONTAL dynamics with a velocity-steering ARCADE model that is on-rails by default and
// keeps its speed through corners — no ice-skating, no spin-out, no scrub-to-stall.
//
// THE MODEL (why it is stable where a per-wheel tyre-force model spins out):
//   * HEADING turns at a controlled yaw rate the steering commands (bicycle model, clamped to MaxTurnRate),
//     so the car can never rotate faster than the cap — spin-out is structurally impossible.
//   * SPEED eases toward a throttle target (full throttle → MaxSpeed; lift → engine-brake to a coast;
//     brake/handbrake → toward a stop). The ease uses the ACTUAL horizontal speed, so a turn never
//     bleeds it (the old bug projected speed onto the heading and lost it in corners → scrub-to-stall).
//   * The VELOCITY DIRECTION is rotated toward the heading by GRIP each step, preserving the eased speed.
//     Grip 1 = glued (the car goes exactly where it points); lower grip / handbrake lets the tail step
//     out for a controllable, clean-recovering drift.
//   * Vertical velocity (gravity + suspension) is left untouched, so the car falls, follows terrain, and
//     the suspension works. STABILITY (anti-roll force pair + downforce) plants it without self-steering.
//
// Input: WASD / arrows + gamepad (left stick steer + throttle, RT/LT triggers throttle/brake, A = handbrake,
// Y or R = flip-recovery reset). All tunable below; the defaults are chosen to feel great out of the box.
[Component("Vehicle Controller", "Physics")]
public class VehicleController : Behaviour {
    [Header("Drive")]
    [Tooltip("How hard the car accelerates toward top speed (m/s² at full throttle off the line). Punchy.")]
    [Range(1f, 60f)]
    public float Acceleration { get; set; } = 16f;

    [Tooltip("Top forward speed (m/s).")]
    [Range(1f, 120f)]
    public float MaxSpeed { get; set; } = 38f;

    [Tooltip("Top reverse speed (m/s).")]
    [Range(1f, 60f)]
    public float MaxReverseSpeed { get; set; } = 14f;

    [Tooltip("Acceleration taper near top speed. 1 = pulls hard all the way, higher = strong launch then " +
             "eases off as it approaches MaxSpeed (a believable power curve).")]
    [Range(1f, 4f)]
    public float PowerCurve { get; set; } = 2f;

    [Tooltip("Service-brake deceleration (m/s²) when you brake (throttle opposing motion).")]
    [Range(2f, 80f)]
    public float BrakeDecel { get; set; } = 28f;

    [Tooltip("Engine braking (m/s²) when coasting with no throttle — a natural lift-off slow-down.")]
    [Range(0f, 20f)]
    public float CoastDecel { get; set; } = 4f;

    [Header("Steering")]
    [Tooltip("Maximum steer angle of the steered wheels at low speed (degrees).")]
    [Range(0f, 60f)]
    public float MaxSteerAngle { get; set; } = 34f;

    [Tooltip("Steer angle shrinks toward this fraction at MaxSpeed (high-speed stability).")]
    [Range(0.1f, 1f)]
    public float HighSpeedSteerScale { get; set; } = 0.45f;

    [Tooltip("How quickly the steer angle reaches the input, in seconds (smaller = snappier turn-in).")]
    [Range(0.02f, 0.6f)]
    public float SteerTime { get; set; } = 0.12f;

    [Tooltip("How quickly the wheels return to centre when you let go, in seconds (smaller = crisper).")]
    [Range(0.02f, 0.6f)]
    public float SteerReturnTime { get; set; } = 0.07f;

    [Tooltip("Caps how fast the car can rotate (rad/s) so a hard corner stays tight but it never spins. " +
             "~1.7 = tight arcade; lower = wider, calmer turns.")]
    [Range(0.3f, 4f)]
    public float MaxTurnRate { get; set; } = 1.7f;

    [Tooltip("How sharply the car's heading reaches the commanded turn rate, in seconds (smaller = crisper " +
             "turn-in; also how hard it cancels self-steer when going straight).")]
    [Range(0.02f, 0.5f)]
    public float TurnResponse { get; set; } = 0.1f;

    [Tooltip("Wheelbase used to convert steer angle into a target turn rate (metres, ~front-to-rear axle).")]
    [Range(0.5f, 6f)]
    public float Wheelbase { get; set; } = 3f;

    [Header("Grip")]
    [Tooltip("How fast the car's velocity realigns to its heading, per second. High = glued on rails (goes " +
             "exactly where it points); lower = loose/driftier. This is the 'no ice-skating' knob.")]
    [Range(1f, 30f)]
    public float Grip { get; set; } = 14f;

    [Tooltip("Which wheels steer: front (a normal car). Rear-steer adds 4-wheel steering for tighter turns.")]
    public bool FrontWheelSteer { get; set; } = true;
    public bool RearWheelSteer { get; set; }

    [Header("Stability")]
    [Tooltip("Anti-roll: a vertical force pair at the track edges that resists body roll in hard corners " +
             "(N per unit of lateral lean). Keeps the car from tipping without forcing pitch flat on hills.")]
    [Range(0f, 200000f)]
    public float AntiRoll { get; set; } = 30000f;

    [Tooltip("Downforce pressed into the road at top speed (N), scaling with speed² — grip climbs with speed.")]
    [Range(0f, 50000f)]
    public float Downforce { get; set; } = 3000f;

    [Header("Handbrake / drift")]
    [Tooltip("Grip fraction while the handbrake is held (lower = the tail steps out further / longer slide). " +
             "1 = no drift, ~0.25 = a long controllable slide. Release to recover cleanly.")]
    [Range(0.02f, 1f)]
    public float HandbrakeGrip { get; set; } = 0.25f;

    [Tooltip("Extra deceleration (m/s²) the handbrake adds while held.")]
    [Range(0f, 40f)]
    public float HandbrakeDecel { get; set; } = 8f;

    [Tooltip("How much harder the car can turn while the handbrake is held (multiplies MaxTurnRate) — lets " +
             "the tail rotate out for a drift. 1 = same as normal, higher = sharper drift rotation.")]
    [Range(1f, 3f)]
    public float HandbrakeTurnBoost { get; set; } = 1.6f;

    Rigidbody chassis;
    readonly List<WheelCollider> wheels = new();
    float steer;          // current steer angle (radians), smoothed toward the input
    float steerVelocity;  // SmoothDamp state for the steer angle
    bool reversing;       // latched drive intent so a slide can't flip the drive direction mid-slide

    // The current smoothed steer, normalized to [-1,1] (right positive). Read by the ChaseCamera to
    // lead the aim into corners. [NotSerialized] keeps it out of the scene YAML and inspector.
    [NotSerialized]
    public float CurrentSteerNormalized =>
        MaxSteerAngle > 0f ? MathHelper.Clamp(steer / (MaxSteerAngle * Mathf.Deg2Rad), -1f, 1f) : 0f;

    protected internal override void OnAttach() {
        chassis = GetComponent<Rigidbody>();
        GatherWheels();
    }

    void GatherWheels() {
        wheels.Clear();
        CollectFrom(entity);
        foreach (WheelCollider wheel in wheels)
            wheel.SharedWheelCount = wheels.Count;

        void CollectFrom(Entity e) {
            foreach (Behaviour b in e.Behaviours)
                if (b is WheelCollider wheel)
                    wheels.Add(wheel);
            foreach (Entity child in e.DirectChildren())
                CollectFrom(child);
        }
    }

    protected internal override void FixedTick(in float dt) {
        if (!SceneManager.IsPlaying || chassis is null || chassis.InternalBody is null)
            return;
        if (wheels.Count == 0)
            GatherWheels();
        if (dt <= 0f || wheels.Count == 0)
            return;

        // --- Input ----------------------------------------------------------------------------
        // Triggers drive on a gamepad if present (RT = throttle, LT = brake/reverse); otherwise W/S.
        float throttle = ReadAxis(Keys.W, Keys.S)
                       + Input.GetGamepadAxis(GamepadAxis.RightTrigger)
                       - Input.GetGamepadAxis(GamepadAxis.LeftTrigger)
                       + Input.GetLeftStick().Y;
        throttle = MathHelper.Clamp(throttle, -1f, 1f);
        // D = steer right, A = steer left. The minus matches the engine's right/yaw convention behind
        // the chase camera (A/D were reversed without it).
        float steerInput = -(ReadAxis(Keys.D, Keys.A) + Input.GetLeftStick().X);
        steerInput = MathHelper.Clamp(steerInput, -1f, 1f);
        bool handbrake = Input.IsKeyDown(Keys.Space) || Input.IsGamepadButtonDown(GamepadButton.A);
        bool resetKey = Input.IsKeyDown(Keys.R) || Input.IsGamepadButtonDown(GamepadButton.Y);

        Transform chassisT = chassis.transform;

        // --- Flip recovery: re-right the car in place while the reset key is held. ---------------------
        if (resetKey) {
            ResetUpright(chassisT);
            return;
        }

        Vector3 forwardDir = chassisT.Forward;
        Vector3 vel = chassis.Velocity;
        var horiz = new Vector3(vel.X, 0f, vel.Z);
        float speed = horiz.Length();
        float signedSpeed = Vector3.Dot(horiz, new Vector3(forwardDir.X, 0f, forwardDir.Z));
        float speedFraction = MathHelper.Clamp(speed / MaxSpeed, 0f, 1f);

        // --- Steering angle: speed-scaled max lock, smoothed toward the input (snappier on return). ----
        float steerScale = MathHelper.Lerp(1f, HighSpeedSteerScale, speedFraction);
        float targetSteer = steerInput * MaxSteerAngle * Mathf.Deg2Rad * steerScale;
        float towardCentre = MathF.Abs(targetSteer) < MathF.Abs(steer) ? 1f : 0f;
        float smoothTime = MathHelper.Lerp(SteerTime, SteerReturnTime, towardCentre);
        steer = Mathf.SmoothDamp(steer, targetSteer, ref steerVelocity, smoothTime, dt);

        // --- Latched drive intent so a slide can't flip forward/back mid-corner. -----------------------
        if (throttle < -0.05f) reversing = true;
        else if (throttle > 0.05f) reversing = false;

        // --- Steer the steered wheels (visual) + count grounded wheels. --------------------------------
        Vector3 chassisPos = chassisT.WorldPosition;
        int grounded = 0;
        foreach (WheelCollider wheel in wheels) {
            bool isFront = Vector3.Dot(wheel.transform.WorldPosition - chassisPos, forwardDir) >= 0f;
            wheel.SteerAngle = (isFront && FrontWheelSteer) ? steer
                             : (!isFront && RearWheelSteer) ? -steer : 0f;
            if (wheel.IsGrounded)
                grounded++;
        }

        // --- Horizontal arcade dynamics + stability, only while on the wheels (no mid-air control). ----
        if (grounded > 0) {
            ApplyArcadeDrive(chassisT, horiz, speed, signedSpeed, throttle, handbrake, dt);
            ApplyStability(chassisT, speedFraction);
        }
    }

    // The on-rails arcade core. Heading turns at a clamped rate; speed eases toward the throttle target
    // from the ACTUAL speed (never bled by a turn); the velocity direction is rotated to follow the nose
    // while preserving that speed. The result corners on rails and keeps its speed, and can never spin.
    void ApplyArcadeDrive(Transform chassisT, Vector3 horiz, float speed, float signedSpeed,
        float throttle, bool handbrake, float dt) {
        Vector3 fwd = chassisT.Forward;
        var headingDir = new Vector3(fwd.X, 0f, fwd.Z);
        if (headingDir.LengthSquared() < 1e-6f)
            return;
        headingDir = headingDir.Normalized();

        // --- Heading: drive the yaw rate toward what the steering commands (bicycle model). The target
        // scales with ground speed and the latched drive sign (so a big slip angle can't flip it and
        // fishtail). Clamped to MaxTurnRate (× the handbrake boost) so a corner stays tight but never
        // spins. A critically-damped blend toward the target CANNOT overshoot — and because the target
        // is 0 going straight, it actively cancels any yaw the terrain induced (no self-steer).
        float driveSign = reversing ? -1f : 1f;
        float turnCap = MaxTurnRate * (handbrake ? HandbrakeTurnBoost : 1f);
        float targetYaw = speed * driveSign * MathF.Tan(MathHelper.Clamp(steer, -1.2f, 1.2f))
                        / MathF.Max(0.5f, Wheelbase);
        targetYaw = MathHelper.Clamp(targetYaw, -turnCap, turnCap);

        Vector3 yawAxis = chassisT.Up;
        float yawRate = Vector3.Dot(chassis.AngularVelocity, yawAxis);
        float yawBlend = 1f - MathF.Exp(-dt / MathF.Max(0.01f, TurnResponse));
        float newYaw = yawRate + (targetYaw - yawRate) * yawBlend;
        // Write back the corrected yaw, leaving roll/pitch angular velocity untouched (so the suspension
        // and anti-roll still control those). Hard-clamp the world-yaw as a final spin-proof backstop.
        Vector3 angVel = chassis.AngularVelocity + yawAxis * (newYaw - yawRate);
        angVel.Y = MathHelper.Clamp(angVel.Y, -turnCap, turnCap);
        chassis.AngularVelocity = angVel;

        // --- Speed: ease the signed speed toward the throttle target. Uses the actual horizontal speed
        // (signedSpeed) as the start so a turn never bleeds it. ----------------------------------------
        float targetSpeed, accel;
        if (throttle > 0.01f && !reversing) {
            float headroom = MathHelper.Clamp(1f - MathF.Max(0f, signedSpeed) / MaxSpeed, 0f, 1f);
            targetSpeed = throttle * MaxSpeed;
            accel = Acceleration * MathF.Pow(headroom, PowerCurve) + 0.5f; // +floor so it always creeps off 0
        } else if (throttle < -0.01f && reversing) {
            float headroom = MathHelper.Clamp(1f - MathF.Max(0f, -signedSpeed) / MaxReverseSpeed, 0f, 1f);
            targetSpeed = throttle * MaxReverseSpeed; // negative
            accel = Acceleration * 0.7f * MathF.Pow(headroom, PowerCurve) + 0.5f;
        } else {
            targetSpeed = 0f;                          // coasting: engine-brake toward a stop
            accel = CoastDecel;
        }
        // Braking: throttle opposing motion decelerates hard and straight (toward a stop, not past it).
        if (throttle * signedSpeed < -0.01f)
            accel = BrakeDecel;
        if (handbrake)
            accel = MathF.Max(accel, HandbrakeDecel);

        float newSigned = Mathf.MoveTowards(signedSpeed, targetSpeed, accel * dt);

        // --- Direction: rotate the velocity to follow the heading, preserving the new speed. Grip sets
        // how fast it snaps to the nose (so a flick still reads as a touch of slide, not a teleport).
        // The handbrake drops the grip so the tail steps out into a controllable drift. -----------------
        float grip = handbrake ? Grip * HandbrakeGrip : Grip;
        Vector3 driveDir = headingDir * (newSigned >= 0f ? 1f : -1f);
        Vector3 currentDir = speed > 0.2f ? horiz / speed : driveDir;
        float gripBlend = 1f - MathF.Exp(-grip * dt);
        Vector3 dir = currentDir + (driveDir - currentDir) * gripBlend;
        if (dir.LengthSquared() < 1e-6f)
            dir = driveDir;
        dir = dir.Normalized();
        Vector3 targetHoriz = dir * MathF.Abs(newSigned);

        // Apply the change as a force so it plays nice with Bepu (the vertical component is untouched, so
        // gravity, suspension, jumps and falling all still work).
        Vector3 deltaV = targetHoriz - horiz;
        chassis.AddForce(deltaV * (chassis.Mass / dt));
    }

    // Anti-roll keeps the car upright in hard corners; downforce presses it into the road at speed.
    void ApplyStability(Transform chassisT, float speedFraction) {
        Vector3 worldUp = Vector3.UnitY;
        Vector3 com = chassisT.WorldPosition;
        Vector3 right = chassisT.Right;
        Vector3 fwd = chassisT.Forward;

        // ROLL ONLY, as a VERTICAL FORCE PAIR at the track edges — a real anti-roll bar, NOT a body torque.
        // A torque about a tilted axis leaks into YAW (the chassis box inertia is very non-uniform, so
        // I^-1 maps the torque off-axis and it gains a world-up component), which made the car self-steer
        // on tilted terrain. Equal-and-opposite VERTICAL forces at horizontally-offset points make a roll-
        // only couple that CANNOT produce a net world-up torque — zero yaw leak. We deliberately don't
        // level PITCH: a car should follow the terrain's slope nose-up/down.
        Vector3 rightFlat = new Vector3(right.X, 0f, right.Z);
        if (rightFlat.LengthSquared() > 1e-6f) {
            rightFlat = rightFlat.Normalized();
            float rollLean = Vector3.Dot(right, worldUp);               // +ve = right side up (rolled left)
            float rollRate = Vector3.Dot(chassis.AngularVelocity, fwd); // roll about the forward axis
            float rollForce = (-rollLean * AntiRoll) - (rollRate * AntiRoll * 0.1f);
            chassis.AddForceAtPosition(worldUp * rollForce, com + rightFlat * 1f);   // ~track half-width
            chassis.AddForceAtPosition(worldUp * -rollForce, com - rightFlat * 1f);
        }

        // Downforce: push straight down, scaling with speed² for a racing feel (grip earns its keep).
        float df = Downforce * speedFraction * speedFraction;
        if (df > 0f)
            chassis.AddForce(-worldUp * df);
    }

    // Re-right the car in place: kill spin, level roll/pitch to upright (keeping heading), lift it a
    // touch so it doesn't re-clip the ground. Held on the reset key — a held key just keeps it level.
    void ResetUpright(Transform chassisT) {
        Vector3 fwd = chassisT.Forward;
        var flatFwd = new Vector3(fwd.X, 0f, fwd.Z);
        if (flatFwd.LengthSquared() < 1e-6f)
            flatFwd = Vector3.UnitZ;
        flatFwd = flatFwd.Normalized();

        float yaw = MathF.Atan2(flatFwd.X, flatFwd.Z);
        Quaternion upright = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw);
        Vector3 pos = chassisT.WorldPosition + Vector3.UnitY * 0.5f;
        chassis.Velocity = Vector3.Zero;
        chassis.AngularVelocity = Vector3.Zero;
        chassis.Teleport(pos, upright);
    }

    static float ReadAxis(Keys positive, Keys negative) =>
        (Input.IsKeyDown(positive) ? 1f : 0f) - (Input.IsKeyDown(negative) ? 1f : 0f);
}
