using OpenTK.Windowing.GraphicsLibraryFramework;

namespace BallisticEngine.Core.GL;

public class GLInput : IInputProvider {
    MouseState mouseState;
    KeyboardState keyboardState;

    readonly System.Func<System.Collections.Generic.IReadOnlyList<JoystickState>> joysticks;

    public GLInput(KeyboardState keyboardState, MouseState mouseState,
        System.Func<System.Collections.Generic.IReadOnlyList<JoystickState>> joysticks = null) {
        this.keyboardState = keyboardState;
        this.mouseState = mouseState;
        this.joysticks = joysticks;
    }

    public bool IsKeyDown(Keys key) {
        return keyboardState.IsKeyDown(key);
    }

    public bool IsKeyPressed(Keys key) {
        return keyboardState.IsKeyPressed(key);
    }

    public bool IsMouseButtonPressed(MouseButton button) {
        return mouseState.IsButtonPressed(button);
    }

    public bool IsMouseButtonDown(MouseButton button) {
        return mouseState.IsButtonDown(button);
    }

    public Vector2 ScrollDelta => new Vector2(mouseState.ScrollDelta.X, mouseState.ScrollDelta.Y);
    public Vector2 MousePosition => new Vector2(mouseState.Position.X, mouseState.Position.Y);
    public Vector2 MouseDelta => new Vector2(mouseState.Delta.X, mouseState.Delta.Y);

    JoystickState Pad(int playerIndex) {
        var list = joysticks?.Invoke();
        if (list is null || (uint)playerIndex >= (uint)list.Count)
            return null;
        return list[playerIndex];
    }

    public bool IsGamepadConnected(int playerIndex) => Pad(playerIndex) is not null;

    public bool IsGamepadButtonDown(int playerIndex, int button) {
        JoystickState pad = Pad(playerIndex);
        return pad is not null && (uint)button < (uint)pad.ButtonCount && pad.IsButtonDown(button);
    }

    public bool IsGamepadButtonPressed(int playerIndex, int button) {
        JoystickState pad = Pad(playerIndex);
        return pad is not null && (uint)button < (uint)pad.ButtonCount && pad.IsButtonPressed(button);
    }

    public float GetGamepadAxis(int playerIndex, int axis) {
        JoystickState pad = Pad(playerIndex);
        return pad is not null && (uint)axis < (uint)pad.AxisCount ? pad.GetAxis(axis) : 0f;
    }
}
