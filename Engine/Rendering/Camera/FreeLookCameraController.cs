using OpenTK.Mathematics;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace BallisticEngine;

public class FreeLookCameraController : Behaviour {
    IWindow window;
    float moveSpeed = 5f;
    float mouseSensitivity = .2f;
    bool isRightMouseDown = false;
    Vector2 lastMousePosition;
    protected internal override void OnBegin()
    {
        window = Window.Current;
        lastMousePosition = Input.MousePosition;
    }

    protected internal override void Tick(in float deltaTime) {
        HandleMouse(deltaTime);
        HandleMovement(deltaTime);
    }

    float pitch; // -90/+90 arası
    float yaw;   // -sonsuz/+sonsuz arası

    private void HandleMouse(float deltaTime) {
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

    private void HandleMovement(float deltaTime) {
       
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
        if (Input.IsKeyDown(Keys.LeftShift))
            direction -= transform.Up;

        if (direction != Vector3.Zero)
            transform.Position += direction.Normalized() * moveSpeed * deltaTime;
    }
}