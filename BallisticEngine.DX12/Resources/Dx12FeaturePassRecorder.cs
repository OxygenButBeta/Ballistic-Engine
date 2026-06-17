using System;
using BallisticEngine;   // IFeaturePassRecorder, RenderFeature

namespace BallisticEngine.DX12;

// PHASE-3 (chunk 20) — the DX12 impl of the engine-agnostic IFeaturePassRecorder (the URP CommandBuffer role).
// A RenderFeature's Record() drives THIS at its event; the recorder resolves the canonical string handle names
// to concrete DX12 targets (so the feature never sees a Dx12 type — the §3 seam) and routes the verbs to the
// existing DX12 blit infra (Dx12FeatureBlitter). DELIBERATELY MINIMAL (D4): the chunk-20 proof needs only the
// SceneColor accessor + an in-place BlitFullscreen("SceneColorTint"); every later verb is added on a concrete
// feature's demand and logged in the design doc §5 (D4).
//
// One recorder instance is reused per frame (DX12HDRenderer owns it); the adapter Re-binds the frame context +
// current feature before each feature.Record. SceneColor follows the LIVE ctx.SceneColor (FSR/back-copy), so a
// feature reading recorder.SceneColor always targets the current scene-color handle.
public sealed class Dx12FeaturePassRecorder : IFeaturePassRecorder {
    // The canonical handle name for the live scene-color target — the SAME string the built-ins declare against
    // (Dx12*Pass.Declare → b.Resource("SceneColor")) and the IO builder mints. A feature reads/writes through
    // this rather than a literal so it follows ctx.SceneColor (native target vs FSR output).
    public const string SceneColorHandle = "SceneColor";

    readonly Dx12FeatureBlitter blitter;
    Dx12FrameContext ctx;       // re-bound per feature.Record by the adapter
    RenderFeature feature;      // the feature currently recording (carries its typed params for the blitter)

    public Dx12FeaturePassRecorder(Dx12FeatureBlitter blitter) {
        this.blitter = blitter;
    }

    // Bind the per-frame context + the feature about to record. Called by Dx12FeaturePassAdapter.Record just
    // before driving feature.Record(this).
    internal void Bind(Dx12FrameContext frameCtx, RenderFeature recordingFeature) {
        ctx = frameCtx;
        feature = recordingFeature;
    }

    public string SceneColor => SceneColorHandle;

    public void SetRenderTarget(string handleName) {
        // D4 minimal: only SceneColor is addressable today; binding it is implicit in BlitFullscreen's dst. A
        // future feature that draws its own geometry into a named target grows this (resolve name → target →
        // RenderColorOnly). Validate the name so a typo fails loud rather than silently no-op'ing.
        Resolve(handleName);   // throws on an unknown handle
    }

    public void BlitFullscreen(string sourceHandle, string destHandle, string shaderOrMaterial = null) {
        Dx12OffscreenTarget src = Resolve(sourceHandle);
        Dx12OffscreenTarget dst = Resolve(destHandle);

        // D4 minimal verb set: the only built-in shader the proof feature uses is "SceneColorTint" (an in-place
        // RMW of SceneColor). The blitter ping-pongs through its own scratch so src==dst==SceneColor is legal.
        switch (shaderOrMaterial) {
            case "SceneColorTint":
                if (!ReferenceEquals(src, dst))
                    throw new NotSupportedException(
                        "[Dx12FeaturePassRecorder] SceneColorTint is an in-place blit (src must equal dst).");
                blitter.Tint(dst, feature);
                break;
            case null:
                // A plain copy (src → dst). Grown on demand; the proof feature never hits this.
                if (!ReferenceEquals(src, dst)) dst.CopyColorFrom(src);
                break;
            default:
                throw new NotSupportedException(
                    $"[Dx12FeaturePassRecorder] unknown blit shader/material '{shaderOrMaterial}'. The verb set is " +
                    "minimal by design (D4) — add the backend for a new shader on a concrete feature's demand.");
        }
    }

    // Resolve a canonical string handle to its concrete DX12 target. Today only SceneColor is mapped (D4); the
    // map grows as the verb surface does. An unknown name throws (a feature typo should fail loud, not no-op).
    Dx12OffscreenTarget Resolve(string handleName) => handleName switch {
        SceneColorHandle => ctx.SceneColor,
        _ => throw new NotSupportedException(
            $"[Dx12FeaturePassRecorder] handle '{handleName}' is not mapped. Only '{SceneColorHandle}' is " +
            "addressable in the chunk-20 verb set (D4) — extend Resolve when a feature needs another."),
    };
}
