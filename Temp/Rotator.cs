
using BallisticEngine;
using OpenTK.Mathematics;
using OpenTK.Windowing.GraphicsLibraryFramework;

public class Rotator : Behaviour{
    public float RotationSpeed { get; set; } = 45.0f; // degrees per second
    static int speedMP = 1; 
    public bool Alpha { get; set; }

    protected internal override void Tick(in float delta)
    {
        float deltaRotation = RotationSpeed * delta * speedMP;

        //Quaternion yRotation = Quaternion.FromEulerAngles(0, MathHelper.DegreesToRadians(deltaRotation), 0);
        Quaternion xRotation = Quaternion.FromEulerAngles(0, 0, MathHelper.DegreesToRadians(deltaRotation));

        transform.Rotation *= xRotation;
        if (Alpha)
        {
            if (Input.IsKeyDown(Keys.K))
            {
                if (RotationSpeed < 100)
                    RotationSpeed += 0.1f;
            }
            else if (Input.IsKeyDown(Keys.J))
            {
                if (RotationSpeed > 0)
                    RotationSpeed -= 0.1f;
            }
            
        }
    }
}
