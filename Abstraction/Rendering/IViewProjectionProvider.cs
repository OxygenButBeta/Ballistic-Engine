using OpenTK.Mathematics;

namespace BallisticEngine;

/// <summary>
/// This interface provides methods to retrieve the view and projection matrices for rendering.
/// </summary>
public interface IViewProjectionProvider {
    Matrix4 GetProjectionMatrix();
    Matrix4 GetViewMatrix();
}