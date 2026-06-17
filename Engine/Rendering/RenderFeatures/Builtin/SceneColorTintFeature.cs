namespace BallisticEngine;

// A trivial built-in RenderFeature: tints the scene-color target. It exists in THIS chunk only so
// ComponentRegistry discovery is testable (RenderFeatureMenu has an entry, ResolveFeature/FeatureNameOf
// round-trip its name) — it is NOT registered into any scene, so the golden scenes stay feature-free and
// pixel-neutral. Chunk 20 wires the backend bridge and uses this exact feature as the seam-proof "tint
// SceneColor" test (a feature that visibly changes the frame, then removes byte-identically to golden).
//
// Authoring shape mirror: params are PLAIN decorated members (NO VolumeParameter — features don't blend,
// design §2a), so they render through the existing attribute-driven DrawerPipeline for free and serialize
// like a Behaviour. Event is an authored member (default PostProcess-1 region: a feature can slot just
// after a built-in via the enum's 50-spacing; the backend honors the raw Event value).
[RenderFeature("Scene Color Tint", "Custom")]
public class SceneColorTintFeature : RenderFeature {
    [Tooltip("Multiplied into the scene color. White = no change.")]
    [ColorUsage]
    public Vector3 Tint { get; set; } = Vector3.One;

    [Range(0f, 1f)]
    [Tooltip("How strongly the tint is applied (0 = off, 1 = full).")]
    public float Strength { get; set; } = 1f;

    // Inject just before composite — a post-lighting/sky color grade, the canonical place for a tint.
    public override RenderPassEvent Event => RenderPassEvent.PostProcess;

    public override void Declare(IFeatureIOBuilder io) {
        // Read-modify-write the live scene color in place (a full-screen tint).
        io.ReadWrite("SceneColor");
    }

    public override void Record(IFeaturePassRecorder recorder) {
        // Chunk-20 backend impl drives the actual tint blit; the param values (Tint/Strength) feed the
        // backend's tint constant. This chat ships no backend, so Record is the authored intent only.
        recorder.BlitFullscreen(recorder.SceneColor, recorder.SceneColor, "SceneColorTint");
    }
}
