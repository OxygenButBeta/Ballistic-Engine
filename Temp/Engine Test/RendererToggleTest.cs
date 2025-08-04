using BallisticEngine;
using OpenTK.Windowing.GraphicsLibraryFramework;

public class RendererToggleTest : Behaviour {
    Renderer renderer;

    protected internal override void OnBegin() {
        Entity entity = Entity.Instantiate("Mesh");
        entity.AddComponent<StaticMeshRenderer>();
        renderer = entity.GetComponent<StaticMeshRenderer>();
    }

    protected internal override void Tick(in float delta) {
        base.Tick(delta);
        if (Input.IsKeyPressed(Keys.K)) {
            renderer.IsEnabled = !renderer.IsEnabled;
        }
    }
}