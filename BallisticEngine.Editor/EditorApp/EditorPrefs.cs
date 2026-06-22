using System.Text.Json;
using System.Text.Json.Serialization;
using SysVec4 = System.Numerics.Vector4;

namespace BallisticEngine.Editor;

internal sealed class EditorPrefs {
    public float AccentR { get; set; } = 0.239f;
    public float AccentG { get; set; } = 0.545f;
    public float AccentB { get; set; } = 0.831f;

    public float UiScale { get; set; } = 1f;

    public bool AlwaysRefresh { get; set; } = true;
    public float CameraBaseSpeed { get; set; } = 10f;
    public float GizmoSize { get; set; } = 90f;

    public int FrameRateLimit { get; set; }

    public float AssetTreeWidth { get; set; } = 190f;

    public List<string> FavoriteFolders { get; set; } = new();

    public bool ShowGrid { get; set; } = true;
    public float GridSize { get; set; } = 1f;
    public bool ShowGizmos { get; set; } = true;
    public float SnapMove { get; set; } = 0.5f;
    public float SnapRotate { get; set; } = 15f;
    public float SnapScale { get; set; } = 0.25f;

    public Dictionary<string, string> LastScenes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> LastCameras { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static string GetLastCamera(string projectRoot) =>
        projectRoot is not null && Current.LastCameras.TryGetValue(projectRoot, out var v) ? v : null;

    public static void SetLastCamera(string projectRoot, string pose) {
        if (projectRoot is null) return;
        Current.LastCameras[projectRoot] = pose;
    }

    public static string GetLastScene(string projectRoot) =>
        Current.LastScenes.TryGetValue(projectRoot, out var scene) ? scene : null;

    public static void SetLastScene(string projectRoot, string scenePath) {
        if (string.IsNullOrEmpty(scenePath))
            Current.LastScenes.Remove(projectRoot);
        else
            Current.LastScenes[projectRoot] = scenePath;
        Save();
    }

    [JsonIgnore]
    public SysVec4 Accent {
        get => new(0.831f, 0.608f, 0.271f, 1f);
        set { }
    }

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
