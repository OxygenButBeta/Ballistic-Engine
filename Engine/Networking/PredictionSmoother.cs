using System.Numerics;

namespace BallisticEngine;

// Visible-correction smoothing for a predicting owner (plan §13 P5d). P5b's reconcile SNAPS the
// transform to the authoritative+replayed pose. When the prediction was CORRECT the snap lands where the
// render already was (invisible). When it was WRONG (a misprediction — the server applied something the
// client couldn't predict, e.g. a collision), the reconciled pose differs from what was rendered → a
// visible POP. This carries a decaying render OFFSET so the correction EASES IN over a few frames instead
// of popping. Proven in %TEMP%\bal-rollback-test (11/11: corrects without a jarring snap, converges to
// authority, a huge divergence snaps rather than rubber-banding forever).
//
// The decay is HYBRID — an exponential tail PLUS a hard per-frame cap — so the visible correction STEP is
// bounded regardless of the error SIZE (a pure exponential erases ~15% of a big error in one frame = a
// visible jump). A correction larger than SnapThreshold is NOT smoothed (a teleport — rubber-banding
// toward a far-off truth feels worse than a clean snap).
public sealed class PredictionSmoother {
    public Vector3 Offset { get; private set; }   // render = authoritative pose + Offset; decays to 0

    const float Retain = 0.85f;               // exponential tail (~15%/frame on small errors)
    const float MaxCorrectionPerFrame = 0.08f; // hard cap on the per-frame correction step (metres)
    const float SnapThreshold = 5f;           // a correction bigger than this SNAPS (no smoothing)

    // After a reconcile snapped the transform from `renderedBefore` to `authoritative`, set the offset so
    // the render position is UNCHANGED this frame (render == renderedBefore), then Decay eases it out. A
    // correction beyond SnapThreshold is left unsmoothed (Offset 0 -> the render snaps to authority).
    public void OnCorrection(Vector3 renderedBefore, Vector3 authoritative) {
        Vector3 correction = authoritative - renderedBefore;
        Offset = correction.Length() > SnapThreshold ? Vector3.Zero : renderedBefore - authoritative;
    }

    // Erase the offset toward 0 each fixed tick: exponential, but never more than MaxCorrectionPerFrame in
    // one frame (bounded visible step). Returns the current offset to add to the authoritative pose.
    public Vector3 Decay() {
        Vector3 target = Offset * Retain;
        Vector3 erased = Offset - target;
        float len = erased.Length();
        if (len > MaxCorrectionPerFrame && len > 0f)
            erased *= MaxCorrectionPerFrame / len;     // clamp the per-frame correction step
        Offset -= erased;
        if (Offset.Length() < 0.0005f)
            Offset = Vector3.Zero;
        return Offset;
    }

    public bool IsActive => Offset != Vector3.Zero;
    public void Clear() => Offset = Vector3.Zero;
}
