using BallisticEngine;
using OpenTK.Mathematics;

public static class SceneInit {
    public static void Init() {
        Scene scene = new Scene();
        Entity cameraEntity = Entity.Instantiate("Camera");
        cameraEntity.AddComponent<HDCamera>();
        cameraEntity.AddComponent<FreeLookCameraController>();

        int columns = 100; // her satırda 10 kutu<
        float spacing = 4f;

        for (int i = 0; i < 5; i++) {
            Entity meshEntity = Entity.Instantiate("Mesh");
            meshEntity.AddComponent<StaticMeshRenderer>();
            meshEntity.AddComponent<Rotator>();
            meshEntity.GetComponent<Rotator>().RotationSpeed = Random.Shared.Next(-90, 90);

            int x = i % columns; 
            int z = i / columns; 

            meshEntity.transform.Position = new Vector3(x * spacing, 0, z * spacing);
            meshEntity.transform.EulerAngles = new Vector3(90, 180, 0);
            meshEntity.transform.Scale = Vector3.One * 6;
        }
    }
}