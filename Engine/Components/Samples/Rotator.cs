using OpenTK.Windowing.GraphicsLibraryFramework;

namespace BallisticEngine;

// Sample behaviour: spins its entity about Z. With Alpha enabled, K/J adjust the speed at play time.
public class Rotator : Behaviour {
    public float RotationSpeed { get; set; } = 45.0f; // degrees per second
    public bool Alpha { get; set; }

    protected internal override void Tick(in float delta) {
        float deltaRotation = RotationSpeed * delta;
        Quaternion zRotation = BQuaternion.FromEulerAngles(0, 0, MathHelper.DegreesToRadians(deltaRotation));
        transform.Rotation *= zRotation;

        if (!Alpha)
            return;

        if (Input.IsKeyDown(Keys.K) && RotationSpeed < 100)
            RotationSpeed += 0.1f;
        else if (Input.IsKeyDown(Keys.J) && RotationSpeed > 0)
            RotationSpeed -= 0.1f;
    }
}
