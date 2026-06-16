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

    [Tooltip("Top speed (m/s) the car eases toward at full throttle.")]
    [Range(1f, 120f)]
    public float MaxSpeed { get; set; } = 35f;

    [Tooltip("How punchy acceleration feels: multiplies the MotorForce/mass into m/s² used to ease the " +
             "speed toward target. Higher = snappier launch. (Drive is now velocity-based, not raw force.)")]
    [Range(0.05f, 2f)]
    public float AccelResponse { get; set; } = 0.5f;

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

    [Tooltip("Yaw response: how hard the car's turn rate is driven to what the steering commands (per " +
             "second). Also the stiffness of the closed yaw loop — high values actively CANCEL any yaw " +
             "drift (from anti-roll/terrain/uneven grip) when you're going straight, so the car holds " +
             "its line instead of self-steering. High = instant, crisp turn-in AND dead-straight tracking.")]
    [Range(2f, 40f)]
    public float YawResponse { get; set; } = 25f;

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
        // D = steer right, A = steer left. The minus flips the raw axis so it matches what the driver
        // sees behind the chase camera in this engine's yaw/right convention (A/D were reversed without).
        float steerInput = -(ReadAxis(Keys.D, Keys.A) + Input.GetLeftStick().X);
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

        // --- Feed the wheels for SUSPENSION + grounding only. The arcade DRIVE/GRIP is done directly on
        //     the chassis velocity below (see ApplyArcadeDrive) — the wheels no longer apply motor/brake/
        //     lateral forces, which is what made the handling a tangle of coupled forces that could spin
        //     out, scrub to a stall, or skid in reverse. Wheels still hold the car up and report contact.
        Vector3 chassisPos = chassisT.WorldPosition;
        int grounded = 0;
        foreach (WheelCollider wheel in wheels) {
            float along = Vector3.Dot(wheel.transform.WorldPosition - chassisPos, forwardDir);
            bool isFront = along >= 0f;
            wheel.SteerAngle = isFront ? currentSteer : 0f; // visual steer of the front wheels
            wheel.MotorForce = 0f;
            wheel.BrakeForce = 0f;
            wheel.Handbrake = false;
            if (wheel.IsGrounded)
                grounded++;
        }

        // --- Direct arcade drive + steering, only while on the wheels (no mid-air control).
        if (grounded > 0) {
            ApplyStability(chassisT, speedFraction, dt);
            ApplyArcadeDrive(chassisT, throttle, handbrake, dt);
        }
    }

    // The CANONICAL arcade model — one place, structurally stable. Heading turns with the steering; the
    // horizontal VELOCITY is set to point along that heading at a throttle-controlled speed. Because the
    // velocity is REBUILT from (headingDir × speed) every step, the car can NEVER skid in reverse, spin
    // out, or scrub to a dead stop in a turn — those were all artifacts of summed, fighting tyre forces.
    // Speed eases toward a target (full throttle → MaxSpeed, brake/coast → down); direction eases toward
    // the nose by ArcadeGrip (1 = glued/no slide, lower = a little drift). Vertical velocity (gravity +
    // suspension) is left untouched, so the car still falls, follows terrain, and the suspension works.
    void ApplyArcadeDrive(Transform chassisT, float throttle, bool handbrake, float dt) {
        Vector3 vel = chassis.Velocity;
        var horiz = new Vector3(vel.X, 0f, vel.Z);
        float speed = horiz.Length();

        Vector3 fwd = chassisT.Forward;
        var headingDir = new Vector3(fwd.X, 0f, fwd.Z);
        if (headingDir.LengthSquared() < 1e-6f)
            return;
        headingDir = headingDir.Normalized();

        // Current signed speed along the heading (so we know if we're rolling forward or back).
        float signed = Vector3.Dot(horiz, headingDir);

        // --- Target speed from throttle. Forward → +MaxSpeed, reverse → -reverseTop, none → coast down.
        float targetSpeed;
        float accel;
        if (throttle > 0.01f) {
            targetSpeed = throttle * MaxSpeed;
            accel = (MotorForce / chassis.Mass) * AccelResponse; // m/s² available to reach it (punchy)
        } else if (throttle < -0.01f) {
            targetSpeed = throttle * MaxSpeed * 0.5f;             // reverse tops out at half
            accel = (ReverseForce / chassis.Mass) * AccelResponse;
        } else {
            targetSpeed = 0f;                                     // coasting: gentle slow-down
            accel = (MotorForce / chassis.Mass) * 0.15f;
        }
        if (handbrake) { targetSpeed = 0f; accel = (BrakeForce / chassis.Mass) * AccelResponse; }
        // Braking (throttle opposing motion) decelerates harder.
        if (throttle * signed < -0.01f)
            accel = (BrakeForce / chassis.Mass) * AccelResponse;

        // Ease the signed speed toward the target at `accel` (m/s per second), framerate-correct.
        float newSigned = MoveToward(signed, targetSpeed, accel * dt);

        // --- Rebuild the horizontal velocity: point it along the heading (forward/back per sign), at the
        //     new speed. ArcadeGrip controls how fast the DIRECTION snaps to the heading (so a flick still
        //     reads as a touch of slide, not a teleport of the velocity), but the result always tracks the
        //     nose — no reverse skid possible. At very low speed just use the heading directly.
        Vector3 driveDir = headingDir * MathF.Sign(newSigned == 0 ? 1f : newSigned);
        Vector3 currentDir = speed > 0.2f ? horiz / speed : driveDir;
        float gripBlend = 1f - MathF.Exp(-ArcadeGrip * dt);
        Vector3 dir = Vector3.Normalize(Vector3.Lerp(currentDir, driveDir, gripBlend));
        Vector3 targetHoriz = dir * MathF.Abs(newSigned);

        // Apply as a velocity change via force (plays nice with Bepu; vertical component untouched).
        Vector3 deltaV = targetHoriz - horiz;
        chassis.AddForce(deltaV * (chassis.Mass / dt));

        // --- Heading: torque the yaw toward the steering's commanded rate (visual nose + turn).
        Vector3 yawAxis = chassisT.Up;
        float yawRate = Vector3.Dot(chassis.AngularVelocity, yawAxis);
        float yawError = targetYawRate - yawRate;
        float yawTorque = MathHelper.Clamp(yawError * YawResponse * chassis.Mass,
            -MaxYawRate * YawResponse * chassis.Mass, MaxYawRate * YawResponse * chassis.Mass);
        chassis.AddTorque(yawAxis * yawTorque);
    }

    // Anti-roll keeps the car upright in hard corners; downforce presses it into the road at speed so
    // grip climbs with velocity. Both are gentle, framerate-correct torques/forces — they plant the
    // car without overriding the player.
    void ApplyStability(Transform chassisT, float speedFraction, float dt) {
        Vector3 worldUp = Vector3.UnitY;
        Vector3 com = chassisT.WorldPosition;

        // Anti-roll / anti-pitch as VERTICAL FORCE PAIRS — a real anti-roll bar, NOT a body torque.
        // A torque about a tilted axis leaks into YAW: the chassis box's inertia is very non-uniform
        // (Iyaw ~ 4.6x Iroll), so Bepu maps the torque through I^-1 and the result isn't parallel to
        // the axis — it gains a world-up (yaw) component. On tilting terrain that ran every frame and
        // the car self-steered ("goes weird after a while"). Equal-and-opposite VERTICAL forces at
        // laterally/longitudinally symmetric points produce a roll/pitch-only couple that CANNOT make
        // a net world-up torque — zero yaw leak by construction, while still levelling the car.
        Vector3 right = chassisT.Right;
        Vector3 fwd = chassisT.Forward;

        // ROLL ONLY: keep the car from tipping SIDEWAYS. Lateral lean = how far the car's right axis
        // points up/down; push the low side up / high side down with a vertical couple at the track
        // edges, damped by the roll rate. We deliberately do NOT level PITCH — a car should follow the
        // terrain's slope nose-up/down; forcing pitch flat fought the hills and pitched the car wildly.
        // The offset points use the HORIZONTAL projection of `right` (no vertical component): a vertical
        // force at a point that is also offset VERTICALLY would make a yaw torque, which is the self-
        // steering-on-terrain bug. Horizontal offset + vertical force = pure roll couple, zero yaw.
        Vector3 rightFlat = new Vector3(right.X, 0f, right.Z);
        if (rightFlat.LengthSquared() > 1e-6f) {
            rightFlat = rightFlat.Normalized();
            float rollLean = Vector3.Dot(right, worldUp);              // +ve = right side up (rolled left)
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

    static float ReadAxis(Keys positive, Keys negative) =>
        (Input.IsKeyDown(positive) ? 1f : 0f) - (Input.IsKeyDown(negative) ? 1f : 0f);

    static float MoveToward(float current, float target, float maxDelta) {
        float diff = target - current;
        if (MathF.Abs(diff) <= maxDelta)
            return target;
        return current + MathF.Sign(diff) * maxDelta;
    }
}
