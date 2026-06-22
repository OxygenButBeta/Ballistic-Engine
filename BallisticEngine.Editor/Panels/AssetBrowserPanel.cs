using System.Reflection;
using BallisticEngine.Serialization;
using SysVec2 = System.Numerics.Vector2;
using SysVec4 = System.Numerics.Vector4;

namespace BallisticEngine.Editor;

internal sealed class AssetBrowserPanel : EditorWindow {
    static IEditorGui gui => EditorGui.Shared;

    public const string DragType = "BALLISTIC_ASSET";

    readonly EditorState state;
    readonly Func<float> scale;
    readonly ThumbnailCache thumbnails = new();

    static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".tga", ".bmp", ".hdr", ".exr"];

    string filter = "";

    int typeFilter;
    static readonly (string label, string[] exts)[] TypeFilters = [
        ("All", []),
        ("Models", [".fbx", ".obj", ".gltf", ".glb", ".dae"]),
        ("Textures", [".png", ".jpg", ".jpeg", ".tga", ".bmp", ".hdr", ".exr"]),
        ("Materials", [".mat"]),
        ("Audio", [".wav", ".wave", ".ogg"]),
        ("Shaders", [".shader", ".glsl"]),
        ("Scripts", [".cs"]),
        ("Scenes", [".scene", ".pyscene"]),
    ];
    static readonly string[] TypeFilterLabels = TypeFilters.Select(t => t.label).ToArray();

    static readonly string[] HiddenExtensions = [".pyscene"];
    bool showSourceFiles;

    static bool IsProtected(string path) => AssetOps.IsProtected(path);

    string renamingPath;
    string renameBuffer = "";
    bool renameFocusPending;

    readonly List<(string path, Guid guid)> visibleFiles = new();
    Guid anchorGuid;

    public string CurrentFolder { get; private set; } =
        Environment.GetEnvironmentVariable("BALLISTIC_EDITOR_FOLDER") is { Length: > 0 } f ? f : "Assets";

    public Action RequestScriptRebuild;

    protected override void OnGui(IEditorGui gui) => DrawContents();

    public AssetBrowserPanel(EditorState state, Func<float> scale) {
        DockKey = EditorLayout.Assets;
        Title = "Assets";
        Icon = EditorIcons.Folder;
        Singleton = false;

        this.state = state;
        this.scale = scale;
    }

    public void InvalidateThumbnails() => thumbnails.InvalidateAll();

    public void DrawContents() {
        float s = scale();
        thumbnails.Pump();
        DrawNavigationBar(s);
        EditorDecoration.DrawDivider();

        float treeW = Math.Clamp(EditorPrefs.Current.AssetTreeWidth, 140f, 420f) * s;
        gui.BeginChild("##foldertree", new SysVec2(treeW, 0), border: false);
        DrawFolderTree();
        gui.EndChild();

        DrawTreeSplitter(s);

        gui.BeginChild("##browserright", new SysVec2(0, 0), border: false);
        DrawGridPane(s);
        gui.EndChild();
    }

    void DrawGridPane(float s) {
        if (state.ConsumeRevealAsset() is { } revealPath)
            RevealAsset(revealPath);

        var searching = filter.Length > 0;
        var assets = AssetDatabase.EnumerateAssets().OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase);

        var folders = new List<string>();
        var files = new List<(string path, Guid guid)>();
        var prefix = CurrentFolder + "/";

        if (!searching) {
            var currentAbsolute = AssetDatabase.Project.ResolveAbsolute(CurrentFolder);
            if (Directory.Exists(currentAbsolute)) {
                foreach (var dir in Directory.GetDirectories(currentAbsolute)) {
                    string rel = prefix + Path.GetFileName(dir);
                    if (IsProtected(rel)) continue;
                    folders.Add(rel);
                }
            }
        }

        var typeExts = TypeFilters[typeFilter].exts;
        bool PassesType(string path) =>
            typeExts.Length == 0 || typeExts.Contains(Path.GetExtension(path).ToLowerInvariant());

        bool Hidden(string path) =>
            IsProtected(path) || (!showSourceFiles && HiddenExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()));

        bool MatchesFilter(string path) {
            if (filter.StartsWith("t:", StringComparison.OrdinalIgnoreCase)) {
                string term = filter[2..].Trim();
                if (term.Length == 0) return true;
                string ext = Path.GetExtension(path).ToLowerInvariant();
                foreach (var (label, exts) in TypeFilters)
                    if (exts.Length > 0 && label.Contains(term, StringComparison.OrdinalIgnoreCase) &&
                        exts.Contains(ext))
                        return true;
                return false;
            }
            return path.Contains(filter, StringComparison.OrdinalIgnoreCase);
        }

        foreach ((string path, Guid guid) in assets) {
            if (!PassesType(path) || Hidden(path))
                continue;

            if (searching) {
                if (MatchesFilter(path))
                    files.Add((path, guid));
                continue;
            }

            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                !path[prefix.Length..].Contains('/'))
                files.Add((path, guid));
        }

        if (typeFilter != 0 && !searching) {
            folders.Clear();
            files.Clear();
            foreach ((string path, Guid guid) in assets)
                if (PassesType(path) && !Hidden(path))
                    files.Add((path, guid));
        }

        DrawGrid(folders, files, s);
        DrawNewScriptPrompt();
        DrawNewAssetPrompt();
    }

    bool openNewAssetPrompt;
    string newAssetName = "";
    string newAssetExt = "";
    string newAssetKind = "Asset";
    Func<string, string> newAssetContent;
    Action newAssetPostCreate;

    string pendingDefaultName = "";
    bool focusNewAssetName;

    void PromptNewAsset(string kind, string defaultName, string ext, Func<string, string> content,
        Action postCreate = null) {
        newAssetKind = kind;
        pendingDefaultName = defaultName;
        newAssetExt = ext;
        newAssetContent = content;
        newAssetPostCreate = postCreate;
        openNewAssetPrompt = true;
    }

    void DrawNewAssetPrompt() {
        if (openNewAssetPrompt) {
            openNewAssetPrompt = false;
            newAssetName = pendingDefaultName;
            focusNewAssetName = true;
            gui.OpenPopup("##newasset");
        }

        gui.CenterNextWindowPos();
        PushPromptStyle();
        if (!gui.BeginPopupModalAutoResize("##newasset")) {
            PopPromptStyle();
            return;
        }

        gui.PushColor(EditorStyleColor.Text, EditorTheme.Text);
        gui.TextUnformatted($"New {newAssetKind}");
        gui.PopColor();
        gui.Separator();
        gui.Spacing();
        gui.TextUnformatted("Name");
        if (focusNewAssetName) {
            focusNewAssetName = false;
            gui.SetKeyboardFocusHere();
        }
        gui.SetNextItemWidth(300);
        bool enter = gui.InputTextEnter("##newassetname", ref newAssetName, 96);

        string trimmed = newAssetName.Trim();
        bool valid = trimmed.Length > 0 && trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
        if (!valid && trimmed.Length > 0)
            gui.TextColored(EditorTheme.Error, "Invalid file name.");
        else
            gui.TextDisabled($"Creates {trimmed}{newAssetExt}");

        gui.Spacing();
        PromptButtons(out bool create, valid);
        if (create || (enter && valid)) {
            CreateNamedAsset(trimmed);
            gui.CloseCurrentPopup();
        }
        gui.EndPopup();
        PopPromptStyle();
    }

    static void PushPromptStyle() {
        gui.PushWindowPadding(new SysVec2(20, 18));
        gui.PushFrameRounding(5f);
        gui.PushItemSpacing(new SysVec2(8, 8));
        gui.PushPopupBg(EditorTheme.PopupBg);
        gui.PushColor(EditorStyleColor.FrameBg, EditorTheme.InputBg);
    }

    static void PopPromptStyle() {
        gui.PopColor(2);
        gui.PopStyleVar(3);
    }

    static void PromptButtons(out bool create, bool createEnabled) {
        create = false;
        if (gui.Button("Cancel", new SysVec2(120, 0)))
            gui.CloseCurrentPopup();
        gui.SameLine();
        gui.BeginDisabled(!createEnabled);
        gui.PushColor(EditorStyleColor.Button, EditorTheme.PrimaryAction);
        gui.PushColor(EditorStyleColor.ButtonHovered, EditorTheme.PrimaryActionHovered);
        gui.PushColor(EditorStyleColor.ButtonActive, EditorTheme.PrimaryActionActive);
        create = gui.Button("Create", new SysVec2(120, 0));
        gui.PopColor(3);
        gui.EndDisabled();
    }

    void CreateNamedAsset(string name) {
        string dir = AssetDatabase.Project.ResolveAbsolute(CurrentFolder);
        string absolute = UniquePath(Path.Combine(dir, name + newAssetExt));
        try {
            if (newAssetContent is null) {
                Directory.CreateDirectory(absolute);
                return;
            }
            File.WriteAllText(absolute, newAssetContent(Path.GetFileNameWithoutExtension(absolute)));
            newAssetPostCreate?.Invoke();
            AsyncAssetImport.Request($"Creating {newAssetKind.ToLowerInvariant()}...");
        }
        catch (Exception e) {
            Debugging.LogError($"Could not create {newAssetKind}: {e.Message}");
        }
    }

    bool openNewScriptPrompt;
    string newScriptName = "";

    void DrawNewScriptPrompt() {
        if (openNewScriptPrompt) {
            openNewScriptPrompt = false;
            newScriptName = "NewBehaviour";
            gui.OpenPopup("##newscript");
        }

        gui.CenterNextWindowPos();
        PushPromptStyle();
        if (!gui.BeginPopupModalAutoResize("##newscript")) {
            PopPromptStyle();
            return;
        }

        gui.PushColor(EditorStyleColor.Text, EditorTheme.Text);
        gui.TextUnformatted("New Script");
        gui.PopColor();
        gui.Separator();
        gui.Spacing();
        gui.TextUnformatted("Class name (= file name)");
        if (gui.IsWindowAppearing())
            gui.SetKeyboardFocusHere();
        gui.SetNextItemWidth(300);
        bool enter = gui.InputTextEnter("##scriptname", ref newScriptName, 64);

        string className = ScriptTemplates.ClassName(newScriptName.Trim());
        bool valid = IsValidIdentifier(className);
        if (!valid && newScriptName.Trim().Length > 0)
            gui.TextColored(EditorTheme.Error, "Not a valid C# class name.");
        else
            gui.TextDisabled($"Creates {className}.cs : Behaviour");

        gui.Spacing();
        PromptButtons(out bool create, valid);
        if (create || (enter && valid)) {
            CreateScriptNamed(className);
            gui.CloseCurrentPopup();
        }
        gui.EndPopup();
        PopPromptStyle();
    }

    (string icon, SysVec4 tint) FolderIcon(string folderPath) {
        bool hasContent = FolderHasContent(folderPath);
        SysVec4 full = new(0.95f, 0.78f, 0.42f, 1f);
        SysVec4 empty = new(0.62f, 0.55f, 0.40f, 0.7f);
        return hasContent ? (EditorIcons.FolderOpen, full) : (EditorIcons.Folder, empty);
    }

    readonly Dictionary<string, bool> folderContentCache = new();

    bool FolderHasContent(string folderPath) {
        if (folderContentCache.TryGetValue(folderPath, out bool cached))
            return cached;
        bool has = false;
        try {
            var abs = AssetDatabase.Project.ResolveAbsolute(folderPath);
            has = Directory.EnumerateDirectories(abs).Any() ||
                  Directory.EnumerateFiles(abs).Any(f => !f.EndsWith(".meta", StringComparison.OrdinalIgnoreCase));
        }
        catch {
        }
        folderContentCache[folderPath] = has;
        return has;
    }

    static bool IsValidIdentifier(string s) {
        if (string.IsNullOrEmpty(s) || !(char.IsLetter(s[0]) || s[0] == '_'))
            return false;
        foreach (char c in s)
            if (!char.IsLetterOrDigit(c) && c != '_')
                return false;
        return true;
    }

    void CreateScriptNamed(string className) {
        var absolute = UniquePath(Path.Combine(
            AssetDatabase.Project.ResolveAbsolute(CurrentFolder), className + ".cs"));
        File.WriteAllText(absolute, ScriptTemplates.Behaviour(Path.GetFileNameWithoutExtension(absolute)));
        RequestScriptRebuild?.Invoke();
        AsyncAssetImport.Request("Creating script...");
    }

    void DrawNavigationBar(float s) {
        gui.BeginDisabled(CurrentFolder == "Assets");
        if (EditorIcons.GhostButton("navback", EditorIcons.Back, "Back", 30 * s)) {
            var slash = CurrentFolder.LastIndexOf('/');
            NavigateTo(slash > 0 ? CurrentFolder[..slash] : "Assets");
        }
        gui.EndDisabled();

        gui.SameLine();
        DrawBreadcrumb();

        float clusterW = 430 * s;
        float startX = MathF.Max(gui.CursorPosX + 8, gui.WindowWidth - clusterW);
        gui.SameLine(startX);
        if (EditorIcons.GhostButton("viewmode", listView ? EditorIcons.Grid : EditorIcons.More,
                listView ? "Switch to grid view" : "Switch to list view", 30 * s))
            listView = !listView;
        gui.SameLine(0, 4);
        if (EditorIcons.GhostButton("navrefresh", EditorIcons.Refresh, "Reimport changed assets"))
            AsyncAssetImport.Request("Refreshing assets...", onFinished: thumbnails.InvalidateAll);
        gui.SameLine(0, 4);
        gui.SetNextItemWidth(110 * s);
        gui.Combo("##typefilter", ref typeFilter, TypeFilterLabels);
        if (gui.IsItemHovered())
            gui.Tooltip("Filter by asset type (searches the whole project)");
        gui.SameLine(0, 4);
        gui.SetNextItemWidth(190 * s);
        gui.InputTextWithHint("##filter", $"{EditorIcons.Search} Search (t:Material)...", ref filter, 128);
    }

    bool revealPending;

    void RevealAsset(string assetPath) {
        if (string.IsNullOrEmpty(assetPath))
            return;
        int slash = assetPath.LastIndexOf('/');
        string folder = slash > 0 ? assetPath[..slash] : "Assets";
        if (!string.Equals(folder, CurrentFolder, StringComparison.OrdinalIgnoreCase))
            NavigateTo(folder);
        filter = "";
        if (AssetDatabase.TryGetGuid(assetPath, out Guid guid))
            state.SelectAsset(assetPath, guid);
    }

    void NavigateTo(string folderPath) {
        CurrentFolder = folderPath;
        folderContentCache.Clear();
        revealPending = true;
    }

    void DrawFavorites() {
        List<string> favs = EditorPrefs.Current.FavoriteFolders;
        if (favs.Count == 0)
            return;

        gui.PushColor(EditorStyleColor.Text, gui.StyleColor(EditorStyleColor.TextDisabled));
        gui.TextUnformatted($"{EditorIcons.Pin}  Favourites");
        gui.PopColor();

        string remove = null;
        for (var i = 0; i < favs.Count; i++) {
            string fav = favs[i];
            string name = fav == "Assets" ? "Assets" : fav[(fav.LastIndexOf('/') + 1)..];
            gui.PushId($"fav{i}");
            bool current = string.Equals(fav, CurrentFolder, StringComparison.OrdinalIgnoreCase);
            gui.PushColor(EditorStyleColor.Text, EditorTheme.FolderTint);
            gui.TextUnformatted(EditorIcons.Folder);
            gui.PopColor();
            gui.SameLine(0, 6);
            if (gui.Selectable($"  {name}##fav", current))
                NavigateTo(fav);
            if (gui.IsItemHovered())
                gui.Tooltip($"{fav}\nRight-click to remove from favourites.");
            if (gui.BeginPopupContextItem($"##favctx{i}")) {
                if (gui.MenuItem("Remove from Favourites")) remove = fav;
                gui.EndPopup();
            }
            gui.PopId();
        }
        if (remove is not null) {
            favs.Remove(remove);
            EditorPrefs.Save();
        }
        gui.Separator();
    }

    static bool IsFavorite(string folderPath) => EditorPrefs.Current.FavoriteFolders.Contains(folderPath);

    static void ToggleFavorite(string folderPath) {
        List<string> favs = EditorPrefs.Current.FavoriteFolders;
        if (!favs.Remove(folderPath))
            favs.Add(folderPath);
        EditorPrefs.Save();
    }

    void DrawFolderTree() {
        DrawFavorites();
        DrawFolderNode("Assets");
        revealPending = false;

        SysVec2 rest = gui.ContentRegionAvail;
        if (rest.Y > 4) {
            gui.Dummy(rest);
            AcceptAssetMoveDrop("Assets");
        }
    }

    void DrawFolderNode(string folderPath) {
        if (IsProtected(folderPath))
            return;

        var absolute = AssetDatabase.Project.ResolveAbsolute(folderPath);
        string[] subDirs;
        try {
            subDirs = Directory.GetDirectories(absolute)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch {
            return;
        }

        var name = folderPath == "Assets" ? "Assets" : folderPath[(folderPath.LastIndexOf('/') + 1)..];
        var isCurrent = string.Equals(folderPath, CurrentFolder, StringComparison.OrdinalIgnoreCase);
        var isAncestor = CurrentFolder.StartsWith(folderPath + "/", StringComparison.OrdinalIgnoreCase);

        var flags = EditorTreeFlags.OpenOnArrow | EditorTreeFlags.SpanAvailWidth;
        if (subDirs.Length == 0) flags |= EditorTreeFlags.Leaf | EditorTreeFlags.NoTreePushOnOpen;
        if (isCurrent) flags |= EditorTreeFlags.Selected;

        if (folderPath == "Assets")
            gui.SetNextItemOpenOnce(true);
        else if (isAncestor)
            if (revealPending) gui.SetNextItemOpenAlways(true); else gui.SetNextItemOpenOnce(true);

        bool open = gui.TreeNodeEx($"      {name}##tn{folderPath}", flags);
        SysVec2 rowMin = gui.ItemRectMin;

        if (gui.IsItemClicked() && !gui.IsItemToggledOpen())
            CurrentFolder = folderPath;

        AcceptAssetMoveDrop(folderPath);

        var expanded = open && subDirs.Length > 0;
        var tint = isCurrent || isAncestor
            ? EditorTheme.FolderTint
            : EditorTheme.FolderTintDim;
        EditorIcons.DrawAt(new SysVec2(rowMin.X + gui.TreeNodeToLabelSpacing, rowMin.Y),
            expanded ? EditorIcons.FolderOpen : EditorIcons.Folder, tint);

        if (expanded) {
            foreach (var dir in subDirs)
                DrawFolderNode(folderPath + "/" + Path.GetFileName(dir));
            gui.TreePop();
        }
    }

    void DrawTreeSplitter(float s) {
        gui.SameLine(0, 0);
        gui.Input.InvisibleButton("##treesplitter", new SysVec2(6 * s, gui.ContentRegionAvail.Y));
        if (gui.IsItemHovered() || gui.IsItemActive())
            gui.SetMouseCursorResizeEW();
        if (gui.IsItemActive())
            EditorPrefs.Current.AssetTreeWidth =
                Math.Clamp(EditorPrefs.Current.AssetTreeWidth + gui.Input.MouseDelta.X / s, 140f, 420f);
        if (gui.IsItemDeactivated())
            EditorPrefs.Save();

        SysVec2 min = gui.ItemRectMin;
        SysVec2 max = gui.ItemRectMax;
        float x = (min.X + max.X) * 0.5f;
        gui.WindowDrawList.AddLine(new SysVec2(x, min.Y), new SysVec2(x, max.Y),
            gui.ColorU32(gui.StyleColor(gui.IsItemActive() ? EditorStyleColor.CheckMark : EditorStyleColor.Border)));
        gui.SameLine(0, 0);
    }

    void AcceptAssetMoveDrop(string targetFolder) {
        if (!gui.BeginDragDropTarget())
            return;

        string text = gui.AcceptDragDropPayloadString(DragType);
        if (text is not null) {
            var guids = new List<Guid>();
            foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
                if (Guid.TryParse(part, out Guid guid))
                    guids.Add(guid);
            MoveAssets(guids, targetFolder);
        }

        if (gui.AcceptDragDropPayloadInt("BALLISTIC_ENTITY") is { } hash) {
            Entity entity = FindEntityByHash(hash);
            if (entity is not null)
                CreatePrefabFromEntity(entity, targetFolder);
        }
        gui.EndDragDropTarget();
    }

    static Entity FindEntityByHash(int hash) {
        Scene scene = SceneManager.GetCurrentScene();
        if (scene is null) return null;
        foreach (Entity e in scene.Entities)
            if (e.InstanceId.GetHashCode() == hash) return e;
        return null;
    }

    void CreatePrefabFromEntity(Entity entity, string targetFolder) {
        if (AssetDatabase.Project is null) return;
        string baseName = string.IsNullOrEmpty(entity.Name) ? "Prefab" : entity.Name;
        string dir = AssetDatabase.Project.ResolveAbsolute(targetFolder);
        Directory.CreateDirectory(dir);

        string relPath = $"{targetFolder}/{baseName}.prefab";
        string abs = Path.Combine(dir, baseName + ".prefab");
        for (int i = 1; File.Exists(abs); i++) {
            relPath = $"{targetFolder}/{baseName} {i}.prefab";
            abs = Path.Combine(dir, $"{baseName} {i}.prefab");
        }

        try {
            File.WriteAllText(abs, PrefabAsset.FromEntity(entity).ToYaml());
            EditorUndo.Push("Create Prefab");
            AsyncAssetImport.Request("Creating prefab...", onFinished: () => {
                if (AssetDatabase.TryGetGuid(relPath, out Guid guid)) {
                    entity.PrefabSource = guid;
                    state.MarkViewportDirty();
                }
            });
        }
        catch (Exception e) {
            Debugging.LogError($"Could not create prefab: {e.Message}");
        }
    }

    void MoveAssets(List<Guid> guids, string targetFolder) {
        if (IsProtected(targetFolder)) {
            Debugging.LogWarning("The Default folder is read-only; assets can't be moved into it.");
            return;
        }
        var targetAbsolute = AssetDatabase.Project.ResolveAbsolute(targetFolder);
        var moved = 0;
        foreach (Guid guid in guids) {
            var assetPath = AssetDatabase.GuidToAssetPath(guid);
            if (assetPath is null || IsProtected(assetPath)) continue;

            var sourceAbsolute = AssetDatabase.Project.ResolveAbsolute(assetPath);
            var destination = Path.Combine(targetAbsolute, Path.GetFileName(sourceAbsolute));
            if (string.Equals(sourceAbsolute, destination, StringComparison.OrdinalIgnoreCase))
                continue;
            if (File.Exists(destination)) {
                Debugging.LogWarning($"Move: '{Path.GetFileName(destination)}' already exists in {targetFolder}; skipped.");
                continue;
            }

            try {
                File.Move(sourceAbsolute, destination);
                var sourceMeta = sourceAbsolute + ".meta";
                if (File.Exists(sourceMeta))
                    File.Move(sourceMeta, destination + ".meta");
                moved++;
            }
            catch (Exception exception) {
                Debugging.LogError($"Move failed for '{assetPath}': {exception.Message}");
            }
        }

        if (moved == 0)
            return;

        state.ClearAssetSelection();
        AsyncAssetImport.Request(moved == 1 ? "Moving asset..." : $"Moving {moved} assets...",
            onFinished: thumbnails.InvalidateAll);
    }

    void DeleteFolder(string folderPath) {
        try {
            Directory.Delete(AssetDatabase.Project.ResolveAbsolute(folderPath), recursive: true);
        }
        catch (Exception e) {
            Debugging.LogError($"Could not delete folder '{folderPath}': {e.Message}");
            return;
        }

        bool inside = string.Equals(CurrentFolder, folderPath, StringComparison.OrdinalIgnoreCase) ||
                      CurrentFolder.StartsWith(folderPath + "/", StringComparison.OrdinalIgnoreCase);
        if (inside) {
            int slash = folderPath.LastIndexOf('/');
            NavigateTo(slash > 0 ? folderPath[..slash] : "Assets");
            state.ClearAssetSelection();
        }
        AsyncAssetImport.Request("Updating assets...", onFinished: thumbnails.InvalidateAll);
    }

    void DrawBreadcrumb() {
        var segments = CurrentFolder.Split('/');
        gui.PushColor(EditorStyleColor.Button, new SysVec4(0, 0, 0, 0));
        gui.PushColor(EditorStyleColor.ButtonHovered, new SysVec4(1, 1, 1, 0.08f));
        gui.AlignTextToFramePadding();

        var cumulative = "";
        for (var i = 0; i < segments.Length; i++) {
            cumulative = i == 0 ? segments[0] : cumulative + "/" + segments[i];
            if (i > 0) {
                gui.SameLine(0, 2);
                gui.TextDisabled(EditorIcons.ChevronRight);
                gui.SameLine(0, 2);
            }
            var target = cumulative;
            var last = i == segments.Length - 1;
            if (!last)
                gui.PushColor(EditorStyleColor.Text, gui.StyleColor(EditorStyleColor.TextDisabled));
            if (gui.Button($"{segments[i]}##crumb{i}"))
                NavigateTo(target);
            if (!last)
                gui.PopColor();
        }

        gui.PopColor(2);
    }

    float tileScale = 1f;
    bool gridHovered;
    bool listView;
    int sortColumn;
    bool sortAscending = true;

    void DrawGrid(List<string> folders, List<(string path, Guid guid)> files, float s) {
        if (gridHovered && gui.KeyCtrl && gui.Input.MouseWheel != 0)
            tileScale = Math.Clamp(tileScale + gui.Input.MouseWheel * 0.12f, 0.55f, 2.4f);

        float tile = 86 * s * tileScale;
        float cellW = tile + gui.ItemSpacing.X;
        var columns = Math.Max(1, (int)(gui.ContentRegionAvail.X / cellW));

        visibleFiles.Clear();
        visibleFiles.AddRange(files);

        if (listView) {
            DrawList(folders, files, s);
            return;
        }

        gui.BeginChild("##grid", default, border: false);
        gridHovered = gui.IsWindowHoveredAllowBlocked();
        var column = 0;

        foreach (var folder in folders) {
            DrawFolderTile(folder, tile);
            NextCell(ref column, columns);
        }

        for (var i = 0; i < files.Count; i++) {
            DrawAssetTile(files[i].path, files[i].guid, tile, i);
            NextCell(ref column, columns);
        }

        if (folders.Count == 0 && files.Count == 0)
            DrawEmptyState(s);

        if (gridHovered && gui.IsMouseClicked(0) &&
            !gui.IsAnyItemHovered() && renamingPath is null)
            state.ClearAssetSelection();

        if (gui.BeginPopupContextWindowEmpty("##gridctx")) {
            if (gui.MenuItem($"{EditorIcons.Add}  Paste", "Ctrl+V", false, ClipboardHasContent))
                ClipboardPaste();
            gui.Separator();
            if (gui.MenuItem($"{EditorIcons.Folder}  New Folder"))
                PromptNewAsset("Folder", "New Folder", "", null);
            if (gui.MenuItem($"{EditorIcons.Code}  New Script"))
                openNewScriptPrompt = true;
            if (gui.MenuItem($"{EditorIcons.Color}  New Material"))
                PromptNewAsset("Material", "New Material", ".mat", _ =>
                    "{\n  \"version\": 1,\n  \"shader\": \"Assets/Default/Shaders/Standard.shader\",\n  \"textures\": {}\n}\n");
            if (gui.MenuItem($"{EditorIcons.Home}  New Scene"))
                PromptNewAsset("Scene", "New Scene", ".scene", n =>
                    $"version: 1\nname: {n}\nentities: []\n");
            if (gui.MenuItem($"{EditorIcons.Grid}  New Terrain"))
                PromptNewAsset("Terrain", "New Terrain", ".terrain", _ =>
                    "{\n  \"version\": 1,\n  \"resolution\": 256,\n  \"sizeX\": 100,\n  \"sizeZ\": 100,\n  \"heightScale\": 20\n}\n");
            DrawDataAssetCreateMenu();
            gui.Separator();
            if (gui.MenuItem($"{EditorIcons.Code}  Open C# Project"))
                OpenCSharpProject();
            gui.MenuItemToggle("Show Source Files", ref showSourceFiles);
            if (gui.MenuItem("Show in Explorer"))
                ShowInExplorer(AssetDatabase.Project.ResolveAbsolute(CurrentFolder), select: false);
            if (gui.MenuItem("Refresh"))
                AsyncAssetImport.Request("Refreshing assets...", onFinished: thumbnails.InvalidateAll);
            if (gui.MenuItem("Force Reimport All"))
                AsyncAssetImport.Request("Reimporting all assets...",
                    onFinished: thumbnails.InvalidateAll, forceAll: true);
            if (gui.IsItemHovered())
                gui.Tooltip("Rebuilds every Library artifact from source (slow on big projects).\n" +
                                 "Assets already loaded in the open scene pick the rebuilt data up on the next scene load.");
            gui.EndPopup();
        }

        if (gui.IsWindowFocusedIncludingChildren() && renamingPath is null &&
            !gui.WantTextInput) {
            if (gui.KeyCtrl && gui.KeyPressed(EditorGuiKey.A) && visibleFiles.Count > 0)
                state.SelectAssets(visibleFiles, visibleFiles[^1]);
            if (gui.KeyPressed(EditorGuiKey.Delete) && state.SelectedAssets.Count > 0)
                AssetOps.DeleteAssets(state, state.SelectedAssets, thumbnails.InvalidateAll);
            if (gui.KeyCtrl && gui.KeyPressed(EditorGuiKey.C) && state.SelectedAssets.Count > 0) ClipboardCopy(cut: false);
            if (gui.KeyCtrl && gui.KeyPressed(EditorGuiKey.X) && state.SelectedAssets.Count > 0) ClipboardCopy(cut: true);
            if (gui.KeyCtrl && gui.KeyPressed(EditorGuiKey.V) && ClipboardHasContent) ClipboardPaste();
            if (gui.KeyCtrl && gui.KeyPressed(EditorGuiKey.D) && state.SelectedAssets.Count > 0) { ClipboardCopy(cut: false); ClipboardPaste(); }
        }

        gui.EndChild();
    }

    void DrawList(List<string> folders, List<(string path, Guid guid)> files, float s) {
        const EditorTableFlags flags = EditorTableFlags.Sortable | EditorTableFlags.Resizable |
            EditorTableFlags.RowBg | EditorTableFlags.ScrollY | EditorTableFlags.BordersInnerV |
            EditorTableFlags.SizingStretchProp;

        if (!gui.BeginTable("##assetlist", 4, flags))
            return;

        gui.TableSetupScrollFreeze(0, 1);
        gui.TableSetupColumn("Name", EditorColumnFlags.DefaultSort | EditorColumnFlags.WidthStretch, 3f);
        gui.TableSetupColumn("Type", EditorColumnFlags.WidthStretch, 1f);
        gui.TableSetupColumn("Size", EditorColumnFlags.WidthStretch, 1f);
        gui.TableSetupColumn("Modified", EditorColumnFlags.WidthStretch, 1.5f);
        gui.TableHeadersRow();

        if (gui.TableGetSortSpec(out int sc, out bool asc)) {
            sortColumn = sc;
            sortAscending = asc;
        }

        var rows = new List<(string path, Guid guid, bool isFolder, long size, DateTime modified)>();
        foreach (var folder in folders)
            rows.Add((folder, Guid.Empty, true, -1, DirModified(folder)));
        foreach ((string path, Guid guid) in files) {
            var fi = TryFileInfo(path);
            rows.Add((path, guid, false, fi?.Length ?? 0, fi?.LastWriteTime ?? DateTime.MinValue));
        }
        SortRows(rows);

        foreach (var row in rows) {
            gui.TableNextRow();
            gui.TableSetColumnIndex(0);
            gui.PushId(row.path);

            string name = Path.GetFileName(row.path);
            string ext = row.isFolder ? "" : Path.GetExtension(row.path).ToLowerInvariant();
            (string icon, SysVec4 tint) = row.isFolder
                ? FolderIcon(row.path)
                : EditorIcons.ForAssetExtension(ext);

            bool selected = !row.isFolder && state.IsAssetSelected(row.guid);
            gui.PushColor(EditorStyleColor.Text, tint);
            gui.TextUnformatted(icon);
            gui.PopColor();
            gui.SameLine(0, 6);

            int index = row.isFolder ? -1 : visibleFiles.FindIndex(f => f.guid == row.guid);
            if (gui.SelectableRow($"{name}##row", selected)) {
                if (row.isFolder) {
                    if (gui.IsMouseDoubleClicked(0)) NavigateTo(row.path);
                }
                else if (gui.IsMouseDoubleClicked(0)) OpenAsset(row.path, ext);
                else HandleTileClick(row.path, row.guid, Math.Max(0, index));
            }
            if (!row.isFolder) {
                DrawTileContextMenu(row.path, row.guid, ext);
                BeginAssetDragSource(name, row.guid);
            }

            gui.TableSetColumnIndex(1);
            gui.TextDisabled(row.isFolder ? "Folder" : Style(ext).Item1);
            gui.TableSetColumnIndex(2);
            gui.TextDisabled(row.isFolder ? "" : HumanSize(row.size));
            gui.TableSetColumnIndex(3);
            gui.TextDisabled(row.modified == DateTime.MinValue ? "" : row.modified.ToString("yyyy-MM-dd HH:mm"));

            gui.PopId();
        }

        gui.EndTable();
    }

    void SortRows(List<(string path, Guid guid, bool isFolder, long size, DateTime modified)> rows) {
        rows.Sort((a, b) => {
            if (a.isFolder != b.isFolder) return a.isFolder ? -1 : 1;
            int c = sortColumn switch {
                1 => string.Compare(Path.GetExtension(a.path), Path.GetExtension(b.path), StringComparison.OrdinalIgnoreCase),
                2 => a.size.CompareTo(b.size),
                3 => a.modified.CompareTo(b.modified),
                _ => string.Compare(Path.GetFileName(a.path), Path.GetFileName(b.path), StringComparison.OrdinalIgnoreCase),
            };
            return sortAscending ? c : -c;
        });
    }

    FileInfo TryFileInfo(string assetPath) {
        try {
            var fi = new FileInfo(AssetDatabase.Project.ResolveAbsolute(assetPath));
            return fi.Exists ? fi : null;
        }
        catch { return null; }
    }

    DateTime DirModified(string assetPath) {
        try { return Directory.GetLastWriteTime(AssetDatabase.Project.ResolveAbsolute(assetPath)); }
        catch { return DateTime.MinValue; }
    }

    static string HumanSize(long bytes) {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):0.#} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):0.##} GB";
    }

    unsafe void DrawEmptyState(float s) {
        gui.Dummy(new SysVec2(0, gui.ContentRegionAvail.Y * 0.28f));
        if (ImGuiController.HasIcons) {
            float iconSize = 40 * s;
            gui.CursorPosX = ((gui.WindowWidth - iconSize) * 0.5f);
            gui.WindowDrawList.AddText(EditorFont.LargeIcons, iconSize,
                gui.CursorScreenPos, gui.ColorU32(new SysVec4(1, 1, 1, 0.07f)),
                filter.Length > 0 ? EditorIcons.Search : EditorIcons.FolderOpen);
            gui.Dummy(new SysVec2(iconSize, iconSize));
            gui.Spacing();
        }
        CenteredDisabledText(filter.Length > 0 ? "No assets match." : "Empty folder");
        if (filter.Length == 0)
            CenteredDisabledText("Drop files here to import them.");
    }

    static void CenteredDisabledText(string text) {
        float w = gui.CalcTextSize(text).X;
        gui.CursorPosX = (Math.Max(0, (gui.WindowWidth - w) * 0.5f));
        gui.TextDisabled(text);
    }

    void BeginRename(string path) {
        if (IsProtected(path)) {
            Debugging.LogWarning("The Default folder is read-only and can't be renamed.");
            return;
        }
        renamingPath = path;
        renameBuffer = Path.GetFileName(path);
        renameFocusPending = true;
    }

    bool DrawRenameField(string path, float tile, bool isFolder) {
        if (renamingPath != path)
            return false;

        gui.SetNextItemWidth(tile);
        if (renameFocusPending) { gui.SetKeyboardFocusHere(); renameFocusPending = false; }
        gui.InputText("##renamefield", ref renameBuffer, 128);

        var enter = gui.IsItemDeactivatedAfterEdit() || gui.KeyPressed(EditorGuiKey.Enter);
        if (enter || gui.IsItemDeactivated()) {
            if (enter && !string.IsNullOrWhiteSpace(renameBuffer))
                CommitRename(path, renameBuffer, isFolder);
            renamingPath = null;
        }
        return true;
    }

    void CommitRename(string path, string newName, bool isFolder) {
        var oldAbsolute = AssetDatabase.Project.ResolveAbsolute(path);
        var parent = Path.GetDirectoryName(oldAbsolute)!;
        if (!isFolder) {
            string oldExt = Path.GetExtension(oldAbsolute);
            if (oldExt.Length > 0 && Path.GetExtension(newName).Length == 0)
                newName += oldExt;
        }
        var newAbsolute = Path.Combine(parent, newName);
        if (string.Equals(oldAbsolute, newAbsolute, StringComparison.OrdinalIgnoreCase))
            return;
        if (File.Exists(newAbsolute) || Directory.Exists(newAbsolute)) {
            Debugging.LogWarning($"Rename: '{newName}' already exists.");
            return;
        }

        try {
            if (isFolder) {
                Directory.Move(oldAbsolute, newAbsolute);
            }
            else {
                File.Move(oldAbsolute, newAbsolute);
                var oldMeta = oldAbsolute + ".meta";
                if (File.Exists(oldMeta))
                    File.Move(oldMeta, newAbsolute + ".meta");
            }
        }
        catch (Exception exception) {
            Debugging.LogError($"Rename failed: {exception.Message}");
            return;
        }

        if (!isFolder && Path.GetExtension(newAbsolute).Equals(".cs", StringComparison.OrdinalIgnoreCase)) {
            ScriptTemplates.RewriteIfPristine(
                Path.GetFileNameWithoutExtension(oldAbsolute),
                Path.GetFileNameWithoutExtension(newAbsolute), newAbsolute);
            RequestScriptRebuild?.Invoke();
        }

        state.ClearAssetSelection();
        AsyncAssetImport.Request("Updating assets...", onFinished: thumbnails.InvalidateAll);
    }

    void CreateFolder() {
        var absolute = UniquePath(Path.Combine(AssetDatabase.Project.ResolveAbsolute(CurrentFolder), "New Folder"));
        Directory.CreateDirectory(absolute);
    }

    void CreateScript() {
        var absolute = UniquePath(Path.Combine(
            AssetDatabase.Project.ResolveAbsolute(CurrentFolder), "NewScript.cs"));
        File.WriteAllText(absolute, ScriptTemplates.Behaviour(Path.GetFileNameWithoutExtension(absolute)));
        RequestScriptRebuild?.Invoke();
        AsyncAssetImport.Request("Creating script...");
    }

    void CreateMaterial() {
        var absolute = UniquePath(Path.Combine(
            AssetDatabase.Project.ResolveAbsolute(CurrentFolder), "New Material.mat"));
        File.WriteAllText(absolute,
            "{\n  \"version\": 1,\n  \"shader\": \"Assets/Default/Shaders/Standard.shader\",\n  \"textures\": {}\n}\n");
        AsyncAssetImport.Request("Creating material...");
    }

    void CreateScene() {
        var absolute = UniquePath(Path.Combine(
            AssetDatabase.Project.ResolveAbsolute(CurrentFolder), "New Scene.scene"));
        File.WriteAllText(absolute,
            $"version: 1\nname: {Path.GetFileNameWithoutExtension(absolute)}\nentities: []\n");
        AsyncAssetImport.Request("Creating scene...");
    }

    void CreateTerrain() {
        var absolute = UniquePath(Path.Combine(
            AssetDatabase.Project.ResolveAbsolute(CurrentFolder), "New Terrain.terrain"));
        File.WriteAllText(absolute,
            "{\n  \"version\": 1,\n  \"resolution\": 256,\n  \"sizeX\": 100,\n  \"sizeZ\": 100,\n  \"heightScale\": 20\n}\n");
        AsyncAssetImport.Request("Creating terrain...");
    }

    void DrawDataAssetCreateMenu() {
        var entries = ComponentRegistry.DataAssetMenu;
        if (entries.Count == 0)
            return;
        gui.Separator();
        foreach (ComponentEntry entry in entries) {
            if (string.IsNullOrEmpty(entry.Menu)) {
                if (gui.MenuItem($"{EditorIcons.Settings}  {entry.DisplayName}"))
                    CreateDataAsset(entry.Type);
            }
            else if (gui.BeginMenu($"{EditorIcons.Settings}  {entry.Menu}")) {
                if (gui.MenuItem(entry.DisplayName))
                    CreateDataAsset(entry.Type);
                gui.EndMenu();
            }
        }
    }

    void CreateDataAsset(Type type) {
        DataAsset instance = DataAsset.CreateInstance(type);
        if (instance is null)
            return;

        var attr = type.GetCustomAttribute<CreateDataAssetAttribute>();
        string fileName = string.IsNullOrEmpty(attr?.FileName) ? type.Name : attr.FileName;
        string serialized = DataAssetSerializer.Serialize(instance);
        PromptNewAsset(attr?.DisplayName ?? type.Name, fileName, ".asset", _ => serialized);
    }

    static string UniquePath(string path) {
        if (!File.Exists(path) && !Directory.Exists(path))
            return path;

        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 1; ; i++) {
            var candidate = Path.Combine(dir, $"{stem} {i}{ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
                return candidate;
        }
    }

    static void ShowInExplorer(string absolutePath, bool select) {
        System.Diagnostics.Process.Start("explorer.exe",
            select ? $"/select,\"{absolutePath}\"" : $"\"{absolutePath}\"");
    }

    static void OpenScript(string path) {
        OpenCSharpProject();
        OpenInDefaultEditor(path);
    }

    static void OpenCSharpProject() {
        try {
            var csproj = BallisticEngine.AssetPipeline.GameScripts.EnsureProjectFile(AssetDatabase.Project);
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(csproj) { UseShellExecute = true });
        }
        catch (Exception exception) {
            Debugging.LogWarning($"Open C# project: {exception.Message}");
        }
    }

    static void OpenInDefaultEditor(string path) {
        var absolute = AssetDatabase.Project.ResolveAbsolute(path);
        try {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(absolute) { UseShellExecute = true });
        }
        catch (Exception exception) {
            Debugging.LogWarning($"Open script: {exception.Message}");
        }
    }

    static void NextCell(ref int column, int columns) {
        column++;
        if (column >= columns)
            column = 0;
        else
            gui.SameLine();
    }

    void DrawFolderTile(string folderPath, float tile) {
        var name = folderPath[(folderPath.LastIndexOf('/') + 1)..];

        gui.PushId(folderPath);
        gui.BeginGroup();

        gui.PushFrameRounding(6f);
        gui.PushColor(EditorStyleColor.Button, new SysVec4(1, 1, 1, 0.025f));
        gui.PushColor(EditorStyleColor.ButtonHovered, new SysVec4(1, 1, 1, 0.07f));
        gui.PushColor(EditorStyleColor.ButtonActive, new SysVec4(1, 1, 1, 0.11f));
        gui.Button("##folder", new SysVec2(tile, tile));
        gui.PopColor(3);
        gui.PopStyleVar();

        (string fIcon, SysVec4 fTint) = FolderIcon(folderPath);
        DrawFolderGlyph(gui.ItemRectMin, gui.ItemRectMax, fIcon, fTint);

        if (gui.IsItemHovered() && gui.IsMouseDoubleClicked(0))
            NavigateTo(folderPath);

        AcceptAssetMoveDrop(folderPath);

        if (gui.BeginPopupContextItem("##folderctx")) {
            if (gui.MenuItem("Open"))
                NavigateTo(folderPath);
            if (gui.MenuItem(IsFavorite(folderPath) ? $"{EditorIcons.Pin}  Remove from Favourites" : $"{EditorIcons.Pin}  Add to Favourites"))
                ToggleFavorite(folderPath);
            if (gui.MenuItem("Rename"))
                BeginRename(folderPath);
            if (gui.MenuItem("Show in Explorer"))
                ShowInExplorer(AssetDatabase.Project.ResolveAbsolute(folderPath), select: false);
            gui.Separator();
            if (gui.MenuItem("Delete Folder"))
                DeleteFolder(folderPath);
            gui.EndPopup();
        }

        if (!DrawRenameField(folderPath, tile, isFolder: true))
            TileLabel(name, tile);
        gui.EndGroup();
        gui.PopId();
    }

    void DrawAssetTile(string path, Guid guid, float tile, int index) {
        var name = Path.GetFileName(path);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        (string tag, SysVec4 color) = Style(ext);

        var selected = state.IsAssetSelected(guid);
        var active = state.SelectedAssetGuid == guid;

        gui.PushId(path);
        gui.BeginGroup();
        gui.PushFrameRounding(6f);

        bool cutGhost = clipboardCut && clipboardPaths.Contains(path);
        if (cutGhost)
            gui.PushAlphaScaled(0.45f);

        bool clicked;
        var hasPreview = ImageExtensions.Contains(ext) ||
                         ext is ".fbx" or ".obj" or ".gltf" or ".glb" or ".dae" or ".mat";
        var thumb = hasPreview ? thumbnails.Get(guid, path) : 0;
        if (thumb != 0) {
            clicked = gui.ImageButton($"##thumb{guid}", thumb, new SysVec2(tile - 8, tile - 8));
        }
        else {
            gui.PushColor(EditorStyleColor.Button, new SysVec4(color.X, color.Y, color.Z, 0.55f));
            gui.PushColor(EditorStyleColor.ButtonHovered, new SysVec4(color.X, color.Y, color.Z, 0.75f));
            gui.PushColor(EditorStyleColor.ButtonActive, new SysVec4(color.X, color.Y, color.Z, 0.95f));
            clicked = gui.Button("##typetile", new SysVec2(tile, tile));
            gui.PopColor(3);
            DrawTypeGlyph(gui.ItemRectMin, gui.ItemRectMax, ext, tag);
        }
        gui.PopStyleVar();

        SysVec2 tileMin = gui.ItemRectMin;
        SysVec2 tileMax = gui.ItemRectMax;

        if (selected) {
            SysVec4 accent = gui.StyleColor(EditorStyleColor.CheckMark);
            gui.WindowDrawList.AddRect(tileMin, tileMax,
                gui.ColorU32(active ? accent : new SysVec4(accent.X, accent.Y, accent.Z, 0.55f)),
                6f, active ? 2.5f : 2f);
        }

        if (thumb != 0 && tile > 64)
            DrawExtensionBadge(tileMin, tileMax, tag, color);

        if (clicked)
            HandleTileClick(path, guid, index);

        DrawTileContextMenu(path, guid, ext);

        if (gui.IsItemHovered()) {
            if (gui.IsMouseDoubleClicked(0))
                OpenAsset(path, ext);
            if (state.SelectedAssets.Count <= 1)
                gui.Tooltip(path);
        }

        BeginAssetDragSource(name, guid);
        if (!DrawRenameField(path, tile, isFolder: false))
            TileLabel(name, tile);
        if (cutGhost)
            gui.PopStyleVar();
        gui.EndGroup();
        gui.PopId();
    }

    void OpenAsset(string path, string ext) {
        switch (ext) {
            case ".scene": LoadScene(path); break;
            case ".cs": OpenScript(path); break;
            case ".prefab": InstantiatePrefab(path); break;
        }
    }

    void HandleTileClick(string path, Guid guid, int index) {
        

        if (gui.KeyShift && anchorGuid != Guid.Empty) {
            var anchorIndex = visibleFiles.FindIndex(f => f.guid == anchorGuid);
            if (anchorIndex >= 0) {
                int from = Math.Min(anchorIndex, index), to = Math.Max(anchorIndex, index);
                state.SelectAssets(visibleFiles.GetRange(from, to - from + 1), (path, guid));
                return;
            }
        }

        if (gui.KeyCtrl) {
            state.ToggleAsset(path, guid);
            anchorGuid = guid;
            return;
        }

        state.SelectAsset(path, guid);
        anchorGuid = guid;
    }

    void DrawTileContextMenu(string path, Guid guid, string ext) {
        if (!gui.BeginPopupContextItem("##assetctx"))
            return;

        if (!state.IsAssetSelected(guid)) {
            state.SelectAsset(path, guid);
            anchorGuid = guid;
        }

        int count = state.SelectedAssets.Count;
        if (count > 1) {
            gui.TextDisabled($"{count} assets selected");
            gui.Separator();
            if (gui.MenuItem($"{EditorIcons.Document}  Copy", "Ctrl+C")) ClipboardCopy(cut: false);
            if (gui.MenuItem("Cut", "Ctrl+X")) ClipboardCopy(cut: true);
            if (gui.MenuItem($"{EditorIcons.Add}  Paste", "Ctrl+V", false, ClipboardHasContent)) ClipboardPaste();
            if (gui.MenuItem("Copy Paths"))
                gui.SetClipboardText(string.Join('\n', state.SelectedAssets.Select(a => a.Path)));
            gui.Separator();
            if (gui.MenuItem($"{EditorIcons.Delete}  Delete {count} Assets"))
                AssetOps.DeleteAssets(state, state.SelectedAssets, thumbnails.InvalidateAll);
        }
        else {
            if (ext == ".scene" && gui.MenuItem($"{EditorIcons.Play}  Open Scene"))
                LoadScene(path);
            if (ext == ".cs" && gui.MenuItem($"{EditorIcons.Code}  Edit Script"))
                OpenScript(path);
            if (ModelInstantiation.IsModel(guid) && gui.MenuItem($"{EditorIcons.Add}  Add to Scene")) {
                EditorUndo.Push("Add Model");
                Entity entity = ModelInstantiation.Instantiate(SceneManager.GetCurrentScene(), guid);
                if (entity is not null)
                    state.Select(entity);
            }
            gui.Separator();
            if (gui.MenuItem($"{EditorIcons.Document}  Copy", "Ctrl+C")) ClipboardCopy(cut: false);
            if (gui.MenuItem("Cut", "Ctrl+X")) ClipboardCopy(cut: true);
            if (gui.MenuItem($"{EditorIcons.Add}  Paste", "Ctrl+V", false, ClipboardHasContent)) ClipboardPaste();
            if (gui.MenuItem("Duplicate", "Ctrl+D")) { ClipboardCopy(cut: false); ClipboardPaste(); }
            gui.Separator();
            if (gui.MenuItem("Rename"))
                BeginRename(path);
            if (gui.MenuItem("Show in Explorer"))
                ShowInExplorer(AssetDatabase.Project.ResolveAbsolute(path), select: true);
            if (gui.MenuItem("Copy Path"))
                gui.SetClipboardText(path);
            gui.Separator();
            if (gui.MenuItem($"{EditorIcons.Delete}  Delete"))
                AssetOps.DeleteAssets(state, [(path, guid)], thumbnails.InvalidateAll);
        }

        gui.EndPopup();
    }

    static readonly List<string> clipboardPaths = new();
    static bool clipboardCut;

    static bool ClipboardHasContent => clipboardPaths.Count > 0;

    void ClipboardCopy(bool cut) {
        clipboardPaths.Clear();
        foreach (var (p, _) in state.SelectedAssets) {
            if (cut && IsProtected(p)) continue;
            clipboardPaths.Add(p);
        }
        clipboardCut = cut;
    }

    void ClipboardPaste() {
        if (clipboardPaths.Count == 0) return;
        if (IsProtected(CurrentFolder)) {
            Debugging.LogWarning("The Default folder is read-only; can't paste into it.");
            return;
        }
        string destDir = AssetDatabase.Project.ResolveAbsolute(CurrentFolder);
        var pasted = false;
        foreach (string srcRel in clipboardPaths.ToArray()) {
            string srcAbs = AssetDatabase.Project.ResolveAbsolute(srcRel);
            if (!File.Exists(srcAbs)) continue;
            string destAbs = UniquePath(Path.Combine(destDir, Path.GetFileName(srcAbs)));
            try {
                if (clipboardCut) {
                    File.Move(srcAbs, destAbs);
                    string srcMeta = srcAbs + ".meta";
                    if (File.Exists(srcMeta)) File.Move(srcMeta, destAbs + ".meta");
                }
                else {
                    File.Copy(srcAbs, destAbs);
                }
                pasted = true;
            }
            catch (Exception e) {
                Debugging.LogError($"Paste failed for '{srcRel}': {e.Message}");
            }
        }
        if (clipboardCut) { clipboardPaths.Clear(); clipboardCut = false; }
        if (pasted) AsyncAssetImport.Request("Pasting assets...", onFinished: thumbnails.InvalidateAll);
    }

    static void TileLabel(string name, float tile) {
        SysVec2 pos = gui.CursorScreenPos;
        float height = gui.TextLineHeight;
        SysVec2 textSize = gui.CalcTextSize(name);
        float x = textSize.X < tile ? pos.X + (tile - textSize.X) * 0.5f : pos.X;

        IEditorDrawList draw = gui.WindowDrawList;
        draw.PushClipRect(pos, new SysVec2(pos.X + tile, pos.Y + height), true);
        draw.AddText(new SysVec2(x, pos.Y), gui.ColorU32(gui.StyleColor(EditorStyleColor.Text)), name);
        draw.PopClipRect();

        gui.Dummy(new SysVec2(tile, height));
    }

    static unsafe void DrawFolderGlyph(SysVec2 min, SysVec2 max, string icon, SysVec4 tint) {
        IEditorDrawList draw = gui.WindowDrawList;
        SysVec2 size = max - min;

        if (ImGuiController.HasIcons) {
            float glyph = size.Y * 0.52f;
            draw.AddText(EditorFont.LargeIcons, glyph,
                new SysVec2(min.X + (size.X - glyph) * 0.5f, min.Y + (size.Y - glyph) * 0.48f),
                gui.ColorU32(tint), icon);
            return;
        }

        var bodyColor = gui.ColorU32(new SysVec4(0.78f, 0.63f, 0.27f, tint.W));
        var tabColor = gui.ColorU32(new SysVec4(0.88f, 0.74f, 0.38f, tint.W));
        draw.AddRectFilled(min + size * new SysVec2(0.18f, 0.24f), min + size * new SysVec2(0.48f, 0.36f), tabColor, 3f);
        draw.AddRectFilled(min + size * new SysVec2(0.18f, 0.32f), min + size * new SysVec2(0.82f, 0.78f), bodyColor, 3f);
    }

    static unsafe void DrawTypeGlyph(SysVec2 min, SysVec2 max, string ext, string tag) {
        IEditorDrawList draw = gui.WindowDrawList;
        SysVec2 size = max - min;

        if (ImGuiController.HasIcons) {
            (string icon, _) = EditorIcons.ForAssetExtension(ext);
            float glyph = size.Y * 0.42f;
            draw.AddText(EditorFont.LargeIcons, glyph,
                new SysVec2(min.X + (size.X - glyph) * 0.5f, min.Y + size.Y * 0.18f),
                gui.ColorU32(new SysVec4(1, 1, 1, 0.85f)), icon);
        }

        SysVec2 tagSize = gui.CalcTextSize(tag);
        draw.AddText(new SysVec2(min.X + (size.X - tagSize.X) * 0.5f, max.Y - size.Y * 0.16f - tagSize.Y * 0.5f),
            gui.ColorU32(new SysVec4(1, 1, 1, 0.55f)), tag);
    }

    static void DrawExtensionBadge(SysVec2 min, SysVec2 max, string tag, SysVec4 color) {
        IEditorDrawList draw = gui.WindowDrawList;
        SysVec2 textSize = gui.CalcTextSize(tag);
        SysVec2 pad = new(5, 2);
        SysVec2 badgeMin = new(min.X + 5, max.Y - textSize.Y - pad.Y * 2 - 5);
        SysVec2 badgeMax = badgeMin + textSize + pad * 2;

        draw.AddRectFilled(badgeMin, badgeMax,
            gui.ColorU32(new SysVec4(color.X * 0.6f, color.Y * 0.6f, color.Z * 0.6f, 0.92f)), 4f);
        draw.AddText(badgeMin + pad, gui.ColorU32(new SysVec4(1, 1, 1, 0.92f)), tag);
    }

    static (string, SysVec4) Style(string ext) => ext switch {
        ".fbx" or ".obj" or ".gltf" or ".glb" => ("MESH", new SysVec4(0.20f, 0.30f, 0.42f, 1f)),
        ".png" or ".jpg" or ".jpeg" or ".tga" or ".bmp" => ("TEX", new SysVec4(0.18f, 0.34f, 0.25f, 1f)),
        ".hdr" or ".exr" => ("HDR", new SysVec4(0.36f, 0.31f, 0.16f, 1f)),
        ".wav" or ".wave" or ".ogg" => ("AUDIO", new SysVec4(0.36f, 0.18f, 0.28f, 1f)),
        ".mat" => ("MAT", new SysVec4(0.33f, 0.21f, 0.36f, 1f)),
        ".volume" => ("VOL", new SysVec4(0.36f, 0.22f, 0.32f, 1f)),
        ".scene" => ("SCENE", new SysVec4(0.38f, 0.25f, 0.15f, 1f)),
        ".pyscene" => ("PYS", new SysVec4(0.30f, 0.19f, 0.12f, 1f)),
        ".shader" or ".glsl" => ("GLSL", new SysVec4(0.15f, 0.32f, 0.35f, 1f)),
        ".cs" => ("C#", new SysVec4(0.27f, 0.23f, 0.40f, 1f)),
        ".cubemap" => ("SKY", new SysVec4(0.20f, 0.30f, 0.38f, 1f)),
        _ => ("FILE", new SysVec4(0.25f, 0.25f, 0.27f, 1f)),
    };

    unsafe void BeginAssetDragSource(string fileName, Guid guid) {
        if (!gui.BeginDragDropSource())
            return;

        var dragAll = state.IsAssetSelected(guid) && state.SelectedAssets.Count > 1;
        var payloadText = dragAll
            ? string.Join(';', state.SelectedAssets.Select(a => a.Guid.ToString("N")))
            : guid.ToString("N");

        gui.SetDragDropPayloadString(DragType, payloadText);

        gui.Text(dragAll ? $"{EditorIcons.Document} {state.SelectedAssets.Count} assets" : fileName);
        gui.EndDragDropSource();
    }

    static void LoadScene(string assetPath) => SceneCommands.Open(assetPath);

    void InstantiatePrefab(string assetPath) {
        PrefabAsset prefab = AssetDatabase.Load<PrefabAsset>(assetPath);
        if (prefab is null)
            return;
        EditorUndo.Push("Instantiate Prefab");
        Entity root = prefab.Instantiate();
        if (root is not null)
            state.Select(root);
        state.MarkViewportDirty();
    }
}
