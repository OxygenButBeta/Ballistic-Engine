namespace BallisticEngine.Editor;

// Shared build state read by BusyOverlay (so a build shows the same full-window progress card as
// asset imports and probe bakes) and written by BuildPanel's worker thread. The build moves through
// a known number of discrete phases, so progress is determinate (a real bar, not a sweep).
//
// All members are volatile / simple value writes — the worker thread sets them, the render thread
// reads them once per frame. No lock needed: a torn read just shows a slightly stale status string.
internal static class BuildProgress {
    public static volatile bool IsBuilding;
    public static volatile string Status = "Building...";
    public static volatile string Detail = "";

    // The pipeline's known top-level phase count (compile, bake+guidmap, manifest, publish, rename,
    // copy-data). The publish phase dominates wall-clock; the bar is coarse but always moves forward.
    public const int TotalPhases = 6;
    static volatile int phase;

    public static float Fraction => Math.Clamp(phase / (float)TotalPhases, 0f, 1f);

    public static void Begin() {
        IsBuilding = true;
        phase = 0;
        Status = "Starting build...";
        Detail = "";
    }

    // Advances to the next phase and sets the headline + subtext shown on the overlay card.
    public static void Step(string status, string detail = "") {
        phase = Math.Min(phase + 1, TotalPhases);
        Status = status;
        Detail = detail;
    }

    public static void End() {
        phase = TotalPhases;
        IsBuilding = false;
        Detail = "";
    }
}
