using OpenTK.Windowing.GraphicsLibraryFramework; // Keys enum, same source the Input facade uses

namespace BallisticEngine;

// Arcade vehicle controller (P7 demo capstone): drives a chassis Rigidbody through its WheelColliders.
// Reads input (throttle/brake/steer), distributes motor force to the driven wheels, steers the front
// wheels, and applies the handbrake. Sits on the chassis entity; the wheels are WheelCollider
// components on child entities. Built entirely on the new physics surface — force-at-position (P2),
// sphere-cast suspension (P2), restitution-stable contacts (P1).
[Component("Vehicle Controller", "Physics")]
public class VehicleController : Behaviour {
    [Header("Drive")]
    [Tooltip("Forward force per driven wheel at full throttle (newtons).")]
    [Range(0f, 50000f)]
    public float MotorForce { get; set; } = 8000f;

    [Tooltip("Braking force per wheel (newtons).")]
    [Range(0f, 50000f)]
    public float BrakeForce { get; set; } = 12000f;

    [Tooltip("Top speed (m/s); motor force tapers to zero as the car approaches it.")]
    [Range(1f, 120f)]
    public float MaxSpeed { get; set; } = 30f;

    [Header("Steering")]
    [Tooltip("Maximum steer angle of the front wheels at low speed (degrees).")]
    [Range(0f, 60f)]
    public float MaxSteerAngle { get; set; } = 30f;

    [Tooltip("Steer angle shrinks toward this fraction at MaxSpeed (high-speed stability).")]
    [Range(0.1f, 1f)]
    public float HighSpeedSteerScale { get; set; } = 0.4f;

    [Header("Layout")]
    [Tooltip("Wheels in front of the chassis centre steer. Behind it (or all, configurable) drive.")]
    public bool FrontWheelDrive { get; set; }
    public bool RearWheelDrive { get; set; } = true;

    Rigidbody chassis;
    readonly List<WheelCollider> wheels = new();

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
        if (!SceneManager.IsPlaying || chassis is null)
            return;
        if (wheels.Count == 0)
            GatherWheels();

        float throttle = ReadAxis(Keys.W, Keys.S) + Input.GetLeftStick().Y;
        throttle = MathHelper.Clamp(throttle, -1f, 1f);
        float steerInput = ReadAxis(Keys.D, Keys.A) + Input.GetLeftStick().X;
        steerInput = MathHelper.Clamp(steerInput, -1f, 1f);
        bool handbrake = Input.IsKeyDown(Keys.Space);

        float speed = chassis.Velocity.Length();
        float speedFraction = MathHelper.Clamp(speed / MaxSpeed, 0f, 1f);

        // Steering shrinks with speed for stability.
        float steerScale = MathHelper.Lerp(1f, HighSpeedSteerScale, speedFraction);
        float steerAngle = steerInput * MaxSteerAngle * MathHelper.DegreesToRadians(1f) * steerScale;

        // Motor tapers toward zero near top speed (so the car doesn't accelerate forever).
        float motor = throttle * MotorForce * (1f - speedFraction);
        bool braking = handbrake ||
            (MathF.Abs(throttle) > 0.01f && Vector3.Dot(chassis.Velocity, chassis.transform.Forward) * throttle < -0.5f);

        // Identify front/rear by each wheel's position relative to the chassis along its forward axis.
        Vector3 chassisPos = chassis.transform.WorldPosition;
        Vector3 forward = chassis.transform.Forward;
        foreach (WheelCollider wheel in wheels) {
            float along = Vector3.Dot(wheel.transform.WorldPosition - chassisPos, forward);
            bool isFront = along >= 0f;

            wheel.SteerAngle = isFront ? steerAngle : 0f;
            bool driven = (isFront && FrontWheelDrive) || (!isFront && RearWheelDrive);
            wheel.MotorForce = driven ? motor : 0f;
            wheel.BrakeForce = braking ? BrakeForce : 0f;
        }
    }

    static float ReadAxis(Keys positive, Keys negative) =>
        (Input.IsKeyDown(positive) ? 1f : 0f) - (Input.IsKeyDown(negative) ? 1f : 0f);
}
