using OpenTK.Mathematics;

namespace BallisticEngine;

public interface IViewProjectionProvider {
    Matrix4 GetProjectionMatrix();
    Matrix4 GetViewMatrix();
}