namespace BallisticEngine;

// Scene-driven skybox: add this component to an entity and assign a cubemap asset (.cubemap).
// The renderer draws the active skybox's cubemap; no skybox component (or no cubemap) means
// no sky is drawn and default ambient lighting is used. Replaces the old project.json default.
public class Skybox : Behaviour {
    public static Skybox Active { get; private set; }

    public Texture3D Cubemap { get; set; }

    // Sky brightness multiplier (also scales the sky's ambient contribution).
    public float Exposure { get; set; } = 1f;

    // Sky orientation in degrees (Y = spin the horizon).
    public OpenTK.Mathematics.Vector3 RotationEuler { get; set; }

    protected internal override void OnAttach() {
        Active = this;
    }

    protected internal override void OnDetach() {
        if (ReferenceEquals(Active, this))
            Active = null;
    }
}
