using OpenTK.Windowing.GraphicsLibraryFramework;

namespace BallisticEngine;

public class Rotator : Behaviour {
    public float RotationSpeed { get; set; } = 45.0f;
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
