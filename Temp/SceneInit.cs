using BallisticEngine;
using OpenTK.Mathematics;

public static class SceneInit {
    public static void Init() {
        Scene scene = new Scene();
        Entity cameraEntity = Entity.Instantiate("Camera");
        cameraEntity.AddComponent<HDCamera>();
        cameraEntity.AddComponent<FreeLookCameraController>();

        Entity rendererEntity = Entity.Instantiate("RendererToggleTest");
        rendererEntity.AddComponent<RendererToggleTest>();

        Entity meshEntity = Entity.Instantiate("Mesh");
        meshEntity.AddComponent<StaticMeshRenderer>();
        meshEntity.AddComponent<Rotator>();
        meshEntity.transform.Position = new Vector3(0, 0, 6);
    }
}