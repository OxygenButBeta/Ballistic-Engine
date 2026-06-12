using Hexa.NET.ImGui;
using SysVec2 = System.Numerics.Vector2;
using SysVec4 = System.Numerics.Vector4;

namespace BallisticEngine.Editor;

// Two hierarchies as tabs: "Entities" (the scene's entity list) and "Scene" (scene-wide
// SceneBehaviours: skybox, fog, ...). Selection from either drives the Inspector.
// Rows carry a type icon (camera/light/mesh), an eye visibility toggle on the right edge,
// and an accent bar marks the selection. The search box filters to a flat list.
internal sealed class HierarchyPanel {
    readonly EditorState state;

    // Inline-rename state: the entity being renamed (by id) and the edit buffer.
    int renamingId = -1;
    string renameBuffer = "";
    bool renameFocusPending;

    string search = "";

    // Set by EditorApplication: the asset browser's current folder (project-relative), so "Create
    // Prefab" writes the .prefab next to whatever the user is browsing. Falls back to "Assets".
    public Func<string> CurrentAssetFolder;

    public HierarchyPanel(EditorState state) => this.state = state;

    // Entities and Scene-components are now SEPARATE dockable windows (hosted by EditorApplication),
    // not inner tabs — so they can be split/rearranged. These are the two window bodies.
    public void DrawEntitiesContents() => DrawEntities();
    public void DrawSceneContents() => DrawSceneBehaviours();

    void DrawEntities() {
        Scene scene = SceneManager.GetCurrentScene();

        // Toolbar: create (+), delete, then the search field filling the rest of the row.
        if (EditorIcons.GhostButton("hieradd", EditorIcons.Add, "Create entity"))
            ImGui.OpenPopup("##hiercreate");
        if (ImGui.BeginPopup("##hiercreate")) {
            DrawCreateMenu(scene);
            ImGui.EndPopup();
        }

        ImGui.SameLine(0, 2);
        ImGui.BeginDisabled(state.Selected is null);
        if (EditorIcons.GhostButton("hierdel", EditorIcons.Delete, "Delete selected (Del)"))
            DeleteSelected(scene);
        ImGui.EndDisabled();

        ImGui.SameLine(0, 6);
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##hiersearch", $"{EditorIcons.Search} Search...", ref search, 128);

        ImGui.Separator();

        // Snapshot so create/delete during iteration is safe; render the forest from the roots.
        var entities = scene.Entities.ToArray();

        // A full-height child as the drop area (minus a slim count footer), so dragging an
        // entity onto empty space unparents it.
        ImGui.BeginChild("##hiertree", new SysVec2(0, -ImGui.GetTextLineHeightWithSpacing()));

        if (search.Length > 0) {
            DrawFilteredList(entities);
        }
        else {
            foreach (Entity entity in entities)
                if (entity.transform.Parent is null)
                    DrawEntityNode(scene, entity, entities);
        }

        // Right-click empty space INSIDE the child (where the empty area actually is) â†’ create menu.
        // Must be opened against this child window, not the outer Hierarchy window.
        if (ImGui.BeginPopupContextWindow("##hierctx",
                ImGuiPopupFlags.MouseButtonRight | ImGuiPopupFlags.NoOpenOverItems)) {
            DrawCreateMenu(scene);
            ImGui.EndPopup();
        }

        ImGui.EndChild();

        // Drop on the empty child area â†’ unparent to world; model assets from the browser
        // instantiate as entities (one per source mesh for splitByNodes imports); script assets
        // create a fresh entity carrying that component (Unity behavior).
        if (ImGui.BeginDragDropTarget()) {
            if (AcceptEntityDrop(entities, out Entity dropped) && dropped.transform.Parent is not null) {
                EditorUndo.Push("Unparent");
                dropped.transform.SetParentKeepingWorld(null);
            }
            if (AcceptAssetDrop(out List<Guid> droppedAssets)) {
                InstantiateModels(scene, droppedAssets);
                InstantiatePrefabs(droppedAssets);
                CreateEntitiesFromScripts(scene, droppedAssets);
            }
            ImGui.EndDragDropTarget();
        }

        ImGui.TextDisabled(entities.Length == 1 ? "1 entity" : $"{entities.Length} entities");

        // Keyboard shortcuts when the hierarchy is focused and not mid-rename. Operate on the whole
        // multi-selection. Ctrl+A selects every visible entity.
        if (ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) && renamingId == -1 &&
            !ImGui.GetIO().WantTextInput) {
            if (ImGui.GetIO().KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.A) && entities.Length > 0)
                state.SelectEntities(entities, entities[^1]);
            if (state.Selected is not null) {
                if (ImGui.GetIO().KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.D))
                    DuplicateSelected(scene);
                if (ImGui.IsKeyPressed(ImGuiKey.Delete))
                    DeleteSelected(scene);
            }
        }
    }

    // ---- Batch operations on the multi-selection -----------------------------

    // Snapshots once, then destroys every selected entity (children go with their parent).
    void DeleteSelected(Scene scene) {
        var targets = state.SelectedEntities.ToArray();
        if (targets.Length == 0) return;
        EditorUndo.Push(targets.Length == 1 ? "Delete Entity" : $"Delete {targets.Length} Entities");
        foreach (Entity e in targets)
            scene.DestroyEntity(e);
        state.Selected = null;
        state.MarkViewportDirty();
    }

    // Duplicates every selected entity; the clones become the new selection (active = last clone).
    void DuplicateSelected(Scene scene) {
        var targets = state.SelectedEntities.ToArray();
        if (targets.Length == 0) return;
        EditorUndo.Push(targets.Length == 1 ? "Duplicate" : $"Duplicate {targets.Length} Entities");
        var clones = new List<Entity>(targets.Length);
        foreach (Entity e in targets)
            clones.Add(EntityClone.Duplicate(scene, e));
        if (clones.Count > 0)
            state.SelectEntities(clones, clones[^1]);
        state.MarkViewportDirty();
    }

    // Captures the entity subtree as a .prefab asset next to the asset browser's current folder, then
    // refreshes so it appears in the browser. The live entity is left as-is (Unity creates the asset
    // without converting the scene object into a prefab instance — v1).
    void CreatePrefab(Entity entity) {
        if (AssetDatabase.Project is null)
            return;

        string folder = CurrentAssetFolder?.Invoke() ?? "Assets";
        string baseName = string.IsNullOrEmpty(entity.Name) ? "Prefab" : entity.Name;
        string dir = AssetDatabase.Project.ResolveAbsolute(folder);
        Directory.CreateDirectory(dir);

        // Avoid clobbering an existing file: Name, Name 1, Name 2, ...
        string path = Path.Combine(dir, baseName + ".prefab");
        for (var i = 1; File.Exists(path); i++)
            path = Path.Combine(dir, $"{baseName} {i}.prefab");

        try {
            File.WriteAllText(path, PrefabAsset.FromEntity(entity).ToYaml());
            AsyncAssetImport.Request("Creating prefab...");
        }
        catch (Exception e) {
            Debugging.LogError($"Could not create prefab: {e.Message}");
        }
    }

    // Search results as a flat list (hierarchy is meaningless while filtering).
    void DrawFilteredList(Entity[] entities) {
        foreach (Entity entity in entities) {
            if (!entity.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
                continue;

            var id = entity.InstanceId.GetHashCode();
            bool selected = state.IsEntitySelected(entity);
            (string icon, SysVec4 tint) = EditorIcons.ForEntity(entity);

            if (!entity.IsActive)
                ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
            if (ImGui.Selectable($"      {entity.Name}##s{id}", selected)) {
                if (ImGui.GetIO().KeyCtrl) state.ToggleEntity(entity);
                else state.Select(entity);
            }
            if (!entity.IsActive)
                ImGui.PopStyleColor();

            DrawRowIcon(ImGui.GetItemRectMin() + new SysVec2(4, 0), icon, tint, entity.IsActive);
            if (selected)
                DrawSelectionBar(ImGui.GetItemRectMin(), ImGui.GetItemRectMax());
        }
    }

    void DrawEntityNode(Scene scene, Entity entity, Entity[] allEntities) {
        var id = entity.InstanceId.GetHashCode();

        // Inline rename takes over the row.
        if (renamingId == id) {
            ImGui.SetNextItemWidth(-1);
            if (renameFocusPending) { ImGui.SetKeyboardFocusHere(); renameFocusPending = false; }
            ImGui.InputText($"##rename{id}", ref renameBuffer, 128);
            var commit = ImGui.IsItemDeactivatedAfterEdit() || ImGui.IsKeyPressed(ImGuiKey.Enter);
            if (commit || ImGui.IsItemDeactivated()) {
                if (commit && !string.IsNullOrWhiteSpace(renameBuffer)) {
                    EditorUndo.Push("Rename");
                    entity.Name = renameBuffer;
                }
                renamingId = -1;
            }
            return;
        }

        var children = Children(entity, allEntities);
        bool selected = state.IsEntitySelected(entity);

        var flags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.SpanAvailWidth |
                    ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.AllowOverlap;
        if (selected) flags |= ImGuiTreeNodeFlags.Selected;
        if (children.Count == 0) flags |= ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen;

        if (!entity.IsActive)
            ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);

        // Leading spaces leave room for the type icon overlaid after the arrow.
        bool open = ImGui.TreeNodeEx($"     {entity.Name}##{id}", flags);

        if (!entity.IsActive)
            ImGui.PopStyleColor();

        // Capture the row rect/hover NOW â€” popups and drag-drop below overwrite "last item" data.
        SysVec2 rowMin = ImGui.GetItemRectMin();
        SysVec2 rowMax = ImGui.GetItemRectMax();
        bool rowHovered = ImGui.IsItemHovered();

        // Click (not on the arrow) selects. Ctrl toggles into the multi-selection; Shift extends a
        // range from the active entity over the currently visible (flattened) order; plain click selects
        // just this one (Unity behavior).
        if (ImGui.IsItemClicked() && !ImGui.IsItemToggledOpen()) {
            ImGuiIOPtr io = ImGui.GetIO();
            if (io.KeyCtrl)
                state.ToggleEntity(entity);
            else if (io.KeyShift && state.Selected is not null)
                state.SelectEntities(RangeBetween(allEntities, state.Selected, entity), entity);
            else
                state.Select(entity);
        }

        // Drag source: carries the entity id. Drop target: reparent the dragged entity onto this one.
        HandleDragDrop(scene, entity, allEntities);

        if (selected && (ImGui.IsKeyPressed(ImGuiKey.F2) ||
                         (rowHovered && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))))
            BeginRename(entity);

        DrawEntityContextMenu(scene, entity, id);

        // Type icon between the arrow and the label.
        (string icon, SysVec4 tint) = EditorIcons.ForEntity(entity);
        DrawRowIcon(new SysVec2(rowMin.X + ImGui.GetTreeNodeToLabelSpacing(), rowMin.Y), icon, tint,
            entity.IsActive);

        if (selected)
            DrawSelectionBar(rowMin, rowMax);

        // Eye toggle pinned to the row's right edge; shown when relevant so rows stay calm.
        if (rowHovered || !entity.IsActive || selected) {
            float eyeW = EditorIcons.SmallButtonWidth(EditorIcons.Eye);
            ImGui.SameLine();
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - eyeW);
            ImGui.PushStyleColor(ImGuiCol.Text, entity.IsActive
                ? ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]
                : new SysVec4(0.45f, 0.47f, 0.52f, 0.6f));
            if (EditorIcons.GhostButtonSmall($"eye{id}", EditorIcons.Eye,
                    entity.IsActive ? "Hide (deactivate)" : "Show (activate)")) {
                EditorUndo.Push("Toggle Active");
                entity.SetActive(!entity.IsActive);
                state.MarkViewportDirty();
            }
            ImGui.PopStyleColor();
        }

        if (open && children.Count > 0) {
            foreach (Entity child in children)
                DrawEntityNode(scene, child, allEntities);
            ImGui.TreePop();
        }
    }

    static void DrawRowIcon(SysVec2 pos, string icon, SysVec4 tint, bool active) {
        if (!active)
            tint = new SysVec4(tint.X, tint.Y, tint.Z, 0.45f);
        EditorIcons.DrawAt(pos, icon, tint);
    }

    // A slim accent bar on the window's left edge marking the selected row.
    static void DrawSelectionBar(SysVec2 rowMin, SysVec2 rowMax) {
        float x = ImGui.GetWindowPos().X + 1;
        ImGui.GetWindowDrawList().AddRectFilled(new SysVec2(x, rowMin.Y), new SysVec2(x + 3, rowMax.Y),
            ImGui.GetColorU32(ImGuiCol.CheckMark));
    }

    void DrawEntityContextMenu(Scene scene, Entity entity, int id) {
        if (!ImGui.BeginPopupContextItem($"##entctx{id}"))
            return;

        // Right-clicking a row that's NOT already in the selection selects just it (Unity); right-
        // clicking an already-selected row keeps the whole multi-selection so the actions batch.
        if (!state.IsEntitySelected(entity))
            state.Select(entity);

        int count = state.SelectedEntities.Count;
        string suffix = count > 1 ? $" ({count})" : "";

        if (count == 1 && ImGui.MenuItem("Rename", "F2")) BeginRename(entity);
        if (count == 1 && ImGui.MenuItem($"{EditorIcons.Package}  Create Prefab")) CreatePrefab(entity);
        if (ImGui.MenuItem($"Duplicate{suffix}", "Ctrl+D")) DuplicateSelected(scene);
        if (entity.transform.Parent is not null && ImGui.MenuItem($"Unparent{suffix}")) {
            EditorUndo.Push("Unparent");
            foreach (Entity e in state.SelectedEntities.ToArray())
                e.transform.SetParentKeepingWorld(null);
            state.MarkViewportDirty();
        }
        ImGui.Separator();
        DrawCreateMenu(scene);   // create children/objects from a node too
        ImGui.Separator();
        if (ImGui.MenuItem($"Delete{suffix}", "Del")) DeleteSelected(scene);
        ImGui.EndPopup();
    }

    unsafe void HandleDragDrop(Scene scene, Entity entity, Entity[] allEntities) {
        if (ImGui.BeginDragDropSource()) {
            int payload = entity.InstanceId.GetHashCode();
            ImGui.SetDragDropPayload(EntityDragType, &payload, (ulong)sizeof(int));
            ImGui.Text($"{EditorIcons.Package} {entity.Name}");
            ImGui.EndDragDropSource();
        }

        if (ImGui.BeginDragDropTarget()) {
            if (AcceptEntityDrop(allEntities, out Entity dragged) &&
                !ReferenceEquals(dragged, entity) &&
                !entity.transform.IsDescendantOf(dragged.transform)) {   // no cycles
                EditorUndo.Push("Reparent");
                dragged.transform.SetParentKeepingWorld(entity.transform);
            }
            // Script asset dropped onto an entity row â†’ add its component (Unity behavior).
            if (AcceptAssetDrop(out List<Guid> droppedAssets))
                AddScriptComponents(entity, droppedAssets);
            ImGui.EndDragDropTarget();
        }
    }

    void AddScriptComponents(Entity entity, List<Guid> guids) {
        var added = false;
        foreach (Guid guid in guids) {
            Type type = ScriptComponentType(guid);
            if (type is null)
                continue;
            if (!added)
                EditorUndo.Push("Add Script Component");
            added = true;
            entity.AddComponent(type);
        }

        if (added)
            state.Select(entity);
    }

    void CreateEntitiesFromScripts(Scene scene, List<Guid> guids) {
        Entity last = null;
        foreach (Guid guid in guids) {
            Type type = ScriptComponentType(guid);
            if (type is null)
                continue;
            if (last is null)
                EditorUndo.Push("Add Script Entity");
            Entity entity = scene.CreateEntity(type.Name);
            entity.AddComponent(type);
            last = entity;
        }

        if (last is not null)
            state.Select(last);
    }

    // Maps a dropped .cs asset to its compiled component by Unity's file-name == class-name rule
    // (the registry only knows Behaviour types, so SceneBehaviours and plain classes resolve null).
    // Internal so the Inspector can reuse it for its own script-drop target.
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

    // Asset-browser drops carry ';'-separated GUIDs (multi-select drags several at once).
    static unsafe bool AcceptAssetDrop(out List<Guid> guids) {
        guids = null;
        ImGuiPayloadPtr payload = ImGui.AcceptDragDropPayload(AssetBrowserPanel.DragType);
        if (payload.IsNull || payload.Data == null)
            return false;

        var text = System.Runtime.InteropServices.Marshal.PtrToStringAnsi((IntPtr)payload.Data, payload.DataSize);
        guids = new List<Guid>();
        foreach (var part in text?.Split(';', StringSplitOptions.RemoveEmptyEntries) ?? [])
            if (Guid.TryParse(part, out Guid guid))
                guids.Add(guid);
        return guids.Count > 0;
    }

    void InstantiateModels(Scene scene, List<Guid> guids) {
        var models = guids.Where(ModelInstantiation.IsModel).ToList();
        if (models.Count == 0)
            return;

        EditorUndo.Push("Add Model");
        Entity last = null;
        foreach (Guid guid in models)
            last = ModelInstantiation.Instantiate(scene, guid) ?? last;
        if (last is not null)
            state.Select(last);
    }

    // Instantiates any dropped .prefab assets into the scene (Unity's drag-prefab-to-hierarchy). The
    // returned roots become the selection. Mirrors InstantiateModels.
    void InstantiatePrefabs(List<Guid> guids) {
        var prefabs = guids
            .Where(g => AssetDatabase.GuidToAssetPath(g)?.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) == true)
            .ToList();
        if (prefabs.Count == 0)
            return;

        EditorUndo.Push(prefabs.Count == 1 ? "Instantiate Prefab" : $"Instantiate {prefabs.Count} Prefabs");
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
    }

    static unsafe bool AcceptEntityDrop(Entity[] entities, out Entity entity) {
        entity = null;
        ImGuiPayloadPtr payload = ImGui.AcceptDragDropPayload(EntityDragType);
        if (payload.IsNull || payload.Data == null)
            return false;

        int id = *(int*)payload.Data;
        foreach (Entity e in entities)
            if (e.InstanceId.GetHashCode() == id) { entity = e; return true; }
        return false;
    }

    static List<Entity> Children(Entity parent, Entity[] all) {
        var list = new List<Entity>();
        foreach (Entity e in all)
            if (ReferenceEquals(e.transform.Parent, parent.transform))
                list.Add(e);
        return list;
    }

    // Depth-first flatten of the visible forest (roots first, each followed by its descendants), the
    // order a shift-range select spans. Matches the on-screen row order.
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

    // The inclusive run of visible entities between two anchors (either click order), for shift-select.
    static List<Entity> RangeBetween(Entity[] all, Entity a, Entity b) {
        List<Entity> flat = FlattenVisible(all);
        int ia = flat.IndexOf(a), ib = flat.IndexOf(b);
        if (ia < 0 || ib < 0)
            return new List<Entity> { b };
        if (ia > ib) (ia, ib) = (ib, ia);
        return flat.GetRange(ia, ib - ia + 1);
    }

    // Shared "create" submenu used by the empty-space context menu and the toolbar + button.
    void DrawCreateMenu(Scene scene) {
        if (ImGui.MenuItem("Create Empty")) {
            EditorUndo.Push("Create Empty");
            state.Select(scene.CreateEntity("Entity"));
        }
        if (ImGui.BeginMenu($"{EditorIcons.Package} 3D Object")) {
            if (ImGui.MenuItem("Cube")) CreatePrimitive(scene, PrimitiveKind.Cube);
            if (ImGui.MenuItem("Sphere")) CreatePrimitive(scene, PrimitiveKind.Sphere);
            if (ImGui.MenuItem("Plane")) CreatePrimitive(scene, PrimitiveKind.Plane);
            ImGui.EndMenu();
        }
        if (ImGui.MenuItem($"{EditorIcons.Grid} Terrain"))
            CreateTerrain(scene);
        if (ImGui.BeginMenu($"{EditorIcons.Lightbulb} Light")) {
            if (ImGui.MenuItem("Directional Light")) CreateWithComponent<DirectionalLight>(scene, "Directional Light");
            if (ImGui.MenuItem("Point Light")) CreateWithComponent<PointLight>(scene, "Point Light");
            if (ImGui.MenuItem("Spot Light")) CreateWithComponent<SpotLight>(scene, "Spot Light");
            ImGui.EndMenu();
        }
        if (ImGui.MenuItem($"{EditorIcons.Camera} Camera"))
            CreateWithComponent<HDCamera>(scene, "Camera");

        // Audio quick-create (a listener + a source are the common pair).
        if (ImGui.BeginMenu($"{EditorIcons.Cloud} Audio")) {
            if (ImGui.MenuItem("Audio Source")) CreateWithComponentNamed(scene, "Audio Source", "AudioSource");
            if (ImGui.MenuItem("Audio Listener")) CreateWithComponentNamed(scene, "Audio Listener", "AudioListener");
            ImGui.EndMenu();
        }

        ImGui.Separator();
        // Every registered component, grouped by its [Component] Menu category — so an entity with ANY
        // component (Particle System, Trail/Line Renderer, Spawner, Health, Animator, ...) is one click
        // away, and new components appear here automatically with no per-item wiring.
        if (ImGui.BeginMenu($"{EditorIcons.Add} Component")) {
            DrawComponentCreateMenu(scene);
            ImGui.EndMenu();
        }
    }

    // Builds the "Create > Component" tree from ComponentRegistry.Menu, nesting each entry under its
    // Menu category ("Effects", "Gameplay", "Physics", ...). Selecting one makes an entity carrying
    // just that component.
    void DrawComponentCreateMenu(Scene scene) {
        // Group by the top-level menu segment (before any '/'); flat entries go to "General".
        var groups = ComponentRegistry.Menu
            .GroupBy(e => string.IsNullOrEmpty(e.Menu) ? "General" : e.Menu.Split('/')[0])
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups) {
            if (ImGui.BeginMenu(group.Key)) {
                foreach (ComponentEntry entry in group.OrderBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase)) {
                    if (ImGui.MenuItem(entry.DisplayName)) {
                        EditorUndo.Push($"Create {entry.DisplayName}");
                        Entity e = scene.CreateEntity(entry.DisplayName);
                        e.AddComponent(entry.Type);
                        state.Select(e);
                    }
                }
                ImGui.EndMenu();
            }
        }
    }

    // Create an entity with a component resolved by its registry NAME (for components not referenced by
    // type in this assembly's quick-create entries).
    void CreateWithComponentNamed(Scene scene, string name, string registryName) {
        Type type = ComponentRegistry.Resolve(registryName);
        if (type is null) return;
        EditorUndo.Push($"Create {name}");
        Entity entity = scene.CreateEntity(name);
        entity.AddComponent(type);
        state.Select(entity);
    }

    void CreateWithComponent<T>(Scene scene, string name) where T : Behaviour {
        EditorUndo.Push($"Create {name}");
        Entity entity = scene.CreateEntity(name);
        entity.AddComponent(typeof(T));
        state.Select(entity);
    }

    void CreatePrimitive(Scene scene, PrimitiveKind kind) {
        EditorUndo.Push($"Create {kind}");
        state.Select(Primitives.Create(scene, kind));
    }

    // Creates a Terrain entity AND its backing assets: a fresh .terrain heightfield next to the asset
    // browser's current folder, plus a shared checker material in Assets/Default (generated once). Both
    // assets import asynchronously, then bind onto the component so the terrain shows the checker
    // immediately. The checker tiles across the terrain so the grid reads at any size.
    void CreateTerrain(Scene scene) {
        EditorUndo.Push("Create Terrain");
        Entity entity = scene.CreateEntity("Terrain");
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

    void DrawSceneBehaviours() {
        Scene scene = SceneManager.GetCurrentScene();

        if (ImGui.Button($"{EditorIcons.Add}  Add Scene Component", new SysVec2(-1, 0)))
            ImGui.OpenPopup("##addscenebehaviour");

        if (ImGui.BeginPopup("##addscenebehaviour")) {
            foreach (ComponentEntry entry in ComponentRegistry.SceneMenu) {
                (string entryIcon, _) = EditorIcons.ForComponentType(entry.Type);
                if (ImGui.MenuItem($"{entryIcon}  {entry.DisplayName}")) {
                    EditorUndo.Push($"Add {entry.DisplayName}");
                    state.SelectSceneBehaviour(scene.AddSceneBehaviour(entry.Type));
                }
            }
            ImGui.EndPopup();
        }

        ImGui.Separator();

        var behaviours = scene.SceneBehaviours.ToArray();
        if (behaviours.Length == 0) {
            ImGui.Spacing();
            ImGui.TextDisabled("No scene components.");
            ImGui.TextDisabled("Add a Skybox to get started.");
        }

        foreach (SceneBehaviour behaviour in behaviours) {
            bool selected = ReferenceEquals(behaviour, state.SelectedSceneBehaviour);
            (string icon, SysVec4 tint) = EditorIcons.ForComponentType(behaviour.GetType());

            if (!behaviour.IsEnabled)
                ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);

            if (ImGui.Selectable($"      {behaviour.GetType().Name}##{behaviour.InstanceId}", selected))
                state.SelectSceneBehaviour(behaviour);

            if (!behaviour.IsEnabled)
                ImGui.PopStyleColor();

            DrawRowIcon(ImGui.GetItemRectMin() + new SysVec2(4, 0), icon, tint, behaviour.IsEnabled);
            if (selected)
                DrawSelectionBar(ImGui.GetItemRectMin(), ImGui.GetItemRectMax());

            if (ImGui.BeginPopupContextItem($"##sbctx{behaviour.InstanceId}")) {
                if (ImGui.MenuItem("Remove")) {
                    EditorUndo.Push($"Remove {behaviour.GetType().Name}");
                    scene.RemoveSceneBehaviour(behaviour);
                    if (ReferenceEquals(state.SelectedSceneBehaviour, behaviour))
                        state.SelectSceneBehaviour(null);
                }
                ImGui.EndPopup();
            }
        }
    }
}
