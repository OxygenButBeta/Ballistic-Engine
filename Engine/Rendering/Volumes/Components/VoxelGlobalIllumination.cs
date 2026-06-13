namespace BallisticEngine;

// Voxel Cone Tracing global illumination (UE5/Lumen-class colored multi-bounce indirect). Voxelizes
// the whole-mesh scene's direct-lit radiance into a 3D grid and cone-traces it in the forward pass.
// Default-on; tune live in the editor. Only engages on scenes with a GPU-driven whole-mesh renderer
// (the imported Bistro/Sun Temple kind); otherwise it safely contributes nothing.
public sealed class VoxelGlobalIllumination : VolumeComponent {
    [Tooltip("Enable voxel cone tracing GI (colored multi-bounce indirect light).")]
    public readonly BoolParameter enabled = new(true);

    [Tooltip("Strength of the cone-traced indirect diffuse + glossy added over the IBL ambient. " +
             "2.0 = the tuned default; raise to make bounce obvious, lower for subtle.")]
    public readonly ClampedFloatParameter intensity = new(2.0f, 0f, 6f);

    [Tooltip("How much the cone-traced bounce REPLACES the flat sky ambient in enclosed areas " +
             "(the Lumen look). 0 = purely additive (older conservative look); 0.6 = default; 1 = the " +
             "sky ambient fully fades out where the hemisphere is closed, so the colored local bounce " +
             "takes over recesses. Open, sky-exposed surfaces are unaffected at any value.")]
    public readonly ClampedFloatParameter skyReplace = new(0.6f, 0f, 1f);

    [Tooltip("Extra GI bounce passes after the direct pass. Each compounds another light bounce, " +
             "filling deeper interiors. 0 = single bounce, 2 = default, more = richer but costlier.")]
    public readonly ClampedIntParameter bounces = new(2, 0, 4);

    [Tooltip("Voxel grid resolution (cubic). Higher = crisper, less-blocky GI for a small extra cost. " +
             "128 fast, 192 default, 256 sharp.")]
    public readonly ClampedIntParameter resolution = new(192, 64, 256);

    [Tooltip("World size (metres) of the camera-centred voxel grid. Smaller = finer voxels (better " +
             "detail) but less reach; larger = more reach, coarser. 60 m default.")]
    public readonly ClampedFloatParameter volumeSize = new(60f, 10f, 200f);

    [Tooltip("DEBUG: show ONLY the GI bounce contribution (brightened), so you can see where light " +
             "lands, leaks, or is missing. Black = the GI gathered nothing there.")]
    public readonly BoolParameter debugView = new(false);
}
