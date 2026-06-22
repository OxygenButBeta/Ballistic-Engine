using OpenTK.Windowing.GraphicsLibraryFramework;

namespace BallisticEngine;

[Component("Vehicle Controller", "Physics")]
public class VehicleController : Behaviour {
    [Header("Drive")]
    [Tooltip("Peak engine pull (m/s² at the torque sweet-spot, full throttle). The gearbox shapes how " +
             "this is delivered across the rev range; higher = punchier overall.")]
    [Range(1f, 80f)]
    public float EnginePower { get; set; } = 34f;

    [Tooltip("Top forward speed (m/s).")]
    [Range(1f, 120f)]
    public float MaxSpeed { get; set; } = 38f;

    [Tooltip("Top reverse speed (m/s).")]
    [Range(1f, 60f)]
    public float MaxReverseSpeed { get; set; } = 14f;

    [Tooltip("Service-brake deceleration (m/s²) when you brake (throttle opposing motion).")]
    [Range(2f, 80f)]
    public float BrakeDecel { get; set; } = 30f;

    [Tooltip("Engine braking (m/s²) when coasting with no throttle — a natural lift-off slow-down.")]
    [Range(0f, 20f)]
    public float CoastDecel { get; set; } = 4f;

    [Header("Gearbox (automatic)")]
    [Tooltip("Number of forward gears. Each gear pulls hard then the revs climb to the shift point and " +
             "it changes up (a brief torque dip) — the readable, non-linear 'car' acceleration.")]
    [Range(1, 8)]
    public int GearCount { get; set; } = 5;

    [Tooltip("Engine revs (0..1 of redline) at which it shifts UP. Lower = short-shifts early (relaxed), " +
             "higher = holds each gear to the redline (sporty).")]
    [Range(0.5f, 1f)]
    public float UpshiftRpm { get; set; } = 0.92f;

    [Tooltip("Engine revs (0..1) at which it shifts DOWN when slowing. Below the upshift point so it " +
             "doesn't hunt between gears.")]
    [Range(0.1f, 0.7f)]
    public float DownshiftRpm { get; set; } = 0.32f;

    [Tooltip("Gear-change time in seconds — the brief torque cut you feel on each shift. 0 = instant " +
             "(no shift feel), ~0.25 = a clear shift kick.")]
    [Range(0f, 0.8f)]
    public float ShiftTime { get; set; } = 0.22f;

    [Tooltip("Spread of the torque curve across the rev range. The engine pulls hardest in the mid-revs " +
             "and tapers near idle and redline; this controls how peaky that is. 1 = broad, 3 = peaky.")]
    [Range(1f, 3f)]
    public float TorqueCurve { get; set; } = 1.6f;

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
             "exactly where it points); lower = loose/driftier. This is the 'no ice-skating' knob. The GTA " +
             "balanced-sport default keeps a touch of slide so it feels weighty, not on a track.")]
    [Range(1f, 30f)]
    public float Grip { get; set; } = 11f;

    [Tooltip("Which wheels steer: front (a normal car). Rear-steer adds 4-wheel steering for tighter turns.")]
    public bool FrontWheelSteer { get; set; } = true;
    public bool RearWheelSteer { get; set; }

    [Header("Stability")]
    [Tooltip("Anti-roll: a vertical force pair at the track edges that resists body roll in hard corners " +
             "(N per unit of lateral lean). Keeps the car from tipping without forcing pitch flat on hills. " +
             "The GTA-sport default leaves a little visible body LEAN in corners (raise it to stiffen).")]
    [Range(0f, 200000f)]
    public float AntiRoll { get; set; } = 20000f;

    [Tooltip("Downforce pressed into the road at top speed (N), scaling with speed² — grip climbs with speed.")]
    [Range(0f, 50000f)]
    public float Downforce { get; set; } = 2200f;

    [Tooltip("Air control: how strongly you can pitch/roll the car WHILE AIRBORNE (off a ramp) to set up " +
             "the landing (m/s² of angular nudge). The car still flies on pure momentum — this only tilts " +
             "it. W/S = nose down/up, A/D = roll. 0 = no air control (pure physics flight).")]
    [Range(0f, 8f)]
    public float AirControl { get; set; } = 2.5f;

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

    [Header("Flip recovery")]
    [Tooltip("Self-right with A/D when flipped (GTA-style): when the car lands on its side or roof, hold A " +
             "or D and it rolls that way back onto its wheels. Arcade torque — only engages when actually " +
             "rolled over. The R / gamepad-Y key still snaps it upright in place. Default on.")]
    public bool SelfRight { get; set; } = true;

    [Tooltip("How strongly A/D rolls the car back over when it's flipped (angular accel, rad/s²). Higher = " +
             "flips back faster.")]
    [Range(1f, 30f)]
    public float SelfRightStrength { get; set; } = 9f;

    Rigidbody chassis;
    readonly List<WheelCollider> wheels = new();
    float steer;
    float steerVelocity;
    bool reversing;

    int gear = 1;
    float shiftTimer;
    float engineRpm;

    [NotSerialized]
    public float CurrentSteerNormalized =>
        MaxSteerAngle > 0f ? MathHelper.Clamp(steer / (MaxSteerAngle * Mathf.Deg2Rad), -1f, 1f) : 0f;

    [NotSerialized] public int CurrentGear { get; private set; }

    [NotSerialized] public float EngineRpm => engineRpm;

    [NotSerialized]
    public float SpeedKmh => chassis is not null
        ? new Vector3(chassis.Velocity.X, 0f, chassis.Velocity.Z).Length() * 3.6f : 0f;

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

        float throttle = ReadAxis(Keys.W, Keys.S)
                         + Input.GetGamepadAxis(GamepadAxis.RightTrigger)
                         - Input.GetGamepadAxis(GamepadAxis.LeftTrigger)
                         + Input.GetLeftStick().Y;
        throttle = MathHelper.Clamp(throttle, -1f, 1f);
        float steerInput = -(ReadAxis(Keys.D, Keys.A) + Input.GetLeftStick().X);
        steerInput = MathHelper.Clamp(steerInput, -1f, 1f);
        bool handbrake = Input.IsKeyDown(Keys.Space) || Input.IsGamepadButtonDown(GamepadButton.A);
        bool resetKey = Input.IsKeyDown(Keys.R) || Input.IsGamepadButtonDown(GamepadButton.Y);

        Transform chassisT = chassis.transform;

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

        float steerScale = MathHelper.Lerp(1f, HighSpeedSteerScale, speedFraction);
        float targetSteer = steerInput * MaxSteerAngle * Mathf.Deg2Rad * steerScale;
        float towardCentre = MathF.Abs(targetSteer) < MathF.Abs(steer) ? 1f : 0f;
        float smoothTime = MathHelper.Lerp(SteerTime, SteerReturnTime, towardCentre);
        steer = Mathf.SmoothDamp(steer, targetSteer, ref steerVelocity, smoothTime, dt);

        float upDot = Vector3.Dot(chassisT.Up, Vector3.UnitY);
        if (SelfRight && upDot < 0.7f) {
            Vector3 axis = Vector3.Cross(chassisT.Up, Vector3.UnitY);
            float steerBias = MathF.Abs(steerInput) > 0.05f ? -steerInput : 1f;
            float invert = 1f - MathF.Min(1f, axis.Length());
            axis += chassisT.Forward * (steerBias * invert);
            if (axis.LengthSquared() > 1e-6f) {
                Vector3 dir = axis.Normalized();
                const float flipRate = 4.5f;
                float curRate = Vector3.Dot(chassis.AngularVelocity, dir);
                chassis.AddTorque(dir * ((flipRate - curRate) * SelfRightStrength * chassis.Mass));
                Vector3 ang = chassis.AngularVelocity;
                chassis.AngularVelocity = dir * Vector3.Dot(ang, dir);
                chassis.Velocity = new Vector3(chassis.Velocity.X * 0.9f, chassis.Velocity.Y, chassis.Velocity.Z * 0.9f);
            }
            return;
        }

        if (throttle < -0.05f) reversing = true;
        else if (throttle > 0.05f) reversing = false;

        Vector3 chassisPos = chassisT.WorldPosition;
        int grounded = 0;
        Vector3 groundNormal = Vector3.Zero;
        foreach (WheelCollider wheel in wheels) {
            bool isFront = Vector3.Dot(wheel.transform.WorldPosition - chassisPos, forwardDir) >= 0f;
            wheel.SteerAngle = (isFront && FrontWheelSteer) ? steer
                             : (!isFront && RearWheelSteer) ? -steer : 0f;
            if (wheel.IsGrounded) {
                grounded++;
                groundNormal += wheel.ContactNormal;
            }
        }

        groundNormal = groundNormal.LengthSquared() > 1e-6f ? groundNormal.Normalized() : Vector3.UnitY;

        if (grounded > 0) {
            ApplyArcadeDrive(chassisT, vel, horiz, speed, signedSpeed, throttle, handbrake, groundNormal, dt);
            ApplyStability(chassisT, speedFraction);
        } else {
            ApplyAirControl(chassisT, throttle, steerInput, dt);
        }
    }

    void ApplyAirControl(Transform chassisT, float throttle, float steerInput, float dt) {
        if (AirControl <= 0f)
            return;
        Vector3 pitchAxis = chassisT.Right;
        Vector3 rollAxis = chassisT.Forward;
        float gain = AirControl * chassis.Mass;
        chassis.AddTorque(pitchAxis * (-throttle * gain));
        chassis.AddTorque(rollAxis * (steerInput * gain));
        chassis.AngularVelocity *= 1f / (1f + dt * 1.5f);
    }

    void ApplyArcadeDrive(Transform chassisT, Vector3 vel, Vector3 horiz, float speed, float signedSpeed,
        float throttle, bool handbrake, Vector3 groundNormal, float dt) {
        Vector3 fwd = chassisT.Forward;
        var headingDir = new Vector3(fwd.X, 0f, fwd.Z);
        if (headingDir.LengthSquared() < 1e-6f)
            return;
        headingDir = headingDir.Normalized();

        float driveSign = reversing ? -1f : 1f;
        float turnCap = MaxTurnRate * (handbrake ? HandbrakeTurnBoost : 1f);
        float targetYaw = speed * driveSign * MathF.Tan(MathHelper.Clamp(steer, -1.2f, 1.2f))
                        / MathF.Max(0.5f, Wheelbase);
        targetYaw = MathHelper.Clamp(targetYaw, -turnCap, turnCap);

        Vector3 yawAxis = chassisT.Up;
        float yawRate = Vector3.Dot(chassis.AngularVelocity, yawAxis);
        float yawBlend = 1f - MathF.Exp(-dt / MathF.Max(0.01f, TurnResponse));
        float newYaw = yawRate + (targetYaw - yawRate) * yawBlend;
        Vector3 angVel = chassis.AngularVelocity + yawAxis * (newYaw - yawRate);
        angVel.Y = MathHelper.Clamp(angVel.Y, -turnCap, turnCap);
        chassis.AngularVelocity = angVel;

        float targetSpeed, accel;
        if (throttle > 0.01f && !reversing) {
            targetSpeed = throttle * MaxSpeed;
            accel = throttle * UpdateGearbox(MathF.Max(0f, signedSpeed), dt) + 0.5f;
        } else if (throttle < -0.01f && reversing) {
            CurrentGear = -1; engineRpm = MathHelper.Clamp(-signedSpeed / MaxReverseSpeed, 0f, 1f);
            float headroom = MathHelper.Clamp(1f - MathF.Max(0f, -signedSpeed) / MaxReverseSpeed, 0f, 1f);
            targetSpeed = throttle * MaxReverseSpeed;
            accel = EnginePower * 0.55f * headroom + 0.5f;
        } else {
            targetSpeed = 0f;
            accel = CoastDecel;
            UpdateGearboxIdle(MathF.Max(0f, signedSpeed), dt);
        }

        if (throttle * signedSpeed < -0.01f)
            accel = BrakeDecel;
        if (handbrake)
            accel = MathF.Max(accel, HandbrakeDecel);

        Vector3 carFwd = chassisT.Forward;
        var driveAxis = new Vector3(carFwd.X, 0f, carFwd.Z);
        driveAxis = driveAxis.LengthSquared() > 1e-6f ? driveAxis.Normalized() : headingDir;

        float forwardVel = Vector3.Dot(horiz, driveAxis);
        float driveAccel;
        if (MathF.Abs(targetSpeed) < 0.01f) {
            driveAccel = -MathF.Sign(forwardVel) * MathF.Min(accel, MathF.Abs(forwardVel) / dt);
        } else if (forwardVel * MathF.Sign(targetSpeed) > MathF.Abs(targetSpeed)) {
            driveAccel = MathF.Sign(targetSpeed) * -CoastDecel;
        } else {
            driveAccel = MathF.Sign(targetSpeed) * accel;
        }
        chassis.AddForce(driveAxis * (driveAccel * chassis.Mass));

        Vector3 sideAxis = new Vector3(driveAxis.Z, 0f, -driveAxis.X);
        float rightVel = Vector3.Dot(horiz, sideAxis);
        float grip = handbrake ? Grip * HandbrakeGrip : Grip;
        float newRight = rightVel * MathF.Exp(-grip * dt);
        chassis.AddForce(sideAxis * ((newRight - rightVel) * chassis.Mass / dt));
    }

    float UpdateGearbox(float forwardSpeed, float dt) {
        int gears = Math.Max(1, GearCount);
        gear = Math.Clamp(gear, 1, gears);

        float gearTop = MaxSpeed * gear / gears;
        float gearBottom = MaxSpeed * (gear - 1) / gears;
        float span = MathF.Max(0.1f, gearTop - gearBottom);
        float revs = MathHelper.Clamp((forwardSpeed - gearBottom) / span, 0f, 1.2f);

        if (shiftTimer > 0f)
            shiftTimer = MathF.Max(0f, shiftTimer - dt);

        if (shiftTimer <= 0f) {
            if (revs >= UpshiftRpm && gear < gears) { gear++; shiftTimer = ShiftTime; }
            else if (revs <= DownshiftRpm && gear > 1) { gear--; shiftTimer = ShiftTime; }
        }

        gearTop = MaxSpeed * gear / gears;
        gearBottom = MaxSpeed * (gear - 1) / gears;
        span = MathF.Max(0.1f, gearTop - gearBottom);
        float targetRevs = MathHelper.Clamp((forwardSpeed - gearBottom) / span, 0f, 1f);
        engineRpm += (targetRevs - engineRpm) * (1f - MathF.Exp(-12f * dt));
        CurrentGear = gear;

        float hump = MathF.Pow(MathF.Sin(MathHelper.Clamp(engineRpm, 0f, 1f) * MathF.PI), 1f / TorqueCurve);
        float torque = 0.35f + 0.65f * hump;
        float shiftCut = shiftTimer > 0f ? 0.1f : 1f;
        return EnginePower * torque * shiftCut;
    }

    void UpdateGearboxIdle(float forwardSpeed, float dt) {
        int gears = Math.Max(1, GearCount);
        gear = Math.Clamp(gear, 1, gears);
        float gearBottom = MaxSpeed * (gear - 1) / gears;
        if (forwardSpeed < gearBottom && gear > 1)
            gear--;
        float gearTop = MaxSpeed * gear / gears;
        float span = MathF.Max(0.1f, gearTop - MaxSpeed * (gear - 1) / gears);
        float targetRevs = forwardSpeed > 0.2f
            ? MathHelper.Clamp((forwardSpeed - MaxSpeed * (gear - 1) / gears) / span, 0f, 1f) : 0f;
        engineRpm += (targetRevs - engineRpm) * (1f - MathF.Exp(-6f * dt));
        if (shiftTimer > 0f) shiftTimer = MathF.Max(0f, shiftTimer - dt);
        CurrentGear = forwardSpeed > 0.2f ? gear : 0;
    }

    void ApplyStability(Transform chassisT, float speedFraction) {
        Vector3 worldUp = Vector3.UnitY;
        Vector3 com = chassisT.WorldPosition;
        Vector3 right = chassisT.Right;
        Vector3 fwd = chassisT.Forward;

        Vector3 rightFlat = new Vector3(right.X, 0f, right.Z);
        if (rightFlat.LengthSquared() > 1e-6f) {
            rightFlat = rightFlat.Normalized();
            float rollLean = Vector3.Dot(right, worldUp);
            float rollRate = Vector3.Dot(chassis.AngularVelocity, fwd);
            float rollForce = (-rollLean * AntiRoll) - (rollRate * AntiRoll * 0.1f);
            chassis.AddForceAtPosition(worldUp * rollForce, com + rightFlat * 1f);
            chassis.AddForceAtPosition(worldUp * -rollForce, com - rightFlat * 1f);
        }

        float df = Downforce * speedFraction * speedFraction;
        if (df > 0f)
            chassis.AddForce(-worldUp * df);
    }

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

    public override void OnDrawGizmosSelected(IGizmos gizmos) {
        Transform t = transform;
        Vector3 origin = t.WorldPosition + Vector3.UnitY * 0.4f;
        Vector3 fwd = t.Forward;
        var flatFwd = new Vector3(fwd.X, 0f, fwd.Z);
        flatFwd = flatFwd.LengthSquared() > 1e-6f ? flatFwd.Normalized() : Vector3.UnitZ;

        gizmos.Color = new Vector3(0.3f, 0.6f, 1f);
        DrawArrow(gizmos, origin, flatFwd, 3f);

        if (!SceneManager.IsPlaying || chassis is null)
            return;

        Vector3 vel = chassis.Velocity;
        var horiz = new Vector3(vel.X, 0f, vel.Z);
        if (horiz.Length() > 0.5f) {
            gizmos.Color = new Vector3(0.3f, 1f, 0.4f);
            DrawArrow(gizmos, origin, horiz.Normalized(), MathF.Min(horiz.Length() * 0.15f, 6f));
        }

        float steerN = CurrentSteerNormalized;
        if (MathF.Abs(steerN) > 0.02f) {
            Vector3 right = t.Right;
            var flatRight = new Vector3(right.X, 0f, right.Z);
            if (flatRight.LengthSquared() > 1e-6f) {
                gizmos.Color = new Vector3(1f, 0.85f, 0.2f);
                DrawArrow(gizmos, origin + flatFwd * 2f, flatRight.Normalized() * MathF.Sign(steerN),
                    MathF.Abs(steerN) * 1.5f);
            }
        }
    }

    static void DrawArrow(IGizmos gizmos, Vector3 origin, Vector3 dir, float length) {
        if (dir.LengthSquared() < 1e-6f || length < 0.05f)
            return;
        dir = dir.Normalized();
        Vector3 tip = origin + dir * length;
        gizmos.DrawLine(origin, tip);
        Vector3 side = Vector3.Cross(dir, Vector3.UnitY);
        if (side.LengthSquared() < 1e-6f) side = Vector3.UnitX;
        side = side.Normalized();
        float b = MathF.Min(0.4f, length * 0.25f);
        gizmos.DrawLine(tip, tip - dir * b + side * b * 0.6f);
        gizmos.DrawLine(tip, tip - dir * b - side * b * 0.6f);
    }
}
