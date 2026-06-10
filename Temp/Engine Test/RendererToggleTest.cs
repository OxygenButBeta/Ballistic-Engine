using BallisticEngine;
using OpenTK.Windowing.GraphicsLibraryFramework;

public class RendererToggleTest : Behaviour {
    Renderer renderer;

    protected internal override void OnBegin() {
        Entity entity = Entity.Instantiate("Mesh");
        renderer = entity.AddComponent<StaticMeshRenderer>();
        renderer.SharedMesh = AssetDatabase.Load<Mesh>("Assets/Default/PH7.fbx");
        renderer.SharedMaterial = AssetDatabase.Load<Material>("Assets/Default/PH7.mat");
    }

    protected internal override void Tick(in float delta) {
        base.Tick(delta);
        if (Input.IsKeyPressed(Keys.K)) {
            renderer.IsEnabled = !renderer.IsEnabled;
        }
    }
}
