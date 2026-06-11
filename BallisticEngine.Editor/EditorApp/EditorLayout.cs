using System.Security.Cryptography;
using System.Text;
using Hexa.NET.ImGui;
using SysVec2 = System.Numerics.Vector2;

namespace BallisticEngine.Editor;

// Per-project dock layout persistence + the programmatic default arrangement.
//
// ImGui's automatic imgui.ini IO is disabled (ImGuiController sets io.IniFilename = null), so the
// editor owns the layout: it serializes ImGui's window/dock settings to a string and writes it under
// %AppData%/BallisticEngine/Layouts/<projectHash>.ini, keyed by the project root path. Load() applies
// it before the first frame; if none exists, BuildDefault() lays out Hierarchy / Inspector / Viewport
// / Assets+Console via DockBuilder. "Reset Layout" deletes the file and rebuilds the default.
internal static class EditorLayout {
    // The window names docked into the default layout (must match the ImGui.Begin titles in BuildUI).
    // Scene/Game and Entities/SceneComponents are separate dockable windows, tabbed together by default.
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

    // Bumped whenever the set of dockable window NAMES changes (a saved layout from an older version
    // references windows that no longer exist, so we version the file to fall back to BuildDefault).
    const int LayoutVersion = 2;

    static string LayoutFile {
        get {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "BallisticEngine", "Layouts");
            Directory.CreateDirectory(dir);
            byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes(projectKey.ToLowerInvariant()));
            return Path.Combine(dir, $"{Convert.ToHexString(hash)[..16]}.v{LayoutVersion}.ini");
        }
    }

    // True when a saved layout exists for this project (so the dock host skips BuildDefault).
    public static bool HasSaved => File.Exists(LayoutFile);

    // Apply the saved layout, if any. Call ONCE after the context is created and BEFORE the first
    // NewFrame (the settings must be in place before windows are submitted).
    public static void Load() {
        try {
            if (File.Exists(LayoutFile))
                ImGui.LoadIniSettingsFromMemory(File.ReadAllText(LayoutFile));
        }
        catch (Exception e) {
            Debugging.LogWarning($"Could not load editor layout: {e.Message}");
        }
    }

    // Persist the current layout. Cheap; called when ImGui flags WantSaveIniSettings and on exit.
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

    // Forget the saved layout so the next BuildDefault (driven by EditorApplication's reset flag) lays
    // the panels out fresh.
    public static void DeleteSaved() {
        try { if (File.Exists(LayoutFile)) File.Delete(LayoutFile); }
        catch (Exception e) { Debugging.LogWarning($"Could not reset editor layout: {e.Message}"); }
    }

    // Builds the default arrangement inside `dockId`: Hierarchy left, Inspector right, Assets+Console
    // tabbed along the bottom, Viewport filling the center. Run inside the dock-host window for the
    // frame the layout needs (re)building.
    public static unsafe void BuildDefault(uint dockId, SysVec2 size) {
        ImGuiP.DockBuilderRemoveNode(dockId);
        ImGuiP.DockBuilderAddNode(dockId, ImGuiDockNodeFlags.None);
        ImGuiP.DockBuilderSetNodeSize(dockId, size);

        uint center = dockId, left, right, bottom;
        ImGuiP.DockBuilderSplitNode(center, ImGuiDir.Left, 0.16f, &left, &center);
        ImGuiP.DockBuilderSplitNode(center, ImGuiDir.Right, 0.22f, &right, &center);
        ImGuiP.DockBuilderSplitNode(center, ImGuiDir.Down, 0.28f, &bottom, &center);

        ImGuiP.DockBuilderDockWindow(Entities, left);
        ImGuiP.DockBuilderDockWindow(SceneComponents, left);   // tabbed with Entities
        ImGuiP.DockBuilderDockWindow(Inspector, right);
        ImGuiP.DockBuilderDockWindow(Assets, bottom);
        ImGuiP.DockBuilderDockWindow(Console, bottom);   // tabbed with Assets
        ImGuiP.DockBuilderDockWindow(SceneView, center);
        ImGuiP.DockBuilderDockWindow(GameView, center);  // tabbed with Scene View
        ImGuiP.DockBuilderFinish(dockId);
    }
}
