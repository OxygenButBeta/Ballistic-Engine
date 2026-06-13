using OpenTK.Mathematics;

namespace BallisticEngine;

// Light Shafts (god-rays). A STANDALONE post-process whose only job is to make light beams
// visible — the bright/dark banding the eye reads as "god-rays". It reuses the volumetric
// march machinery (half-res, shadow-map-gated, temporally denoised) but contributes a purely
// ADDITIVE in-scatter, so it never alters the physically-balanced VolumetricFog: turn fog and
// shafts on together and the shafts ride on top of accurate fog; turn fog off and the shaft
// pass supplies its own thin medium so beams still show.
//
// What it does that plain fog can't: physical fog keeps the off-sun air lit (skylight), which
// by design softens the sun's shadow contrast — so a physically-correct fog looks hazy, not
// ray-streaked. This component re-injects that contrast (lit air glows, shadowed air stays
// dark) and, additionally, in-scatters every point/spot light along the ray so a torch glows
// as a halo and a spotlight paints a visible cone of light. Off by default.
public sealed class LightShafts : VolumeComponent {
    public readonly BoolParameter enabled = new(false);

    [Tooltip("Master strength of the composited shafts.")]
    public readonly ClampedFloatParameter intensity = new(1f, 0f, 4f);

    [Header("Sun")]
    [Tooltip("Sun god-rays: shadow-gated in-scatter toward the directional light. 0 = no sun shafts; 1 = natural; higher = stylised.")]
    public readonly ClampedFloatParameter sunShafts = new(1f, 0f, 8f);

    [Header("Punctual (point / spot)")]
    [Tooltip("Volumetric lighting from point & spot lights: in-scatters each light along the ray so its cone/halo glows. 0 = off.")]
    public readonly ClampedFloatParameter punctualShafts = new(1f, 0f, 8f);

    [Tooltip("Carve punctual shafts with the light's shadow map (true = real beams through gaps; false = cheaper, fills the whole cone).")]
    public readonly BoolParameter punctualShadows = new(true);

    [Header("Medium")]
    [Tooltip("Density of the shaft medium, 1/m. Used when VolumetricFog is OFF (when fog is on, the shafts march through the fog's medium instead). More = thicker, brighter beams.")]
    public readonly ClampedFloatParameter density = new(0.02f, 0f, 0.5f);
}
