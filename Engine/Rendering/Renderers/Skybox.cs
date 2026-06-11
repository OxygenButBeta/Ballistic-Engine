namespace BallisticEngine;

// Scene-wide skybox: lives in the scene's SceneBehaviour list (the editor's "Scene" hierarchy),
// not on an entity. Assign a cubemap asset (or an equirect .hdr/.exr); the renderer draws the
// active skybox's cubemap. No skybox (or no cubemap) = no sky, default ambient lighting.
public class Skybox : SceneBehaviour {
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
