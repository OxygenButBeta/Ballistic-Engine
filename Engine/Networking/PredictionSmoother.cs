namespace BallisticEngine;

public sealed class PredictionSmoother {
    public Vector3 Offset { get; private set; }

    const float Retain = 0.85f;
    const float MaxCorrectionPerFrame = 0.08f;
    const float SnapThreshold = 5f;

    public void OnCorrection(Vector3 renderedBefore, Vector3 authoritative) {
        Vector3 correction = authoritative - renderedBefore;
        Offset = correction.Length() > SnapThreshold ? Vector3.Zero : renderedBefore - authoritative;
    }

    public Vector3 Decay() {
        Vector3 target = Offset * Retain;
        Vector3 erased = Offset - target;
        float len = erased.Length();
        if (len > MaxCorrectionPerFrame && len > 0f)
            erased *= MaxCorrectionPerFrame / len;
        Offset -= erased;
        if (Offset.Length() < 0.0005f)
            Offset = Vector3.Zero;
        return Offset;
    }

    public bool IsActive => Offset != Vector3.Zero;
    public void Clear() => Offset = Vector3.Zero;
}
