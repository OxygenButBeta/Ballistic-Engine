using System.Security.Cryptography;
using System.Text;
using Hexa.NET.ImGui;
using SysVec2 = System.Numerics.Vector2;

namespace BallisticEngine.Editor;

internal static class EditorLayout {
    public const string Entities = "Entities";
    public const string SceneComponents = "Scene";
    public const string Inspector = "Inspector";
    public const string SceneView = "Scene View";
    public const string GameView = "Game View";
    public const string Assets = "Assets";
    public const string Console = "Console";

    static string projectKey = "default";

    public static void SetProject(string projectRootPath) {
        projectKey = string.IsNullOrEmpty(projectRootPath) ? "default" : projectRootPath;
    }

    const int LayoutVersion = 2;

    static string LayoutDir {
        get {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "BallisticEngine", "Layouts");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    static string ProjectStem =>
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(projectKey.ToLowerInvariant())))[..16];

    static string LayoutFile => Path.Combine(LayoutDir, $"{ProjectStem}.v{LayoutVersion}.ini");

    static string PanelStateFile => Path.Combine(LayoutDir, $"{ProjectStem}.v{LayoutVersion}.panels");

    public static bool HasSaved => File.Exists(LayoutFile);

    public static void Load() {
        try {
            if (File.Exists(LayoutFile))
                ImGui.LoadIniSettingsFromMemory(File.ReadAllText(LayoutFile));
        }
        catch (Exception e) {
            Debugging.LogWarning($"Could not load editor layout: {e.Message}");
        }
    }

    public static unsafe void Save() {
        try {
            byte* ini = ImGui.SaveIniSettingsToMemory();
            string text = System.Runtime.InteropServices.Marshal.PtrToStringUTF8((IntPtr)ini);
            if (text is not null)
                File.WriteAllText(LayoutFile, text);
        }
        catch (Exception e) {
            Debugging.LogWarning($"Could not save editor layout: {e.Message}");
        }
    }

    public static void DeleteSaved() {
        try {
            if (File.Exists(LayoutFile)) File.Delete(LayoutFile);
            if (File.Exists(PanelStateFile)) File.Delete(PanelStateFile);
        }
        catch (Exception e) { Debugging.LogWarning($"Could not reset editor layout: {e.Message}"); }
    }

    public static void SavePanelState(IEnumerable<string> hiddenKeys) {
        try { File.WriteAllLines(PanelStateFile, hiddenKeys); }
        catch (Exception e) { Debugging.LogWarning($"Could not save panel state: {e.Message}"); }
    }

    public static IReadOnlyCollection<string> LoadPanelState() {
        try {
            if (File.Exists(PanelStateFile))
                return File.ReadAllLines(PanelStateFile)
                    .Select(l => l.Trim())
                    .Where(l => l.Length > 0)
                    .ToHashSet();
        }
        catch (Exception e) { Debugging.LogWarning($"Could not load panel state: {e.Message}"); }
        return Array.Empty<string>();
    }

    public static unsafe void BuildDefault(uint dockId, SysVec2 size) {
        ImGuiP.DockBuilderRemoveNode(dockId);
        ImGuiP.DockBuilderAddNode(dockId, ImGuiDockNodeFlags.None);
        ImGuiP.DockBuilderSetNodeSize(dockId, size);

        uint center = dockId, leftOuter, details, bottom, leftTop, leftBottom;
        ImGuiP.DockBuilderSplitNode(center, ImGuiDir.Down, 0.26f, &bottom, &center);
        ImGuiP.DockBuilderSplitNode(center, ImGuiDir.Left, 0.15f, &leftOuter, &center);
        ImGuiP.DockBuilderSplitNode(center, ImGuiDir.Left, 0.26f, &details, &center);
        ImGuiP.DockBuilderSplitNode(leftOuter, ImGuiDir.Up, 0.42f, &leftTop, &leftBottom);

        ImGuiP.DockBuilderDockWindow(SceneComponents, leftTop);
        ImGuiP.DockBuilderDockWindow(Entities, leftBottom);
        ImGuiP.DockBuilderDockWindow(Inspector, details);
        ImGuiP.DockBuilderDockWindow(Assets, bottom);
        ImGuiP.DockBuilderDockWindow(Console, bottom);
        ImGuiP.DockBuilderDockWindow(SceneView, center);
        ImGuiP.DockBuilderDockWindow(GameView, center);
        ImGuiP.DockBuilderFinish(dockId);
    }
}
