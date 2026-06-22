namespace BallisticEngine.Editor;

internal static class BuildProgress {
    public static volatile bool IsBuilding;
    public static volatile string Status = "Building...";
    public static volatile string Detail = "";

    public const int TotalPhases = 6;
    static volatile int phase;

    public static float Fraction => Math.Clamp(phase / (float)TotalPhases, 0f, 1f);

    public static void Begin() {
        IsBuilding = true;
        phase = 0;
        Status = "Starting build...";
        Detail = "";
    }

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
