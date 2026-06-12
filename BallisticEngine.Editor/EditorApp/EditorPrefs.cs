using System.Text.Json;
using System.Text.Json.Serialization;
using SysVec4 = System.Numerics.Vector4;

namespace BallisticEngine.Editor;

// Persisted editor preferences (theme accent, viewport defaults, gizmo/snap settings). Stored as a
// single JSON file under %AppData%/BallisticEngine so settings survive across runs and projects.
// Load() runs once at startup BEFORE the theme is first applied; Save() runs whenever the Settings
// panel changes something. All access is via the static Current snapshot.
internal sealed class EditorPrefs {
    // --- Theme --- refined azure accent that pairs with the cool-graphite panels (0x3D8BD4).
    public float AccentR { get; set; } = 0.239f;
    public float AccentG { get; set; } = 0.545f;
    public float AccentB { get; set; } = 0.831f;

    // User UI scale multiplier on top of the auto-detected monitor DPI (Unity's editor UI scale).
    public float UiScale { get; set; } = 1f;

    // --- Viewport / camera ---
    public bool AlwaysRefresh { get; set; } = true;
    public float CameraBaseSpeed { get; set; } = 10f;
    public float GizmoSize { get; set; } = 90f;     // on-screen handle length in px

    // --- Performance --- 0 = VSync (Adaptive), otherwise a hard FPS cap (e.g. 60, 120, 144).
    public int FrameRateLimit { get; set; }

    // --- Asset browser --- width of the folder tree pane (unscaled px; multiplied by DPI scale).
    public float AssetTreeWidth { get; set; } = 190f;

    // --- Grid + snapping ---
    public bool ShowGrid { get; set; } = true;
    public float GridSize { get; set; } = 1f;
    public bool ShowGizmos { get; set; } = true;
    public float SnapMove { get; set; } = 0.5f;
    public float SnapRotate { get; set; } = 15f;
    public float SnapScale { get; set; } = 0.25f;

    // --- Session --- last scene opened per project (project root path -> "Assets/..." scene path), so
    // reopening the editor restores the scene you were last editing instead of always the StartupScene.
    // Keyed by project so switching between projects each remembers its own scene.
    public Dictionary<string, string> LastScenes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // Last Scene-view camera pose per project (root path -> "px,py,pz,pitch,yaw"), so reopening the
    // editor restores where you were looking. Keyed per project like LastScenes.
    public Dictionary<string, string> LastCameras { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static string GetLastCamera(string projectRoot) =>
        projectRoot is not null && Current.LastCameras.TryGetValue(projectRoot, out var v) ? v : null;

    public static void SetLastCamera(string projectRoot, string pose) {
        if (projectRoot is null) return;
        Current.LastCameras[projectRoot] = pose;
    }

    // Returns the last scene opened for this project root, or null if none has been recorded yet.
    public static string GetLastScene(string projectRoot) =>
        Current.LastScenes.TryGetValue(projectRoot, out var scene) ? scene : null;

    // Records the last scene opened for this project root and persists prefs. A null/empty path clears it
    // (e.g. File > New leaves no file to reopen, so fall back to the StartupScene next launch).
    public static void SetLastScene(string projectRoot, string scenePath) {
        if (string.IsNullOrEmpty(scenePath))
            Current.LastScenes.Remove(projectRoot);
        else
            Current.LastScenes[projectRoot] = scenePath;
        Save();
    }

    [JsonIgnore]
    public SysVec4 Accent {
        get => new(AccentR, AccentG, AccentB, 1f);
        set { AccentR = value.X; AccentG = value.Y; AccentB = value.Z; }
    }

    // ---- Storage ----

    public static EditorPrefs Current { get; private set; } = new();

    static string FilePath {
        get {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BallisticEngine");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "editorprefs.json");
        }
    }

    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static void Load() {
        try {
            if (File.Exists(FilePath))
                Current = JsonSerializer.Deserialize<EditorPrefs>(File.ReadAllText(FilePath)) ?? new EditorPrefs();
        }
        catch (Exception exception) {
            Debugging.LogWarning($"Could not read editor prefs: {exception.Message}");
            Current = new EditorPrefs();
        }
    }

    public static void Save() {
        try {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, JsonOptions));
        }
        catch (Exception exception) {
            Debugging.LogWarning($"Could not save editor prefs: {exception.Message}");
        }
    }
}
