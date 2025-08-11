using OpenTK.Mathematics;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace BallisticEngine;

public class FreeLookCameraController : Behaviour {
    float moveSpeed = 10f;
    const float mouseSensitivity = .2f;
    bool isRightMouseDown;
    Vector2 lastMousePosition;
    
    float pitch;
    float yaw;
    protected internal override void OnBegin()
    {
        lastMousePosition = Input.MousePosition;
    }

    protected internal override void Tick(in float deltaTime) {
        HandleMouse();
        HandleMovement(deltaTime);
        switch (Input.ScrollDelta.Y) {
            case > 0:
                moveSpeed += 1f;
                break;
            case < 0:
                moveSpeed = Math.Max(1f, moveSpeed - 1f );
                break;
        }
    }


    void HandleMouse() {
        if (Input.IsMouseButtonDown(MouseButton.Right)) {
            if (!isRightMouseDown) {
                isRightMouseDown = true;
                lastMousePosition = Input.MousePosition;
            }

            Vector2 delta = Input.MousePosition - lastMousePosition;
            lastMousePosition = Input.MousePosition;

            yaw -= delta.X * mouseSensitivity;  
            pitch += delta.Y * mouseSensitivity;

            pitch = MathHelper.Clamp(pitch, -89f, 89f);

            Quaternion qPitch = Quaternion.FromAxisAngle(Vector3.UnitX, MathHelper.DegreesToRadians(pitch));
            Quaternion qYaw = Quaternion.FromAxisAngle(Vector3.UnitY, MathHelper.DegreesToRadians(yaw));

            transform.Rotation = qYaw * qPitch;

        } else {
            isRightMouseDown = false;
        }
    }

    void HandleMovement(float deltaTime) {
       
        float tempSpeed = moveSpeed;
        Vector3 direction = Vector3.Zero;

        if (Input.IsKeyDown(Keys.W))
            direction += transform.Forward;
        if (Input.IsKeyDown(Keys.S))
            direction -= transform.Forward;
        if (Input.IsKeyDown(Keys.D))
            direction -= transform.Right;
        if (Input.IsKeyDown(Keys.A))
            direction += transform.Right;
        if (Input.IsKeyDown(Keys.Space))
            direction += transform.Up;
        if (Input.IsKeyDown(Keys.LeftControl))
            direction -= transform.Up;
        if (Input.IsKeyDown(Keys.LeftShift))
            tempSpeed *= 2f; 
            

        
        if (direction != Vector3.Zero)
            transform.Position += direction.Normalized() * tempSpeed * deltaTime;
    }
}