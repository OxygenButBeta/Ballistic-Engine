using OpenTK.Windowing.GraphicsLibraryFramework; // Keys enum, same source the Input facade uses

namespace BallisticEngine;

// Arcade vehicle controller (P7 demo capstone): drives a chassis Rigidbody through its WheelColliders.
// Reads input (throttle/brake/steer), distributes motor force to the driven wheels, steers the front
// wheels, and applies the handbrake. Sits on the chassis entity; the wheels are WheelCollider
// components on child entities. Built entirely on the new physics surface — force-at-position (P2),
// sphere-cast suspension (P2), restitution-stable contacts (P1).
//
// ARCADE feel (GTA / Need-for-Speed), rebuilt:
//   * STEERING is instant and sharp — the wheel angle chases the input fast (with just enough
//     weight to feel planted, not twitchy) and the lock SHRINKS with speed for high-speed stability.
//   * ACCELERATION is punchy — a strong launch force that tapers smoothly toward top speed, plus a
//     separate, gentler reverse.
//   * The car STAYS PLANTED — an anti-roll torque keeps it from tipping in hard corners and a little
//     downforce presses the tyres into the road at speed (more grip the faster you go).
// With the sticky WheelCollider lateral model, the result corners on rails instead of skating.
[Component("Vehicle Controller", "Physics")]
public class VehicleController : Behaviour {
    [Header("Drive")]
    [Tooltip("Forward force per driven wheel at full throttle (newtons). Punchy launch.")]
    [Range(0f, 80000f)]
    public float MotorForce { get; set; } = 16000f;

    [Tooltip("Reverse force per driven wheel (newtons) — usually weaker than forward.")]
    [Range(0f, 80000f)]
    public float ReverseForce { get; set; } = 9000f;

    [Tooltip("Braking force per wheel (newtons).")]
    [Range(0f, 80000f)]
    public float BrakeForce { get; set; } = 20000f;

    [Tooltip("Top speed (m/s); motor force tapers to zero as the car approaches it.")]
    [Range(1f, 120f)]
    public float MaxSpeed { get; set; } = 35f;

    [Tooltip("Acceleration taper shape near top speed. 1 = linear, 2 = strong launch then ease off.")]
    [Range(1f, 4f)]
    public float AccelCurve { get; set; } = 2f;

    [Header("Steering")]
    [Tooltip("Maximum steer angle of the front wheels at low speed (degrees).")]
    [Range(0f, 60f)]
    public float MaxSteerAngle { get; set; } = 35f;

    [Tooltip("Steer angle shrinks toward this fraction at MaxSpeed (high-speed stability).")]
    [Range(0.1f, 1f)]
    public float HighSpeedSteerScale { get; set; } = 0.45f;

    [Tooltip("How fast the steer angle chases the input (per second). High = instant arcade response.")]
    [Range(1f, 30f)]
    public float SteerResponse { get; set; } = 14f;

    [Tooltip("How fast the steer angle returns to centre when you let go (per second).")]
    [Range(1f, 40f)]
    public float SteerReturn { get; set; } = 20f;

    [Header("Stability")]
    [Tooltip("Anti-roll torque that keeps the car from tipping in hard corners (N·m per rad of tilt).")]
    [Range(0f, 200000f)]
    public float AntiRoll { get; set; } = 40000f;

    [Tooltip("Downforce: extra grip pressed into the road, scaling with speed (N at MaxSpeed).")]
    [Range(0f, 50000f)]
    public float Downforce { get; set; } = 2000f;

    [Tooltip("Yaw response: how fast the car's turn rate reaches what the steering commands (per " +
             "second). High = instant, crisp arcade turn-in; lower = heavier. The nose tracks this " +
             "exactly (no overshoot), and the grip turns the velocity with it, so it never spins out.")]
    [Range(2f, 30f)]
    public float YawResponse { get; set; } = 10f;

    [Tooltip("Maximum turn rate (rad/s) — caps how fast the car can rotate so a hard corner stays tight " +
             "but never spins the car so fast it rolls over. ~1.5 = tight arcade; lower = wider, calmer.")]
    [Range(0.3f, 4f)]
    public float MaxYawRate { get; set; } = 1.6f;

    [Tooltip("Wheelbase used to convert steer angle into the target yaw rate (metres). ~front-to-rear " +
             "axle distance; the demo car is ~3 m. Smaller = tighter turns for the same steer angle.")]
    [Range(0.5f, 6f)]
    public float Wheelbase { get; set; } = 3f;

    [Tooltip("Arcade grip: how fast the velocity is realigned to the car's heading, per second (it " +
             "redirects the velocity, PRESERVING speed — it never brakes). High = glued on rails, the " +
             "car goes exactly where it points; low = loose/driftier. This is the no-skating feel.")]
    [Range(0f, 40f)]
    public float ArcadeGrip { get; set; } = 16f;

    [Header("Layout")]
    [Tooltip("Wheels in front of the chassis centre steer. Behind it (or all, configurable) drive.")]
    public bool FrontWheelDrive { get; set; }
    public bool RearWheelDrive { get; set; } = true;

    Rigidbody chassis;
    readonly List<WheelCollider> wheels = new();
    float currentSteer;   // radians, smoothed toward the target each step (instant-but-weighted feel)
    float targetYawRate;  // rad/s the steering commands this step (set in FixedTick, used by the yaw controller)
    bool reversing;       // latched drive intent (from throttle) so slip can't flip the steer/grip direction

    protected internal override void OnAttach() {
        chassis = GetComponent<Rigidbody>();
        GatherWheels();
    }

    void GatherWheels() {
        wheels.Clear();
        CollectFrom(entity);
        foreach (WheelCollider wheel in wheels)
            wheel.SharedWheelCount = wheels.Count; // so each wheel carries its share of the load

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
        if (dt <= 0f)
            return;

        float throttle = ReadAxis(Keys.W, Keys.S) + Input.GetLeftStick().Y;
        throttle = MathHelper.Clamp(throttle, -1f, 1f);
        float steerInput = ReadAxis(Keys.D, Keys.A) + Input.GetLeftStick().X;
        steerInput = MathHelper.Clamp(steerInput, -1f, 1f);
        bool handbrake = Input.IsKeyDown(Keys.Space);

        Transform chassisT = chassis.transform;
        Vector3 forwardDir = chassisT.Forward;
        float signedSpeed = Vector3.Dot(chassis.Velocity, forwardDir); // +forward, -reverse
        float speed = chassis.Velocity.Length();
        float speedFraction = MathHelper.Clamp(speed / MaxSpeed, 0f, 1f);

        // --- Steering: instant, sharp, speed-scaled, smoothed toward the target for a weighted feel.
        float steerScale = MathHelper.Lerp(1f, HighSpeedSteerScale, speedFraction);
        float targetSteer = steerInput * MaxSteerAngle * MathHelper.DegreesToRadians(1f) * steerScale;
        // Faster return-to-centre than turn-in so the car settles crisply (arcade snap).
        float rate = MathF.Abs(targetSteer) > MathF.Abs(currentSteer) ? SteerResponse : SteerReturn;
        currentSteer = MoveToward(currentSteer, targetSteer, rate * dt);

        // Target YAW RATE the steering commands (bicycle model: yaw = v·tan(steer)/wheelbase). It scales
        // with how fast you're going (|speed|, the GROUND speed). Crucially it must NOT depend on the
        // forward-PROJECTED speed: at a big slip angle that flips sign and the target whipsaws, snapping
        // the car around (a brutal fishtail). The turn direction is the steer sign; the reverse case is
        // keyed off the player's THROTTLE intent (latched, not the slip-corrupted velocity), so the only
        // time the target reverses is when the driver is actually backing up — never mid-slide.
        if (throttle < -0.05f) reversing = true;
        else if (throttle > 0.05f) reversing = false;
        float driveSign = reversing ? -1f : 1f;
        targetYawRate = speed * driveSign * MathF.Tan(MathHelper.Clamp(currentSteer, -1.2f, 1.2f))
                        / MathF.Max(0.5f, Wheelbase);
        // Cap the turn rate — the raw bicycle value at speed with full lock is absurdly high (a 5 m
        // radius at 14 m/s ≈ 2.5 rad/s) and would spin/flip the car. MaxYawRate keeps cornering tight
        // but sane, and is the single biggest lever on "tight arcade turns vs rolls over".
        targetYawRate = MathHelper.Clamp(targetYawRate, -MaxYawRate, MaxYawRate);

        // --- Motor: punchy launch that tapers toward top speed. Forward and reverse handled apart.
        float motor = 0f;
        if (throttle > 0.01f) {
            float headroom = MathHelper.Clamp(1f - signedSpeed / MaxSpeed, 0f, 1f);
            motor = throttle * MotorForce * MathF.Pow(headroom, AccelCurve);
        } else if (throttle < -0.01f) {
            float reverseTop = MaxSpeed * 0.5f; // reverse tops out slower
            float headroom = MathHelper.Clamp(1f + signedSpeed / reverseTop, 0f, 1f);
            motor = throttle * ReverseForce * MathF.Pow(headroom, AccelCurve);
        }

        // Brake when the throttle clearly opposes forward motion (press S while rolling forward = brake,
        // not instant reverse). Requires a real forward/back speed so a momentary slip (signedSpeed
        // dipping negative mid-corner) can't false-trigger the brakes and bog the car.
        bool braking = MathF.Abs(throttle) > 0.01f && signedSpeed * throttle < -1.5f;
        if (braking)
            motor = 0f;

        // --- Identify front/rear by each wheel's position along the chassis forward axis, drive them.
        Vector3 chassisPos = chassisT.WorldPosition;
        int grounded = 0;
        foreach (WheelCollider wheel in wheels) {
            float along = Vector3.Dot(wheel.transform.WorldPosition - chassisPos, forwardDir);
            bool isFront = along >= 0f;

            wheel.SteerAngle = isFront ? currentSteer : 0f;
            bool driven = (isFront && FrontWheelDrive) || (!isFront && RearWheelDrive);
            wheel.MotorForce = driven ? motor : 0f;
            wheel.BrakeForce = braking ? BrakeForce : 0f;
            // Handbrake locks the REAR wheels (lets the tail rotate; front keeps steering authority).
            wheel.Handbrake = handbrake && !isFront;
            if (wheel.IsGrounded)
                grounded++;
        }

        // --- Stability + steering + arcade grip: only when on the wheels (no mid-air control).
        if (grounded > 0) {
            ApplyStability(chassisT, speedFraction, dt);
            ApplyArcadeHandling(chassisT, dt);
        }
    }

    // The unified arcade handling step — the single source of truth for turning, and the reason the
    // car corners on rails. Two coupled velocity-level moves, applied together so they can't fight:
    //   1) YAW: drive the body's yaw rate to the rate the steering commands (YawResponse, capped by
    //      MaxYawRate so a hard corner can't spin the car fast enough to roll). This turns the NOSE.
    //   2) GRIP: rotate the VELOCITY vector toward the car's actual heading, PRESERVING its magnitude
    //      (ArcadeGrip = how fast it realigns). This makes the trajectory follow the nose — the car
    //      goes where it points instead of skating, and keeps its speed (it redirects, never brakes).
    // Reading the real forward direction for the grip (instead of a hand-computed yaw angle) keeps the
    // turn direction correct under any euler/quaternion convention, and using the GROUND speed (not the
    // slip-projected speed) for the yaw target keeps it from whipsawing. Pitch/roll stay with the
    // suspension + anti-roll, so bumps and lean still read physically.
    void ApplyArcadeHandling(Transform chassisT, float dt) {
        // 1) YAW: drive the body's yaw rate toward what the steering commands (framerate-correct, no
        //    overshoot). Set only the yaw component of angular velocity; pitch/roll stay with the
        //    suspension. This rotates the NOSE crisply to the asked-for turn rate.
        Vector3 yawAxis = chassisT.Up;
        Vector3 angVel = chassis.AngularVelocity;
        float yawRate = Vector3.Dot(angVel, yawAxis);
        float yawBlend = 1f - MathF.Exp(-YawResponse * dt);
        float newYaw = MathHelper.Lerp(yawRate, targetYawRate, yawBlend);
        chassis.AngularVelocity = angVel + yawAxis * (newYaw - yawRate);

        // 2) GRIP: rotate the velocity vector toward the car's ACTUAL heading (read from the transform,
        //    so the direction sign is always correct), PRESERVING its magnitude. This is the no-skating
        //    feel: the car keeps its speed but its trajectory follows where the nose now points. Reading
        //    the real forward direction (rather than rotating by a hand-computed yaw angle) keeps the
        //    turn direction correct regardless of the euler/quaternion sign conventions.
        Vector3 vel = chassis.Velocity;
        var horiz = new Vector3(vel.X, 0f, vel.Z);
        float speed = horiz.Length();
        if (speed < 0.3f)
            return;

        Vector3 fwd = chassisT.Forward;
        var horizFwd = new Vector3(fwd.X, 0f, fwd.Z);
        if (horizFwd.LengthSquared() < 1e-6f)
            return;
        horizFwd = horizFwd.Normalized();

        // Realign toward forward, or backward when reversing — keyed off the LATCHED drive intent (not
        // the velocity-vs-heading dot, which flips past 90° of slip and would make the grip yank the car
        // around). So the grip always pulls the trajectory toward the nose the driver is heading.
        Vector3 desiredDir = reversing ? -horizFwd : horizFwd;

        // Blend the velocity direction toward the heading by ArcadeGrip (per-second rate via exp), then
        // restore the original speed — a pure redirection, never a brake.
        float gripBlend = 1f - MathF.Exp(-ArcadeGrip * dt);
        Vector3 newDir = Vector3.Normalize(Vector3.Lerp(horiz / speed, desiredDir, gripBlend));
        Vector3 newHoriz = newDir * speed;
        Vector3 deltaV = newHoriz - horiz;
        chassis.AddForce(deltaV * (chassis.Mass / dt));
    }

    // Anti-roll keeps the car upright in hard corners; downforce presses it into the road at speed so
    // grip climbs with velocity. Both are gentle, framerate-correct torques/forces — they plant the
    // car without overriding the player.
    void ApplyStability(Transform chassisT, float speedFraction, float dt) {
        Vector3 up = chassisT.Up;

        // Anti-roll: torque that rotates the body's up axis back toward world up, damped by roll rate.
        Vector3 worldUp = Vector3.UnitY;
        Vector3 tiltAxis = Vector3.Cross(up, worldUp); // axis to rotate around; |.| = sin(tilt)
        float tilt = MathF.Asin(MathHelper.Clamp(tiltAxis.Length(), 0f, 1f));
        if (tilt > 1e-4f) {
            Vector3 axis = tiltAxis / tiltAxis.Length();
            // Spring toward upright minus damping along the same axis (critically-ish damped).
            float rollRate = Vector3.Dot(chassis.AngularVelocity, axis);
            Vector3 torque = axis * (AntiRoll * tilt) - axis * (AntiRoll * 0.15f * rollRate);
            chassis.AddTorque(torque);
        }

        // Downforce: push straight down, scaling with speed² for a racing feel (grip earns its keep).
        float df = Downforce * speedFraction * speedFraction;
        if (df > 0f)
            chassis.AddForce(-worldUp * df);
    }

    static float ReadAxis(Keys positive, Keys negative) =>
        (Input.IsKeyDown(positive) ? 1f : 0f) - (Input.IsKeyDown(negative) ? 1f : 0f);

    static float MoveToward(float current, float target, float maxDelta) {
        float diff = target - current;
        if (MathF.Abs(diff) <= maxDelta)
            return target;
        return current + MathF.Sign(diff) * maxDelta;
    }
}
