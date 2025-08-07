using OpenTK.Mathematics;
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
}