using BallisticEngine.Serialization;
using OpenTK.Mathematics;

namespace BallisticEngine;

// One-off authoring of SampleProject's Main.scene (recreates the old SceneInit setup),
// invoked via `--author-scene`. Builds entities in edit mode and serializes them, then exits.
// Kept in the Runtime so the player exe can regenerate the sample scene without the editor.
internal static class SceneAuthoring {
    public static void AuthorMainScene(string projectPath) {
        GLBallisticEngineWindow runtime = new(1280, 720);
        EngineBootstrap bootstrap = new(runtime, projectPath);

        Scene scene = SceneManager.GetCurrentScene();
        scene.Name = "Main";

        Mesh mesh = AssetDatabase.Load<Mesh>("Assets/Default/PH7.fbx");
        Material material = AssetDatabase.Load<Material>("Assets/Default/PH7.mat");

        Entity camera = Entity.Instantiate("Camera");
        camera.AddComponent<HDCamera>();
        camera.AddComponent<FreeLookCameraController>();
        camera.transform.Position = new Vector3(0, 0, -12);

        Entity meshEntity = Entity.Instantiate("Mesh");
        StaticMeshRenderer renderer = meshEntity.AddComponent<StaticMeshRenderer>();
        renderer.SharedMesh = mesh;
        renderer.SharedMaterial = material;
        Rotator rotator = meshEntity.AddComponent<Rotator>();
        rotator.RotationSpeed = 15f;
        rotator.Alpha = true;
        meshEntity.transform.EulerAngles = new Vector3(90, 180, 0);
        meshEntity.transform.Scale = Vector3.One * 6;

        Entity light = Entity.Instantiate("Directional Light");
        light.AddComponent<DirectionalLight>();
        light.transform.Position = new Vector3(3, 0.5f, 0);

        var outputPath = bootstrap.Project.ResolveAbsolute("Assets/Scenes/Main.scene");
        SceneSerializer.Save(scene, outputPath);
        Console.WriteLine($"Authored scene: {outputPath}");

        runtime.Close();
    }
}
