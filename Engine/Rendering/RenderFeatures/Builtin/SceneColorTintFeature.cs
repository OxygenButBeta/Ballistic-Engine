namespace BallisticEngine;

[RenderFeature("Scene Color Tint", "Custom")]
public class SceneColorTintFeature : RenderFeature {
    [Tooltip("Multiplied into the scene color. White = no change.")]
    [ColorUsage]
    public Vector3 Tint { get; set; } = Vector3.One;

    [Range(0f, 1f)]
    [Tooltip("How strongly the tint is applied (0 = off, 1 = full).")]
    public float Strength { get; set; } = 1f;

    public override RenderPassEvent Event => RenderPassEvent.PostProcess;

    public override void Declare(IFeatureIOBuilder io) {
        io.ReadWrite("SceneColor");
    }

    public override void Record(IFeaturePassRecorder recorder) {
        recorder.BlitFullscreen(recorder.SceneColor, recorder.SceneColor, "SceneColorTint");
    }
}
