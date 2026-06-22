namespace BallisticEngine.DX12;

public sealed class Dx12FeaturePassRecorder : IFeaturePassRecorder {
    public const string SceneColorHandle = "SceneColor";

    readonly Dx12FeatureBlitter blitter;
    Dx12FrameContext ctx;
    RenderFeature feature;

    public Dx12FeaturePassRecorder(Dx12FeatureBlitter blitter) {
        this.blitter = blitter;
    }

    internal void Bind(Dx12FrameContext frameCtx, RenderFeature recordingFeature) {
        ctx = frameCtx;
        feature = recordingFeature;
    }

    public string SceneColor => SceneColorHandle;

    public void SetRenderTarget(string handleName) {
        Resolve(handleName);
    }

    public void BlitFullscreen(string sourceHandle, string destHandle, string shaderOrMaterial = null) {
        Dx12OffscreenTarget src = Resolve(sourceHandle);
        Dx12OffscreenTarget dst = Resolve(destHandle);

        switch (shaderOrMaterial) {
            case "SceneColorTint":
                if (!ReferenceEquals(src, dst))
                    throw new NotSupportedException(
                        "[Dx12FeaturePassRecorder] SceneColorTint is an in-place blit (src must equal dst).");
                blitter.Tint(dst, feature);
                break;
            case null:
                if (!ReferenceEquals(src, dst)) dst.CopyColorFrom(src);
                break;
            default:
                throw new NotSupportedException(
                    $"[Dx12FeaturePassRecorder] unknown blit shader/material '{shaderOrMaterial}'. The verb set is " +
                    "minimal by design (D4) — add the backend for a new shader on a concrete feature's demand.");
        }
    }

    Dx12OffscreenTarget Resolve(string handleName) => handleName switch {
        SceneColorHandle => ctx.SceneColor,
        _ => throw new NotSupportedException(
            $"[Dx12FeaturePassRecorder] handle '{handleName}' is not mapped. Only '{SceneColorHandle}' is " +
            "addressable in the chunk-20 verb set (D4) — extend Resolve when a feature needs another."),
    };
}
