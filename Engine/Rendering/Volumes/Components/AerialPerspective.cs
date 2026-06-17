namespace BallisticEngine;

// Aerial perspective: the atmospheric scattering haze that distant opaque geometry picks up as it
// recedes toward the horizon — the #1 cue that sells scale (far buildings/mountains desaturate and
// shift toward the sky colour). The DX12 backend bakes a Hillaire-2020 froxel volume (a small 3D LUT
// of accumulated single-scatter inscatter + transmittance from the camera) using the SAME Rayleigh/
// Mie/ozone atmosphere the ProceduralSky shows, so geometry fades into exactly the colour of the sky
// behind it — never a flat blue-white veil. ON by default at a calibrated strength; only applies while
// a ProceduralSky drives the atmosphere.
public sealed class AerialPerspective : VolumeComponent {
    [Tooltip("Master toggle. When off the scene gets no atmospheric distance haze.")]
    public readonly BoolParameter enabled = new(true);

    [Tooltip("Master strength. 1 = physically calibrated against the sky; below 1 thins the haze, above 1 pushes it for a hazier/foggier mood.")]
    public readonly ClampedFloatParameter intensity = new(1f, 0f, 4f);

    [Tooltip("Distance (m) at which the haze starts to build. Geometry closer than this is left untouched, so foreground and interiors stay crisp.")]
    public readonly ClampedFloatParameter startDistance = new(30f, 0f, 2000f);

    [Tooltip("Far distance (m) the froxel volume covers. Beyond it the haze holds at the volume's last slice. Larger = the haze builds more gradually over a deeper vista; the haze reaches ~half strength around 40% of this.")]
    public readonly ClampedFloatParameter maxDistance = new(2000f, 100f, 60000f);

    [Tooltip("How quickly the haze deepens with distance (the atmosphere's apparent density for the in-scene march). 1 = physical; higher = thicker/closer haze, lower = clearer air.")]
    public readonly ClampedFloatParameter densityScale = new(1f, 0.1f, 8f);

    [Tooltip("Colour grade on the in-scattered haze (the extinction that dims the distance stays neutral). Leave white for the physical sky-matched tint.")]
    public readonly ColorParameter tint = new(Vector3.One);
}
