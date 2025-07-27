using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace BallisticEngine.Rendering;

internal class Camera(float width, float height, Vector3 position) {
    float movingSpeed = 8f;
    readonly float sensitivity = 180f;

    Vector3 position = position;
    Vector3 up = Vector3.UnitY;
    Vector3 front = -Vector3.UnitZ;
    Vector3 right = Vector3.UnitX;

    float pitch;
    float maxOnX = -90.0f;

    bool firstMove = true;
    Vector2 lastPos;
    MouseState mouse;

    public Matrix4 GetViewMatrix() {
        return Matrix4.LookAt(position, position + front, up);
    }

    public Matrix4 GetProjectionMatrix() {
        return Matrix4.CreatePerspectiveFieldOfView(MathHelper.DegreesToRadians(45.0f), width / height,
            0.1f, 100.0f);
    }


    void UpdateVectors() {
        if (pitch > 89.0f)
            pitch = 89.0f;

        if (pitch < -89.0f)
            pitch = -89.0f;


        front.X = MathF.Cos(MathHelper.DegreesToRadians(pitch)) *
                  MathF.Cos(MathHelper.DegreesToRadians(maxOnX));
        front.Y = MathF.Sin(MathHelper.DegreesToRadians(pitch));
        front.Z = MathF.Cos(MathHelper.DegreesToRadians(pitch)) *
                  MathF.Sin(MathHelper.DegreesToRadians(maxOnX));

        front = Vector3.Normalize(front);

        right = Vector3.Normalize(Vector3.Cross(front, Vector3.UnitY));
        up = Vector3.Normalize(Vector3.Cross(right, front));
    }

    void InputController(KeyboardState input, MouseState mouse, FrameEventArgs e, GLGameWindow gl) {
        if (mouse.ScrollDelta.Y != 0) {
            this.movingSpeed += mouse.ScrollDelta.Y;
            if (this.movingSpeed < 0.1f)
                this.movingSpeed = 0.1f;
        }

        float _movingSpeed = this.movingSpeed;
        if (input.IsKeyDown(Keys.LeftShift))
            _movingSpeed *= 5;

        if (input.IsKeyDown(Keys.W))
            position += front * _movingSpeed * (float)e.Time;

        if (input.IsKeyDown(Keys.A))
            position -= right * _movingSpeed * (float)e.Time;

        if (input.IsKeyDown(Keys.S))
            position -= front * _movingSpeed * (float)e.Time;

        if (input.IsKeyDown(Keys.D))
            position += right * _movingSpeed * (float)e.Time;

        if (input.IsKeyDown(Keys.Space))
            position.Y += _movingSpeed * (float)e.Time;


        if (input.IsKeyDown(Keys.LeftControl))
            position.Y -= _movingSpeed * (float)e.Time;

        if (firstMove) {
            lastPos = new Vector2(mouse.X, mouse.Y);
            firstMove = false;
        }
        else {
            if (!mouse.IsButtonDown(MouseButton.Button2)) {
                gl.CursorState = CursorState.Normal;
                return;
            }

            gl.CursorState = CursorState.Grabbed;

            var deltaX = mouse.X - lastPos.X;
            var deltaY = mouse.Y - lastPos.Y;
            lastPos = new Vector2(mouse.X, mouse.Y);

            maxOnX += deltaX * sensitivity * (float)e.Time;
            pitch -= deltaY * sensitivity * (float)e.Time;
        }

        UpdateVectors();
    }

    public void Update(FrameEventArgs e, GLGameWindow gl) {
        InputController(gl.KeyboardState, gl.MouseState, e, gl);
        mouse = gl.MouseState;
    }


    public void FrameUpdate() {
        if (!mouse.IsButtonDown(MouseButton.Button2))
            return;
    }
}