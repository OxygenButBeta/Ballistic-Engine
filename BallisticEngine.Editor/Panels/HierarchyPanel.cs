using SysVec2 = System.Numerics.Vector2;
using SysVec4 = System.Numerics.Vector4;

namespace BallisticEngine.Editor;

internal sealed class HierarchyPanel : EditorWindow {
    static IEditorGui gui => EditorGui.Shared;

    readonly EditorState state;

    int renamingId = -1;
    string renameBuffer = "";
    bool renameFocusPending;

    string search = "";

    readonly Dictionary<int, bool> openState = new();

    enum ExpandForce { None, CollapseAll, ExpandAll }

    ExpandForce pendingForce = ExpandForce.None;

    public Func<string> CurrentAssetFolder;

    public HierarchyPanel(EditorState state) {
        DockKey = EditorLayout.Entities;
        Title = "Entities";
        Icon = EditorIcons.Package;
        Singleton = false;
        this.state = state;
    }

    protected override void OnGui(IEditorGui gui) => DrawEntities();

    public void DrawEntitiesContents() => DrawEntities();

    void DrawEntities() {
        Scene scene = SceneManager.GetCurrentScene();

        if (EditorIcons.GhostButton("hieradd", EditorIcons.Add, "Create entity"))
            gui.OpenPopup("##hiercreate");
        if (gui.BeginPopup("##hiercreate")) {
            DrawCreateMenu(scene);
            gui.EndPopup();
        }

        gui.SameLine(0, 2);
        gui.BeginDisabled(state.Selected is null);
        if (EditorIcons.GhostButton("hierdel", EditorIcons.Delete, "Delete selected (Del)"))
            DeleteSelected(scene);
        gui.EndDisabled();

        gui.SameLine(0, 2);
        if (EditorIcons.GhostButton("hiercollapse", EditorIcons.ChevronRight, "Collapse All"))
            pendingForce = ExpandForce.CollapseAll;
        gui.SameLine(0, 2);
        if (EditorIcons.GhostButton("hierexpand", EditorIcons.ChevronDown, "Expand All"))
            pendingForce = ExpandForce.ExpandAll;

        gui.SameLine(0, 6);
        gui.SetNextItemWidth(-1);
        gui.InputTextWithHint("##hiersearch", $"{EditorIcons.Search} Search (t:Component)...", ref search, 128);

        EditorDecoration.DrawDivider();

        var entities = scene.Entities.ToArray();

        if (openState.Count > entities.Length)
            PruneOpenState(entities);

        gui.BeginChild("##hiertree", new SysVec2(0, -gui.TextLineHeightWithSpacing), border: false);

        if (search.Length > 0) {
            DrawFilteredList(entities);
        }
        else {
            foreach (Entity entity in entities)
                if (entity.transform.Parent is null)
                    DrawEntityNode(scene, entity, entities);
        }

        if (gui.BeginPopupContextWindowEmpty("##hierctx")) {
            DrawCreateMenu(scene);
            gui.EndPopup();
        }

        gui.EndChild();

        pendingForce = ExpandForce.None;

        if (gui.BeginDragDropTarget()) {
            if (AcceptEntityDrop(entities, out Entity dropped) && dropped.transform.Parent is not null) {
                EditorCommands.Structural("Unparent", () => dropped.transform.SetParentKeepingWorld(null));
            }
            if (AcceptAssetDrop(out List<Guid> droppedAssets)) {
                InstantiateModels(scene, droppedAssets);
                InstantiatePrefabs(droppedAssets);
                CreateEntitiesFromScripts(scene, droppedAssets);
            }
            gui.EndDragDropTarget();
        }

        gui.TextDisabled(entities.Length == 1 ? "1 entity" : $"{entities.Length} entities");

        if (gui.IsWindowFocusedIncludingChildren() && renamingId == -1 &&
            !gui.WantTextInput) {
            if (gui.KeyCtrl && gui.KeyPressed(EditorGuiKey.A) && entities.Length > 0)
                state.SelectEntities(entities, entities[^1]);
            if (state.Selected is not null) {
                if (gui.KeyCtrl && gui.KeyPressed(EditorGuiKey.D))
                    DuplicateSelected(scene);
                if (gui.KeyPressed(EditorGuiKey.Delete))
                    DeleteSelected(scene);
                if (gui.KeyCtrl && gui.KeyShift && gui.KeyPressed(EditorGuiKey.G))
                    GroupSelected(scene);
            }
        }
    }

    void DeleteSelected(Scene scene) {
        var targets = state.SelectedEntities.ToArray();
        if (targets.Length == 0) return;
        EditorCommands.Structural(targets.Length == 1 ? "Delete Entity" : $"Delete {targets.Length} Entities", () => {
            foreach (Entity e in targets)
                scene.DestroyEntity(e);
            state.Selected = null;
            state.MarkViewportDirty();
        });
    }

    void DuplicateSelected(Scene scene) {
        var targets = state.SelectedEntities.ToArray();
        if (targets.Length == 0) return;
        EditorCommands.Structural(targets.Length == 1 ? "Duplicate" : $"Duplicate {targets.Length} Entities", () => {
            var clones = new List<Entity>(targets.Length);
            foreach (Entity e in targets)
                clones.Add(EntityClone.Duplicate(scene, e));
            if (clones.Count > 0)
                state.SelectEntities(clones, clones[^1]);
            state.MarkViewportDirty();
        });
    }

    void GroupSelected(Scene scene) {
        var targets = state.SelectedEntities.ToArray();
        if (targets.Length == 0) return;

        var roots = new List<Entity>();
        foreach (Entity e in targets) {
            bool hasSelectedAncestor = false;
            for (Transform t = e.transform.Parent; t is not null; t = t.Parent)
                if (t.Entity is { } pe && Array.IndexOf(targets, pe) >= 0) { hasSelectedAncestor = true; break; }
            if (!hasSelectedAncestor) roots.Add(e);
        }
        if (roots.Count == 0) return;

        EditorCommands.Structural(roots.Count == 1 ? "Group" : $"Group {roots.Count} Entities", () => {
            Vector3 centre = Vector3.Zero;
            foreach (Entity e in roots) centre += e.transform.WorldPosition;
            centre /= roots.Count;

            Entity group = scene.CreateEntity("Group");
            group.transform.WorldPosition = centre;

            Transform commonParent = roots[0].transform.Parent;
            foreach (Entity e in roots)
                if (!ReferenceEquals(e.transform.Parent, commonParent)) { commonParent = null; break; }
            if (commonParent is not null)
                group.transform.SetParentKeepingWorld(commonParent);

            foreach (Entity e in roots)
                e.transform.SetParentKeepingWorld(group.transform);

            state.Select(group);
            state.MarkViewportDirty();
        });
    }

    void CreatePrefab(Entity entity) {
        if (AssetDatabase.Project is null)
            return;

        string folder = CurrentAssetFolder?.Invoke() ?? "Assets";
        string baseName = string.IsNullOrEmpty(entity.Name) ? "Prefab" : entity.Name;
        string dir = AssetDatabase.Project.ResolveAbsolute(folder);
        Directory.CreateDirectory(dir);

        string relPath = $"{folder}/{baseName}.prefab";
        string abs = Path.Combine(dir, baseName + ".prefab");
        for (var i = 1; File.Exists(abs); i++) {
            relPath = $"{folder}/{baseName} {i}.prefab";
            abs = Path.Combine(dir, $"{baseName} {i}.prefab");
        }

        try {
            File.WriteAllText(abs, PrefabAsset.FromEntity(entity).ToYaml());
            EditorCommands.Structural("Create Prefab", () => {
                AsyncAssetImport.Request("Creating prefab...", onFinished: () => {
                    if (AssetDatabase.TryGetGuid(relPath, out Guid guid)) {
                        entity.PrefabSource = guid;
                        state.MarkViewportDirty();
                    }
                });
            });
        }
        catch (Exception e) {
            Debugging.LogError($"Could not create prefab: {e.Message}");
        }
    }

    bool MatchesSearch(Entity entity) {
        if (search.StartsWith("t:", StringComparison.OrdinalIgnoreCase)) {
            string term = search[2..].Trim();
            foreach (Behaviour b in entity.Behaviours)
                if (term.Length == 0 || b.GetType().Name.Contains(term, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }
        return entity.Name.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    void DrawFilteredList(Entity[] entities) {
        foreach (Entity entity in entities) {
            if (!MatchesSearch(entity))
                continue;

            var id = entity.InstanceId.GetHashCode();
            bool selected = state.IsEntitySelected(entity);
            (string icon, SysVec4 tint) = EditorIcons.ForEntity(entity);

            if (!entity.IsActive)
                gui.PushColor(EditorStyleColor.Text, gui.StyleColor(EditorStyleColor.TextDisabled));
            if (gui.Selectable($"      {entity.Name}##s{id}", selected)) {
                if (gui.KeyCtrl) state.ToggleEntity(entity);
                else state.Select(entity);
            }
            if (!entity.IsActive)
                gui.PopColor();

            DrawRowIcon(gui.ItemRectMin + new SysVec2(4, 0), icon, tint, entity.IsActive);
            if (selected)
                DrawSelectionBar(gui.ItemRectMin, gui.ItemRectMax);
        }
    }

    void DrawEntityNode(Scene scene, Entity entity, Entity[] allEntities) {
        var id = entity.InstanceId.GetHashCode();

        if (renamingId == id) {
            gui.SetNextItemWidth(-1);
            if (renameFocusPending) { gui.SetKeyboardFocusHere(); renameFocusPending = false; }
            gui.InputText($"##rename{id}", ref renameBuffer, 128);
            var commit = gui.IsItemDeactivatedAfterEdit() || gui.KeyPressed(EditorGuiKey.Enter);
            if (commit || gui.IsItemDeactivated()) {
                if (commit && !string.IsNullOrWhiteSpace(renameBuffer)) {
                    EditorCommands.EditEntity(entity, "Rename", () => entity.Name = renameBuffer);
                }
                renamingId = -1;
            }
            return;
        }

        var children = Children(entity, allEntities);
        bool selected = state.IsEntitySelected(entity);

        if (children.Count > 0) {
            bool firstSeen = !openState.ContainsKey(id);
            if (pendingForce != ExpandForce.None || firstSeen) {
                bool wantOpen = pendingForce switch {
                    ExpandForce.ExpandAll => true,
                    ExpandForce.CollapseAll => false,
                    _ => false,
                };
                gui.SetNextItemOpen(wantOpen);
                openState[id] = wantOpen;
            }
        }

        var flags = EditorTreeFlags.OpenOnArrow | EditorTreeFlags.SpanAvailWidth |
                    EditorTreeFlags.AllowOverlap;
        if (selected) flags |= EditorTreeFlags.Selected;
        if (children.Count == 0) flags |= EditorTreeFlags.Leaf | EditorTreeFlags.NoTreePushOnOpen;

        bool isChild = entity.transform.Parent is not null;
        bool tinted = !entity.IsActive || entity.IsPrefabInstance || isChild;
        if (tinted) {
            SysVec4 col = !entity.IsActive ? gui.StyleColor(EditorStyleColor.TextDisabled)
                : entity.IsPrefabInstance ? EditorTheme.PrefabBlue : EditorTheme.RowChild;
            gui.PushColor(EditorStyleColor.Text, col);
        }

        bool open = gui.TreeNodeEx($"     {entity.Name}##{id}", flags);

        if (children.Count > 0)
            openState[id] = open;

        if (tinted)
            gui.PopColor();

        SysVec2 rowMin = gui.ItemRectMin;
        SysVec2 rowMax = gui.ItemRectMax;
        bool rowHovered = gui.IsItemHovered();

        if (gui.IsItemClicked() && !gui.IsItemToggledOpen()) {
            if (gui.KeyCtrl)
                state.ToggleEntity(entity);
            else if (gui.KeyShift && state.Selected is not null)
                state.SelectEntities(RangeBetween(allEntities, state.Selected, entity), entity);
            else
                state.Select(entity);
        }

        HandleDragDrop(scene, entity, allEntities);

        if (selected && (gui.KeyPressed(EditorGuiKey.F2) ||
                         (rowHovered && gui.IsMouseDoubleClicked(0))))
            BeginRename(entity);

        DrawEntityContextMenu(scene, entity, id);

        (string icon, SysVec4 tint) = EditorIcons.ForEntity(entity);
        DrawRowIcon(new SysVec2(rowMin.X + gui.TreeNodeToLabelSpacing, rowMin.Y), icon, tint,
            entity.IsActive);

        if (selected)
            DrawSelectionBar(rowMin, rowMax);

        if (rowHovered || !entity.IsActive || selected) {
            float eyeW = EditorIcons.SmallButtonWidth(EditorIcons.Eye);
            gui.SameLine();
            gui.CursorPosX = (gui.CursorPosX + gui.ContentRegionAvail.X - eyeW);
            gui.PushColor(EditorStyleColor.Text, entity.IsActive
                ? gui.StyleColor(EditorStyleColor.TextDisabled)
                : EditorTheme.IconMuted);
            if (EditorIcons.GhostButtonSmall($"eye{id}", EditorIcons.Eye,
                    entity.IsActive ? "Hide (deactivate)" : "Show (activate)")) {
                bool newActive = !entity.IsActive;
                var batch = selected && state.SelectedEntities.Count > 1
                    ? state.SelectedEntities.ToArray()
                    : new[] { entity };
                EditorCommands.Structural(batch.Length > 1 ? $"Toggle Active ({batch.Length})" : "Toggle Active", () => {
                    foreach (Entity e in batch)
                        if (!e.IsDestroyed) e.SetActive(newActive);
                });
                state.MarkViewportDirty();
            }
            gui.PopColor();
        }

        if (open && children.Count > 0) {
            IEditorDrawList dl = gui.WindowDrawList;
            uint lineCol = gui.ColorU32(EditorTheme.TreeGuide);
            float gutterX = rowMin.X + gui.TreeNodeToLabelSpacing * 0.5f;
            float elbowW = gui.TreeNodeToLabelSpacing * 0.42f;
            float halfRow = gui.FrameHeight * 0.5f;
            float lastChildY = rowMin.Y;

            foreach (Entity child in children) {
                float childTopY = gui.CursorScreenPos.Y;
                DrawEntityNode(scene, child, allEntities);
                float midY = childTopY + halfRow;
                dl.AddLine(new SysVec2(gutterX, midY), new SysVec2(gutterX + elbowW, midY), lineCol, 1f);
                lastChildY = midY;
            }

            dl.AddLine(new SysVec2(gutterX, rowMax.Y), new SysVec2(gutterX, lastChildY), lineCol, 1f);

            gui.TreePop();
        }
    }

    internal static void DrawRowIcon(SysVec2 pos, string icon, SysVec4 tint, bool active) {
        if (!active)
            tint = new SysVec4(tint.X, tint.Y, tint.Z, 0.45f);
        EditorIcons.DrawAt(pos, icon, tint);
    }

    internal static void DrawSelectionBar(SysVec2 rowMin, SysVec2 rowMax) {
        float x = gui.WindowPos.X + 1;
        gui.WindowDrawList.AddRectFilled(new SysVec2(x, rowMin.Y), new SysVec2(x + 3, rowMax.Y),
            gui.ColorU32(gui.StyleColor(EditorStyleColor.CheckMark)));
    }

    void DrawEntityContextMenu(Scene scene, Entity entity, int id) {
        if (!gui.BeginPopupContextItem($"##entctx{id}"))
            return;

        if (!state.IsEntitySelected(entity))
            state.Select(entity);

        int count = state.SelectedEntities.Count;
        string suffix = count > 1 ? $" ({count})" : "";

        if (count == 1 && gui.MenuItem("Rename", "F2")) BeginRename(entity);
        if (count == 1 && !entity.IsPrefabInstance &&
            gui.MenuItem($"{EditorIcons.Package}  Create Prefab")) CreatePrefab(entity);

        if (count == 1 && entity.IsPrefabInstance && gui.BeginMenu($"{EditorIcons.Package}  Prefab")) {
            if (gui.MenuItem("Select Asset")) {
                string p = AssetDatabase.GuidToAssetPath(entity.PrefabSource);
                if (p is not null) state.RequestRevealAsset(p);
            }
            if (gui.MenuItem("Apply Overrides")) PrefabInstanceOps.ApplyAll(entity);
            if (gui.MenuItem("Revert Overrides")) { PrefabInstanceOps.RevertAll(entity); state.MarkViewportDirty(); }
            gui.Separator();
            if (gui.MenuItem("Unpack")) EditorCommands.EditEntity(entity, "Unpack Prefab", () => entity.PrefabSource = Guid.Empty);
            gui.EndMenu();
        }
        if (gui.MenuItem($"Duplicate{suffix}", "Ctrl+D")) DuplicateSelected(scene);
        if (gui.MenuItem($"Group{suffix}", "Ctrl+Shift+G")) GroupSelected(scene);
        if (entity.transform.Parent is not null && gui.MenuItem($"Unparent{suffix}")) {
            EditorCommands.Structural("Unparent", () => {
                foreach (Entity e in state.SelectedEntities.ToArray())
                    e.transform.SetParentKeepingWorld(null);
                state.MarkViewportDirty();
            });
        }
        gui.Separator();
        DrawCreateMenu(scene);
        gui.Separator();
        if (gui.MenuItem($"Delete{suffix}", "Del")) DeleteSelected(scene);
        gui.EndPopup();
    }

    unsafe void HandleDragDrop(Scene scene, Entity entity, Entity[] allEntities) {
        if (gui.BeginDragDropSource()) {
            gui.SetDragDropPayloadInt(EntityDragType, entity.InstanceId.GetHashCode());
            gui.Text($"{EditorIcons.Package} {entity.Name}");
            gui.EndDragDropSource();
        }

        if (gui.BeginDragDropTarget()) {
            if (AcceptEntityDrop(allEntities, out Entity dragged) &&
                !ReferenceEquals(dragged, entity) &&
                !entity.transform.IsDescendantOf(dragged.transform)) {
                EditorCommands.Structural("Reparent", () => dragged.transform.SetParentKeepingWorld(entity.transform));
            }

            if (AcceptAssetDrop(out List<Guid> droppedAssets))
                AddScriptComponents(entity, droppedAssets);
            gui.EndDragDropTarget();
        }
    }

    void AddScriptComponents(Entity entity, List<Guid> guids) {
        var types = new List<Type>();
        foreach (Guid guid in guids) {
            Type type = ScriptComponentType(guid);
            if (type is not null)
                types.Add(type);
        }
        if (types.Count == 0)
            return;

        EditorCommands.Structural("Add Script Component", () => {
            foreach (Type type in types)
                entity.AddComponent(type);
            state.Select(entity);
        });
    }

    public bool DropAssetsIntoScene() {
        Scene scene = SceneManager.GetCurrentScene();
        if (scene is null || !AcceptAssetDrop(out List<Guid> droppedAssets))
            return false;
        InstantiateModels(scene, droppedAssets);
        InstantiatePrefabs(droppedAssets);
        CreateEntitiesFromScripts(scene, droppedAssets);
        return true;
    }

    void CreateEntitiesFromScripts(Scene scene, List<Guid> guids) {
        var types = new List<Type>();
        foreach (Guid guid in guids) {
            Type type = ScriptComponentType(guid);
            if (type is not null)
                types.Add(type);
        }
        if (types.Count == 0)
            return;

        EditorCommands.Structural("Add Script Entity", () => {
            Entity last = null;
            foreach (Type type in types) {
                Entity entity = Spawn(scene, type.Name);
                entity.AddComponent(type);
                last = entity;
            }
            if (last is not null)
                state.Select(last);
        });
    }

    internal static Type ScriptComponentType(Guid guid) {
        var path = AssetDatabase.GuidToAssetPath(guid);
        if (path is null || !path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            return null;

        var stem = Path.GetFileNameWithoutExtension(path);
        Type type = ComponentRegistry.Resolve(ScriptTemplates.ClassName(stem));
        if (type is null)
            Debugging.LogWarning($"'{path}': no compiled component named '{stem}' â€” " +
                                 "rebuild scripts (Ctrl+R) or make the class name match the file name.");
        return type;
    }

    const string EntityDragType = "BALLISTIC_ENTITY";

    static bool AcceptAssetDrop(out List<Guid> guids) {
        guids = null;
        string text = gui.AcceptDragDropPayloadString(AssetBrowserPanel.DragType);
        if (text is null)
            return false;

        guids = new List<Guid>();
        foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
            if (Guid.TryParse(part, out Guid guid))
                guids.Add(guid);
        return guids.Count > 0;
    }

    void InstantiateModels(Scene scene, List<Guid> guids) {
        var models = guids.Where(ModelInstantiation.IsModel).ToList();
        if (models.Count == 0)
            return;

        EditorCommands.Structural("Add Model", () => {
            Entity last = null;
            foreach (Guid guid in models)
                last = ModelInstantiation.Instantiate(scene, guid) ?? last;
            if (last is not null)
                state.Select(last);
        });
    }

    void InstantiatePrefabs(List<Guid> guids) {
        var prefabs = guids
            .Where(g => AssetDatabase.GuidToAssetPath(g)?.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) == true)
            .ToList();
        if (prefabs.Count == 0)
            return;

        EditorCommands.Structural(prefabs.Count == 1 ? "Instantiate Prefab" : $"Instantiate {prefabs.Count} Prefabs", () => {
            var roots = new List<Entity>();
            foreach (Guid guid in prefabs) {
                PrefabAsset prefab = AssetDatabase.Load<PrefabAsset>(AssetDatabase.GuidToAssetPath(guid));
                Entity root = prefab?.Instantiate();
                if (root is not null)
                    roots.Add(root);
            }
            if (roots.Count > 0)
                state.SelectEntities(roots, roots[^1]);
            state.MarkViewportDirty();
        });
    }

    static bool AcceptEntityDrop(Entity[] entities, out Entity entity) {
        entity = null;
        if (gui.AcceptDragDropPayloadInt(EntityDragType) is not { } id)
            return false;

        foreach (Entity e in entities)
            if (e.InstanceId.GetHashCode() == id) { entity = e; return true; }
        return false;
    }

    void PruneOpenState(Entity[] entities) {
        var live = new HashSet<int>(entities.Length);
        foreach (Entity e in entities)
            live.Add(e.InstanceId.GetHashCode());
        List<int> stale = null;
        foreach (int key in openState.Keys)
            if (!live.Contains(key))
                (stale ??= new List<int>()).Add(key);
        if (stale is not null)
            foreach (int key in stale)
                openState.Remove(key);
    }

    static List<Entity> Children(Entity parent, Entity[] all) {
        var list = new List<Entity>();
        foreach (Entity e in all)
            if (ReferenceEquals(e.transform.Parent, parent.transform))
                list.Add(e);
        return list;
    }

    static List<Entity> FlattenVisible(Entity[] all) {
        var order = new List<Entity>();
        void Walk(Entity e) {
            order.Add(e);
            foreach (Entity child in Children(e, all))
                Walk(child);
        }
        foreach (Entity e in all)
            if (e.transform.Parent is null)
                Walk(e);
        return order;
    }

    static List<Entity> RangeBetween(Entity[] all, Entity a, Entity b) {
        List<Entity> flat = FlattenVisible(all);
        int ia = flat.IndexOf(a), ib = flat.IndexOf(b);
        if (ia < 0 || ib < 0)
            return new List<Entity> { b };
        if (ia > ib) (ia, ib) = (ib, ia);
        return flat.GetRange(ia, ib - ia + 1);
    }

    Entity Spawn(Scene scene, string name) {
        Entity e = scene.CreateEntity(name);
        e.transform.Position = state.SceneSpawnPoint;
        return e;
    }

    void DrawCreateMenu(Scene scene) {
        if (gui.MenuItem("Create Empty")) {
            EditorCommands.Structural("Create Empty", () => state.Select(Spawn(scene, "Entity")));
        }
        if (gui.BeginMenu($"{EditorIcons.Package} 3D Object")) {
            if (gui.MenuItem("Cube")) CreatePrimitive(scene, PrimitiveKind.Cube);
            if (gui.MenuItem("Sphere")) CreatePrimitive(scene, PrimitiveKind.Sphere);
            if (gui.MenuItem("Plane")) CreatePrimitive(scene, PrimitiveKind.Plane);
            gui.EndMenu();
        }
        if (gui.MenuItem($"{EditorIcons.Grid} Terrain"))
            CreateTerrain(scene);
        if (gui.BeginMenu($"{EditorIcons.Lightbulb} Light")) {
            if (gui.MenuItem("Directional Light")) CreateWithComponent<DirectionalLight>(scene, "Directional Light");
            if (gui.MenuItem("Point Light")) CreateWithComponent<PointLight>(scene, "Point Light");
            if (gui.MenuItem("Spot Light")) CreateWithComponent<SpotLight>(scene, "Spot Light");
            gui.EndMenu();
        }
        if (gui.MenuItem($"{EditorIcons.Camera} Camera"))
            CreateWithComponent<HDCamera>(scene, "Camera");

        if (gui.BeginMenu($"{EditorIcons.Cloud} Audio")) {
            if (gui.MenuItem("Audio Source")) CreateWithComponentNamed(scene, "Audio Source", "AudioSource");
            if (gui.MenuItem("Audio Listener")) CreateWithComponentNamed(scene, "Audio Listener", "AudioListener");
            gui.EndMenu();
        }

        gui.Separator();
        if (gui.BeginMenu($"{EditorIcons.Add} Component")) {
            DrawComponentCreateMenu(scene);
            gui.EndMenu();
        }
    }

    void DrawComponentCreateMenu(Scene scene) {
        var groups = ComponentRegistry.Menu
            .GroupBy(e => string.IsNullOrEmpty(e.Menu) ? "General" : e.Menu.Split('/')[0])
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups) {
            if (gui.BeginMenu(group.Key)) {
                foreach (ComponentEntry entry in group.OrderBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase)) {
                    if (gui.MenuItem(entry.DisplayName)) {
                        EditorCommands.Structural($"Create {entry.DisplayName}", () => {
                            Entity e = Spawn(scene, entry.DisplayName);
                            e.AddComponent(entry.Type);
                            state.Select(e);
                        });
                    }
                }
                gui.EndMenu();
            }
        }
    }

    void CreateWithComponentNamed(Scene scene, string name, string registryName) {
        Type type = ComponentRegistry.Resolve(registryName);
        if (type is null) return;
        EditorCommands.Structural($"Create {name}", () => {
            Entity entity = Spawn(scene, name);
            entity.AddComponent(type);
            state.Select(entity);
        });
    }

    void CreateWithComponent<T>(Scene scene, string name) where T : Behaviour {
        EditorCommands.Structural($"Create {name}", () => {
            Entity entity = Spawn(scene, name);
            entity.AddComponent(typeof(T));
            state.Select(entity);
        });
    }

    void CreatePrimitive(Scene scene, PrimitiveKind kind) {
        EditorCommands.Structural($"Create {kind}", () => {
            Entity e = Primitives.Create(scene, kind);
            if (e is not null) e.transform.Position = state.SceneSpawnPoint;
            state.Select(e);
        });
    }

    void CreateTerrain(Scene scene) {
        EditorCommands.Structural("Create Terrain", () => {
            Entity entity = Spawn(scene, "Terrain");
            var terrain = (Terrain)entity.AddComponent(typeof(Terrain));
            state.Select(entity);

            string folder = CurrentAssetFolder?.Invoke() ?? "Assets";
            string dir = AssetDatabase.Project.ResolveAbsolute(folder);
            Directory.CreateDirectory(dir);
            string terrainAbs = UniqueAssetPath(Path.Combine(dir, "Terrain.terrain"));
            File.WriteAllText(terrainAbs,
                "{\n  \"version\": 1,\n  \"resolution\": 256,\n  \"sizeX\": 100,\n  \"sizeZ\": 100,\n  \"heightScale\": 20\n}\n");
            string terrainRel = ToProjectRelative(terrainAbs);

            string materialRel = TerrainAssets.EnsureCheckerMaterial();

            AsyncAssetImport.Request("Creating terrain...", onFinished: () => {
                var asset = AssetDatabase.Load<TerrainAsset>(terrainRel);
                if (asset is not null) terrain.Terrain3D = asset;
                if (materialRel is not null) {
                    var mat = AssetDatabase.Load<Material>(materialRel);
                    if (mat is not null) terrain.Material = mat;
                }
                state.MarkViewportDirty();
            });
        });
    }

    static string UniqueAssetPath(string path) {
        if (!File.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path);
        string name = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        for (var i = 1; ; i++) {
            string candidate = Path.Combine(dir, $"{name} {i}{ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    static string ToProjectRelative(string absolute) {
        string root = AssetDatabase.Project.ResolveAbsolute("Assets");
        string assetsRel = "Assets" + absolute[root.Length..];
        return assetsRel.Replace('\\', '/');
    }

    void BeginRename(Entity entity) {
        renamingId = entity.InstanceId.GetHashCode();
        renameBuffer = entity.Name ?? "";
        renameFocusPending = true;
    }

}
