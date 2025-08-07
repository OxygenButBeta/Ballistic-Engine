using BallisticEngine;
using OpenTK.Mathematics;

public static class SceneInit {
    public static void Init() {
        Scene scene = new Scene();
        Entity cameraEntity = Entity.Instantiate("Camera");
        cameraEntity.AddComponent<HDCamera>();
        cameraEntity.AddComponent<FreeLookCameraController>();

        int columns = 10; // her satırda 10 kutu
        float spacing = 4f;

        for (int i = 0; i < 500; i++)
        {
            Entity meshEntity = Entity.Instantiate("Mesh");
            meshEntity.AddComponent<StaticMeshRenderer>();
            meshEntity.AddComponent<Rotator>();
            meshEntity.GetComponent<Rotator>().RotationSpeed = Random.Shared.Next(-90, 90);

            int x = i % columns;         // x konumu: satır içindeki pozisyon
            int z = i / columns;         // z konumu: satır sayısı kadar yukarı çık

            meshEntity.transform.Position = new Vector3(x * spacing, 0, z * spacing);
        }


  
    }
}