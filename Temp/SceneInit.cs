using BallisticEngine;
using OpenTK.Mathematics;

public static class SceneInit {
    public static void Init() {
        Scene scene = new Scene();
        Entity cameraEntity = Entity.Instantiate("Camera");
        cameraEntity.AddComponent<HDCamera>();
        cameraEntity.AddComponent<FreeLookCameraController>();

        // Assets come from the project; components only get them assigned.
        // Both renderers below share the same Mesh/Material instances (AssetDatabase caches by GUID).
        Mesh mesh = AssetDatabase.Load<Mesh>("Assets/Default/PH7.fbx");
        Material material = AssetDatabase.Load<Material>("Assets/Default/PH7.mat");

        Entity meshEntity = Entity.Instantiate("Mesh");
        StaticMeshRenderer meshRenderer = meshEntity.AddComponent<StaticMeshRenderer>();
        meshRenderer.SharedMesh = mesh;
        meshRenderer.SharedMaterial = material;

        Rotator rotator = meshEntity.AddComponent<Rotator>();
        rotator.RotationSpeed = Random.Shared.Next(-20, 20);
        rotator.Alpha = true;

        meshEntity.transform.Position = Vector3.Zero;
        meshEntity.transform.EulerAngles = new Vector3(90, 180, 0);
        meshEntity.transform.Scale = Vector3.One * 6;

        Entity lightEntity = Entity.Instantiate("Directional Light");
        lightEntity.AddComponent<DirectionalLight>();
        lightEntity.transform.Position = new Vector3(3, 0.5f, 0);

        StaticMeshRenderer lightMarker = lightEntity.AddComponent<StaticMeshRenderer>();
        lightMarker.SharedMesh = mesh;
        lightMarker.SharedMaterial = material;
    }
}
