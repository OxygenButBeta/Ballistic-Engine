using OpenTK.Windowing.GraphicsLibraryFramework;

namespace BallisticEngine;

/// <summary>
/// This interface defines the methods for input handling in the engine.
/// By default, it uses OpenTK Windowing.GraphicsLibraryFramework for binding.
/// Every input provider such as OpenGL, DirectX, or Vulkan should implement this interface and must Provide a way to convert OpenTK input to their own input system.
/// </summary>
public interface IInputProvider
{
    bool IsKeyDown(Keys key);
    bool IsKeyPressed(Keys key);
    bool IsMouseButtonPressed(MouseButton button);
    bool IsMouseButtonDown(MouseButton button);
    Vector2 ScrollDelta { get; }
    Vector2 MousePosition { get; }

    // Per-frame mouse movement in pixels. Unlike (MousePosition - lastMousePosition) tracking, this
    // keeps working while the cursor is GRABBED (locked to the window centre), which is exactly when
    // first-person look needs it.
    Vector2 MouseDelta { get; }

    // ---- Gamepad (raw, by 0-based player index + raw button/axis index) ----
    // The facade maps Xbox-style enums onto these raw indices. All return safe defaults (false / 0 /
    // not-connected) when no controller is plugged into that slot, so game code never special-cases it.
    bool IsGamepadConnected(int playerIndex);
    bool IsGamepadButtonDown(int playerIndex, int button);
    bool IsGamepadButtonPressed(int playerIndex, int button);
    float GetGamepadAxis(int playerIndex, int axis);
}