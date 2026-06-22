
namespace BallisticEngine.Editor;

internal sealed class SceneCameraView : IViewProjectionProvider {
    HDCamera camera;
    float aspect = 16f / 9f;

    const float NearPlane = 0.1f;
    const float FarPlane = 1000f;
    const float FovYDegrees = 45f;

    public void Bind(HDCamera sceneCamera, float panelAspect) {
        camera = sceneCamera;
        if (panelAspect > 0)
            aspect = panelAspect;
    }

    public Transform Transform => camera.Transform;
    public Vector3 AmbientColor => camera.AmbientColor;

    public Matrix4 GetViewMatrix() => camera.GetViewMatrix();

    public Matrix4 GetProjectionMatrix() =>
        BMatrix.CreatePerspectiveFieldOfView(MathHelper.DegreesToRadians(FovYDegrees), aspect, NearPlane, FarPlane);
}
