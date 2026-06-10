using System.Text;
using ImGuiNET;
using SysVec2 = System.Numerics.Vector2;
using SysVec4 = System.Numerics.Vector4;

namespace BallisticEngine.Editor;

// File-explorer-style asset browser: navigable folders with a tile grid (folders + assets as
// boxes). Single click selects (Inspector shows the asset), double click opens (folder/scene),
// tiles are drag sources carrying the asset GUID. Files dropped from the OS land in the
// current folder and import (wired in EditorApplication via the window FileDrop event).
internal sealed class AssetBrowserPanel {
    public const string DragType = "BALLISTIC_ASSET";

    readonly EditorState state;
    readonly Func<float> scale;

    string filter = "";

    // Project-relative with forward slashes, e.g. "Assets" or "Assets/Default/Sky".
    public string CurrentFolder { get; private set; } = "Assets";

    public AssetBrowserPanel(EditorState state, Func<float> scale) {
        this.state = state;
        this.scale = scale;
    }

    public void DrawContents() {
        float s = scale();
        DrawNavigationBar(s);
        ImGui.Separator();

        var searching = filter.Length > 0;
        var assets = AssetDatabase.EnumerateAssets().OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase);

        // Folders directly under CurrentFolder, and assets directly inside it (or a flat
        // project-wide result list while searching).
        var folders = new List<string>();
        var files = new List<(string path, Guid guid)>();
        var prefix = CurrentFolder + "/";

        foreach ((string path, Guid guid) in assets) {
            if (searching) {
                if (path.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    files.Add((path, guid));
                continue;
            }

            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var rest = path[prefix.Length..];
            var slash = rest.IndexOf('/');
            if (slash >= 0) {
                var sub = prefix + rest[..slash];
                if (!folders.Contains(sub))
                    folders.Add(sub);
            }
            else {
                files.Add((path, guid));
            }
        }

        DrawGrid(folders, files, s);
    }

    void DrawNavigationBar(float s) {
        ImGui.BeginDisabled(CurrentFolder == "Assets");
        if (ImGui.Button("<", new SysVec2(28 * s, 0))) {
            var slash = CurrentFolder.LastIndexOf('/');
            CurrentFolder = slash > 0 ? CurrentFolder[..slash] : "Assets";
        }
        ImGui.EndDisabled();

        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(CurrentFolder.Replace("/", "  /  "));

        ImGui.SameLine(ImGui.GetWindowWidth() - 320 * s);
        if (ImGui.Button("Refresh"))
            AssetDatabase.Refresh();
        ImGui.SameLine();
        ImGui.SetNextItemWidth(200 * s);
        ImGui.InputTextWithHint("##filter", "Search...", ref filter, 128);
    }

    void DrawGrid(List<string> folders, List<(string path, Guid guid)> files, float s) {
        float tile = 86 * s;
        float cellW = tile + ImGui.GetStyle().ItemSpacing.X;
        var columns = Math.Max(1, (int)(ImGui.GetContentRegionAvail().X / cellW));

        ImGui.BeginChild("##grid");
        var column = 0;

        foreach (var folder in folders) {
            DrawFolderTile(folder, tile);
            NextCell(ref column, columns);
        }

        foreach ((string path, Guid guid) in files) {
            DrawAssetTile(path, guid, tile);
            NextCell(ref column, columns);
        }

        if (folders.Count == 0 && files.Count == 0)
            ImGui.TextDisabled(filter.Length > 0 ? "No assets match." : "Empty folder. Drop files here to import.");

        ImGui.EndChild();
    }

    static void NextCell(ref int column, int columns) {
        column++;
        if (column >= columns)
            column = 0;
        else
            ImGui.SameLine();
    }

    void DrawFolderTile(string folderPath, float tile) {
        var name = folderPath[(folderPath.LastIndexOf('/') + 1)..];

        ImGui.PushID(folderPath);
        ImGui.BeginGroup();

        ImGui.PushStyleColor(ImGuiCol.Button, new SysVec4(0.27f, 0.24f, 0.16f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new SysVec4(0.36f, 0.32f, 0.20f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new SysVec4(0.45f, 0.39f, 0.23f, 1f));
        ImGui.Button("DIR", new SysVec2(tile, tile));
        ImGui.PopStyleColor(3);

        if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            CurrentFolder = folderPath;

        TileLabel(name, tile);
        ImGui.EndGroup();
        ImGui.PopID();
    }

    void DrawAssetTile(string path, Guid guid, float tile) {
        var name = Path.GetFileName(path);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        (string tag, SysVec4 color) = Style(ext);

        var selected = state.SelectedAssetGuid == guid;

        ImGui.PushID(path);
        ImGui.BeginGroup();

        ImGui.PushStyleColor(ImGuiCol.Button, color);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Brighten(color, 1.25f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, Brighten(color, 1.45f));
        // Button fires on RELEASE, so starting a drag does NOT change the selection —
        // you can drag an asset onto an Inspector slot without losing what's inspected.
        var clicked = ImGui.Button(tag, new SysVec2(tile, tile));
        ImGui.PopStyleColor(3);

        if (selected) {
            ImGui.GetWindowDrawList().AddRect(
                ImGui.GetItemRectMin(), ImGui.GetItemRectMax(),
                ImGui.GetColorU32(new SysVec4(0.24f, 0.47f, 0.71f, 1f)), 3f, ImDrawFlags.None, 2f);
        }

        if (clicked)
            state.SelectAsset(path, guid);

        if (ImGui.IsItemHovered()) {
            if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && ext == ".scene")
                LoadScene(path);
            ImGui.SetTooltip(path);
        }

        BeginAssetDragSource(name, guid);
        TileLabel(name, tile);
        ImGui.EndGroup();
        ImGui.PopID();
    }

    static void TileLabel(string name, float tile) {
        // Trim the label to the tile width.
        var label = name;
        while (label.Length > 4 && ImGui.CalcTextSize(label).X > tile)
            label = label[..^4] + "...";
        ImGui.TextUnformatted(label);
    }

    static (string, SysVec4) Style(string ext) => ext switch {
        ".fbx" or ".obj" or ".gltf" or ".glb" => ("MESH", new SysVec4(0.16f, 0.24f, 0.34f, 1f)),
        ".png" or ".jpg" or ".jpeg" or ".tga" or ".bmp" => ("TEX", new SysVec4(0.16f, 0.30f, 0.22f, 1f)),
        ".hdr" or ".exr" => ("HDR", new SysVec4(0.30f, 0.26f, 0.14f, 1f)),
        ".mat" => ("MAT", new SysVec4(0.28f, 0.18f, 0.30f, 1f)),
        ".scene" => ("SCN", new SysVec4(0.33f, 0.21f, 0.13f, 1f)),
        ".pyscene" => ("PYS", new SysVec4(0.27f, 0.17f, 0.11f, 1f)),
        ".shader" or ".glsl" => ("SHD", new SysVec4(0.13f, 0.28f, 0.30f, 1f)),
        ".cubemap" => ("SKY", new SysVec4(0.18f, 0.26f, 0.33f, 1f)),
        _ => ("FILE", new SysVec4(0.22f, 0.22f, 0.22f, 1f)),
    };

    static SysVec4 Brighten(SysVec4 c, float f) => new(
        Math.Min(c.X * f, 1f), Math.Min(c.Y * f, 1f), Math.Min(c.Z * f, 1f), 1f);

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
}
