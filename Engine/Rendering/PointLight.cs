using OpenTK.Mathematics;

namespace BallisticEngine;

// Local point light, forward-shaded. Registers on attach so the editor viewport is lit
// without entering play mode. The renderer uploads the first MaxPointLights active lights.
public class PointLight : Behaviour {
    public Vector3 Color { get; set; } = Vector3.One;
    public float Intensity { get; set; } = 10f;
    public float Range { get; set; } = 10f;

    protected internal override void OnAttach() {
        if (!RuntimeSet<PointLight>.Contains(this))
            RuntimeSet<PointLight>.Add(this);
    }

    protected internal override void OnDetach() {
        RuntimeSet<PointLight>.Remove(this);
    }
}
