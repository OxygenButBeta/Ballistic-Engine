using OpenTK.Mathematics;

namespace BallisticEngine;

// Spot light pointing along the entity's forward axis, forward-shaded. Registers on
// attach so the editor viewport is lit without entering play mode.
public class SpotLight : Behaviour {
    public Vector3 Color { get; set; } = Vector3.One;
    public float Intensity { get; set; } = 20f;
    public float Range { get; set; } = 15f;

    // Cone angles in degrees; full brightness inside Inner, fades to zero at Outer.
    public float InnerAngle { get; set; } = 25f;
    public float OuterAngle { get; set; } = 35f;

    protected internal override void OnAttach() {
        if (!RuntimeSet<SpotLight>.Contains(this))
            RuntimeSet<SpotLight>.Add(this);
    }

    protected internal override void OnDetach() {
        RuntimeSet<SpotLight>.Remove(this);
    }
}
