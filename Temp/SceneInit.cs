using BallisticEngine;

public static class SceneInit
{
    public static void Init()
    {
        Scene scene = new Scene();
        Entity cameraEntity = new Entity("Camera");
        cameraEntity.AddComponent<HDCamera>();
    }
}