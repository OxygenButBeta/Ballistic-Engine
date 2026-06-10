using System.Text;
using ImGuiNET;

namespace BallisticEngine.Editor;

// Lists the project's assets (from the import pipeline) grouped by folder. Each asset is a
// drag source carrying its GUID, so it can be dropped onto an Inspector asset slot. Double-click
// a .scene to load it; Refresh re-imports.
internal sealed class AssetBrowserPanel {
    public const string DragType = "BALLISTIC_ASSET";

    string filter = "";

    public void Draw() {
        ImGui.SetNextWindowPos(new System.Numerics.Vector2(220, 600), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new System.Numerics.Vector2(760, 280), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Assets")) { ImGui.End(); return; }

        if (ImGui.Button("Refresh"))
            AssetDatabase.Refresh();
        ImGui.SameLine();
        ImGui.InputText("Filter", ref filter, 128);
        ImGui.Separator();

        // Sort by path for stable grouping.
        var assets = AssetDatabase.EnumerateAssets()
            .Where(kv => filter.Length == 0 || kv.Key.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        string currentFolder = null;
        bool folderOpen = false;

        foreach ((string path, Guid guid) in assets) {
            string folder = GetFolder(path);
            if (folder != currentFolder) {
                if (folderOpen) ImGui.Unindent();
                currentFolder = folder;
                folderOpen = ImGui.CollapsingHeader(folder.Length == 0 ? "Assets" : folder);
                if (folderOpen) ImGui.Indent();
            }

            if (!folderOpen)
                continue;

            DrawAssetRow(path, guid);
        }

        if (folderOpen) ImGui.Unindent();
        ImGui.End();
    }

    static void DrawAssetRow(string path, Guid guid) {
        var fileName = Path.GetFileName(path);
        ImGui.Selectable($"{fileName}##{guid}");

        if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) &&
            path.EndsWith(".scene", StringComparison.OrdinalIgnoreCase)) {
            LoadScene(path);
        }

        BeginAssetDragSource(fileName, guid);
    }

    static unsafe void BeginAssetDragSource(string fileName, Guid guid) {
        if (!ImGui.BeginDragDropSource())
            return;

        byte[] payload = Encoding.ASCII.GetBytes(guid.ToString("N"));
        fixed (byte* p = payload)
            ImGui.SetDragDropPayload(DragType, (IntPtr)p, (uint)payload.Length);

        ImGui.Text(fileName);
        ImGui.EndDragDropSource();
    }

    static void LoadScene(string assetPath) {
        Scene scene = SceneManager.GetCurrentScene();
        if (SceneManager.IsPlaying)
            SceneManager.StopPlay();

        scene.Clear();
        BallisticEngine.Serialization.SceneSerializer.Load(
            AssetDatabase.Project.ResolveAbsolute(assetPath));
    }

    static string GetFolder(string path) {
        int slash = path.LastIndexOf('/');
        return slash < 0 ? "" : path[..slash];
    }
}
