
using BallisticEngine;
using OpenTK.Mathematics;

public class Rotator : Behaviour{
    public float RotationSpeed { get; set; } = 45.0f; // degrees per second
    
    protected internal override void Tick(in float delta) {
        float deltaRotation = RotationSpeed * delta;

        Quaternion yRotation = Quaternion.FromEulerAngles(0, MathHelper.DegreesToRadians(deltaRotation), 0);
        Quaternion xRotation = Quaternion.FromEulerAngles(MathHelper.DegreesToRadians(deltaRotation), 0, 0);

        transform.Rotation *= yRotation * xRotation;
    }
}
