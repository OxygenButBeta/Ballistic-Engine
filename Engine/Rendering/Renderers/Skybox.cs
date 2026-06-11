namespace BallisticEngine;

// Scene-wide skybox: lives in the scene's SceneBehaviour list (the editor's "Scene" hierarchy),
// not on an entity. Assign a cubemap asset (or an equirect .hdr/.exr); the renderer draws the
// active skybox's cubemap. No skybox (or no cubemap) = no sky, default ambient lighting.
public class Skybox : SceneBehaviour {
    public static Skybox Active { get; private set; }

    public Texture3D Cubemap { get; set; }

    // Sky luminance scale (also scales the sky's IBL ambient contribution). With the physical
    // pipeline, lights live in real magnitudes (sun ~80000 lux) but an HDRI is authored in
    // RELATIVE luminance (peak often ~1-100), so this multiplier brings the sky up to the same
    // physical scale - otherwise the IBL ambient is ~thousandfold too dim against the sun and
    // shadows crush. ~5000 is a sane daylight-HDRI starting point; tune per environment map.
    public float Exposure { get; set; } = 5000f;

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
