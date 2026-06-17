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

    // ---- Tree expand/collapse state (EF13+EF14) ------------------------------
    // ImGui keeps tree open-state implicitly per-node in its storage, which can't honour either an
    // on-demand Collapse/Expand-All OR a "collapsed on first load" default without fighting the user's
    // own per-node toggles every frame. So the hierarchy OWNS the open-state, keyed by entity id (the
    // same GetHashCode() used everywhere else): the tracker is the source of truth, pushed into ImGui
    // via SetNextItemOpen ONLY on the frames a force applies (a button click, or a node seen for the
    // first time — which is collapsed-by-default), and read back from ImGui otherwise so manual
    // expansions persist.
    readonly Dictionary<int, bool> openState = new();

    enum ExpandForce { None, CollapseAll, ExpandAll }
    // Set by the toolbar buttons; consumed for exactly ONE frame (all nodes get SetNextItemOpen that
    // frame), then cleared. Outside that frame ImGui keeps the user's per-node state.
    ExpandForce pendingForce = ExpandForce.None;

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

        // Collapse-all / expand-all: arm a one-frame force the tree applies via SetNextItemOpen (EF13).
        ImGui.SameLine(0, 2);
        if (EditorIcons.GhostButton("hiercollapse", EditorIcons.ChevronRight, "Collapse All"))
            pendingForce = ExpandForce.CollapseAll;
        ImGui.SameLine(0, 2);
        if (EditorIcons.GhostButton("hierexpand", EditorIcons.ChevronDown, "Expand All"))
            pendingForce = ExpandForce.ExpandAll;

        ImGui.SameLine(0, 6);
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##hiersearch", $"{EditorIcons.Search} Search (t:Component)...", ref search, 128);

        EditorDecoration.DrawDivider();

        // Snapshot so create/delete during iteration is safe; render the forest from the roots.
        var entities = scene.Entities.ToArray();

        // Keep the open-state tracker bounded: drop entries for entities that no longer exist (deleted,
        // or a scene swap), so a destroyed id can't shadow a future reuse and the dict stays the size of
        // the live scene. Cheap — only runs when the tracker holds more than the live entity count.
        if (openState.Count > entities.Length)
            PruneOpenState(entities);

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

        // The collapse/expand-all force is consumed for exactly this one frame — every node has now
        // read it (or, while filtering, none did). Clear it so the user's subsequent toggles stick.
        pendingForce = ExpandForce.None;

        // Drop on the empty child area â†’ unparent to world; model assets from the browser
        // instantiate as entities (one per source mesh for splitByNodes imports); script assets
        // create a fresh entity carrying that component (Unity behavior).
        if (ImGui.BeginDragDropTarget()) {
            if (AcceptEntityDrop(entities, out Entity dropped) && dropped.transform.Parent is not null) {
                EditorCommands.Structural("Unparent", () => dropped.transform.SetParentKeepingWorld(null));
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
                // Ctrl+Shift+G: wrap the selection in a new parent "Group" (Unity), keeping world poses.
                if (ImGui.GetIO().KeyCtrl && ImGui.GetIO().KeyShift && ImGui.IsKeyPressed(ImGuiKey.G))
                    GroupSelected(scene);
            }
        }
    }

    // ---- Batch operations on the multi-selection -----------------------------

    // Snapshots once, then destroys every selected entity (children go with their parent).
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

    // Duplicates every selected entity; the clones become the new selection (active = last clone).
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

    // Ctrl+Shift+G (Unity's Group): create a new empty "Group" entity at the centre of the selection
    // and reparent every selected entity under it, KEEPING their world poses. Only top-level selected
    // entities are grouped (a selected child whose selected ancestor is also grouped moves with it, so
    // it's skipped) — otherwise reparenting a child onto the group would pull it out of its parent.
    void GroupSelected(Scene scene) {
        var targets = state.SelectedEntities.ToArray();
        if (targets.Length == 0) return;

        // Drop any selected entity that has a selected ANCESTOR — it'll move with that ancestor.
        var roots = new List<Entity>();
        foreach (Entity e in targets) {
            bool hasSelectedAncestor = false;
            for (Transform t = e.transform.Parent; t is not null; t = t.Parent)
                if (t.Entity is { } pe && Array.IndexOf(targets, pe) >= 0) { hasSelectedAncestor = true; break; }
            if (!hasSelectedAncestor) roots.Add(e);
        }
        if (roots.Count == 0) return;

        EditorCommands.Structural(roots.Count == 1 ? "Group" : $"Group {roots.Count} Entities", () => {
            // Group pivot at the centre of the roots' world positions (Unity-style).
            Vector3 centre = Vector3.Zero;
            foreach (Entity e in roots) centre += e.transform.WorldPosition;
            centre /= roots.Count;

            Entity group = scene.CreateEntity("Group");
            group.transform.WorldPosition = centre;

            // Reparent under the common parent of the roots if they share one, so the group sits where the
            // objects were in the hierarchy; otherwise it's a scene root.
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

    // Captures the entity subtree as a .prefab asset next to the asset browser's current folder, then
    // refreshes so it appears in the browser AND links the live entity to it (Entity.PrefabSource), so
    // the scene object becomes a prefab instance (Unity behaviour). The GUID is assigned by the import,
    // so the link binds in the post-refresh callback (main thread) once TryGetGuid resolves the path.
    void CreatePrefab(Entity entity) {
        if (AssetDatabase.Project is null)
            return;

        string folder = CurrentAssetFolder?.Invoke() ?? "Assets";
        string baseName = string.IsNullOrEmpty(entity.Name) ? "Prefab" : entity.Name;
        string dir = AssetDatabase.Project.ResolveAbsolute(folder);
        Directory.CreateDirectory(dir);

        // Avoid clobbering an existing file: Name, Name 1, Name 2, ... (relative path drives the link).
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

    // Unity-style search: "t:ComponentName" filters to entities that HAVE a component whose type name
    // contains the term (so "t:Light" matches PointLight/SpotLight/DirectionalLight); anything else is
    // a plain case-insensitive name match. The "t:" term may be empty ("t:") — then any entity with at
    // least one component matches, which is a handy "show me things with components" filter.
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

    // Search results as a flat list (hierarchy is meaningless while filtering).
    void DrawFilteredList(Entity[] entities) {
        foreach (Entity entity in entities) {
            if (!MatchesSearch(entity))
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
                    // Single-entity value edit -> scoped through EditorCommands.EditEntity (PushEntity:
                    // selection survives, no whole-scene re-bake), the preferred path for one-entity edits.
                    EditorCommands.EditEntity(entity, "Rename", () => entity.Name = renameBuffer);
                }
                renamingId = -1;
            }
            return;
        }

        var children = Children(entity, allEntities);
        bool selected = state.IsEntitySelected(entity);

        // EF13+EF14 open-state: the tracker is the source of truth, and ONLY parent nodes (leaves have no
        // fold) are tracked. Force ImGui's open state when a Collapse/Expand-All is armed this frame, OR
        // when the node is seen for the FIRST time (default collapsed — covers first scene load and any
        // freshly-created entity without scene-change detection). Otherwise leave ImGui alone so the
        // user's own arrow toggles persist.
        if (children.Count > 0) {
            bool firstSeen = !openState.ContainsKey(id);
            if (pendingForce != ExpandForce.None || firstSeen) {
                bool wantOpen = pendingForce switch {
                    ExpandForce.ExpandAll => true,
                    ExpandForce.CollapseAll => false,
                    _ => false,   // first-seen default = collapsed (EF14)
                };
                ImGui.SetNextItemOpen(wantOpen);
                openState[id] = wantOpen;
            }
        }

        var flags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.SpanAvailWidth |
                    ImGuiTreeNodeFlags.AllowOverlap;
        if (selected) flags |= ImGuiTreeNodeFlags.Selected;
        if (children.Count == 0) flags |= ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen;

        // Label colour priority: inactive greys out, then a prefab-instance root tints blue (Unity's
        // prefab colour), then CHILD entities (anything with a parent) read slightly dimmer than roots
        // so the hierarchy depth is obvious at a glance.
        bool isChild = entity.transform.Parent is not null;
        bool tinted = !entity.IsActive || entity.IsPrefabInstance || isChild;
        if (tinted) {
            SysVec4 col = !entity.IsActive ? ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]
                : entity.IsPrefabInstance ? EditorTheme.PrefabBlue   // Unity's prefab-instance tint
                : EditorTheme.RowChild;   // child: dimmer than a root's white
            ImGui.PushStyleColor(ImGuiCol.Text, col);
        }

        // Leading spaces leave room for the type icon overlaid after the arrow.
        bool open = ImGui.TreeNodeEx($"     {entity.Name}##{id}", flags);

        // Remember ImGui's actual open state so a manual arrow toggle persists across frames (and so the
        // next Collapse/Expand-All starts from the truth). A leaf reports closed but has nothing to track.
        if (children.Count > 0)
            openState[id] = open;

        if (tinted)
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
                : EditorTheme.IconMuted);
            if (EditorIcons.GhostButtonSmall($"eye{id}", EditorIcons.Eye,
                    entity.IsActive ? "Hide (deactivate)" : "Show (activate)")) {
                bool newActive = !entity.IsActive;
                // If the clicked row is part of the multi-selection, toggle the WHOLE selection to the
                // same state (Unity-style); otherwise just this one.
                var batch = selected && state.SelectedEntities.Count > 1
                    ? state.SelectedEntities.ToArray()
                    : new[] { entity };
                // F1 pilot: a multi-entity toggle is structural (whole-scene snapshot), routed through
                // the choke point. Byte-identical to the old manual Push(); the batch read stays outside.
                EditorCommands.Structural(batch.Length > 1 ? $"Toggle Active ({batch.Length})" : "Toggle Active", () => {
                    foreach (Entity e in batch)
                        if (!e.IsDestroyed) e.SetActive(newActive);
                });
                state.MarkViewportDirty();
            }
            ImGui.PopStyleColor();
        }

        if (open && children.Count > 0) {
            // Tree connector lines (code-editor / Unity style): a vertical guide down the indent gutter
            // plus a short horizontal "elbow" into each child row. Drawn after the children so we know
            // their row Ys; the vertical stops at the LAST child's elbow (Unity convention).
            ImDrawListPtr dl = ImGui.GetWindowDrawList();
            uint lineCol = ImGui.GetColorU32(EditorTheme.TreeGuide);
            float gutterX = rowMin.X + ImGui.GetTreeNodeToLabelSpacing() * 0.5f;
            float elbowW = ImGui.GetTreeNodeToLabelSpacing() * 0.42f;
            float halfRow = ImGui.GetFrameHeight() * 0.5f;
            float lastChildY = rowMin.Y;

            foreach (Entity child in children) {
                // Capture the child row's top BEFORE drawing it — a child with its own subtree changes
                // "last item", so derive the elbow Y from the cursor (this child's row top) instead.
                float childTopY = ImGui.GetCursorScreenPos().Y;
                DrawEntityNode(scene, child, allEntities);
                float midY = childTopY + halfRow;
                dl.AddLine(new SysVec2(gutterX, midY), new SysVec2(gutterX + elbowW, midY), lineCol, 1f);
                lastChildY = midY;
            }
            // Vertical guide from just under this node's row down to the last child's elbow.
            dl.AddLine(new SysVec2(gutterX, rowMax.Y), new SysVec2(gutterX, lastChildY), lineCol, 1f);

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
        if (count == 1 && !entity.IsPrefabInstance &&
            ImGui.MenuItem($"{EditorIcons.Package}  Create Prefab")) CreatePrefab(entity);

        // Prefab instance actions (Unity's right-click > Prefab submenu): Apply pushes overrides to the
        // asset, Revert discards them, Select reveals the source .prefab in the browser.
        if (count == 1 && entity.IsPrefabInstance && ImGui.BeginMenu($"{EditorIcons.Package}  Prefab")) {
            if (ImGui.MenuItem("Select Asset")) {
                string p = AssetDatabase.GuidToAssetPath(entity.PrefabSource);
                if (p is not null) state.RequestRevealAsset(p);
            }
            if (ImGui.MenuItem("Apply Overrides")) PrefabInstanceOps.ApplyAll(entity);
            if (ImGui.MenuItem("Revert Overrides")) { PrefabInstanceOps.RevertAll(entity); state.MarkViewportDirty(); }
            ImGui.Separator();
            // F1 pilot: a single-entity edit -- scoped through EditorCommands.EditEntity (maps to
            // PushEntity, byte-identical: selection survives, no whole-scene rebuild).
            if (ImGui.MenuItem("Unpack")) EditorCommands.EditEntity(entity, "Unpack Prefab", () => entity.PrefabSource = Guid.Empty);
            ImGui.EndMenu();
        }
        if (ImGui.MenuItem($"Duplicate{suffix}", "Ctrl+D")) DuplicateSelected(scene);
        if (ImGui.MenuItem($"Group{suffix}", "Ctrl+Shift+G")) GroupSelected(scene);
        if (entity.transform.Parent is not null && ImGui.MenuItem($"Unparent{suffix}")) {
            EditorCommands.Structural("Unparent", () => {
                foreach (Entity e in state.SelectedEntities.ToArray())
                    e.transform.SetParentKeepingWorld(null);
                state.MarkViewportDirty();
            });
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
                EditorCommands.Structural("Reparent", () => dragged.transform.SetParentKeepingWorld(entity.transform));
            }
            // Script asset dropped onto an entity row â†’ add its component (Unity behavior).
            if (AcceptAssetDrop(out List<Guid> droppedAssets))
                AddScriptComponents(entity, droppedAssets);
            ImGui.EndDragDropTarget();
        }
    }

    void AddScriptComponents(Entity entity, List<Guid> guids) {
        // Resolve the addable component types first (pure reads) so the undo snapshot is taken only
        // when at least one will actually be added -- byte-identical to the old lazy "Push on first add".
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

    // Drop target for the SCENE VIEW: call inside a BeginDragDropTarget/EndDragDropTarget block over
    // the viewport image. Accepts the same asset payload the hierarchy does (model → instantiate,
    // prefab → instantiate, script → entity-with-component), so assets can be dragged straight into the
    // 3D view instead of only onto the hierarchy/inspector. Returns true if something was instantiated.
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
        // Resolve the spawnable component types first (pure reads) so the undo snapshot is taken only
        // when at least one entity will actually be created -- byte-identical to the old lazy Push.
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

        EditorCommands.Structural("Add Model", () => {
            Entity last = null;
            foreach (Guid guid in models)
                last = ModelInstantiation.Instantiate(scene, guid) ?? last;
            if (last is not null)
                state.Select(last);
        });
    }

    // Instantiates any dropped .prefab assets into the scene (Unity's drag-prefab-to-hierarchy). The
    // returned roots become the selection. Mirrors InstantiateModels.
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

    // Removes open-state entries whose entity is gone (delete / scene swap). Builds the live-id set from
    // the same id space the tree keys on (InstanceId.GetHashCode()).
    void PruneOpenState(Entity[] entities) {
        var live = new HashSet<int>(entities.Length);
        foreach (Entity e in entities)
            live.Add(e.InstanceId.GetHashCode());
        // Materialize the keys to remove first — can't mutate the dict while enumerating it.
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

    // Creates a root entity and drops it at the scene-view spawn point (a short distance in front of
    // the editor camera, refreshed each frame in EditorApplication) — Unity's create-in-front-of-the-
    // SceneView, instead of every new object piling up at world origin. Only roots are repositioned;
    // entities created as children inherit their parent's frame.
    Entity Spawn(Scene scene, string name) {
        Entity e = scene.CreateEntity(name);
        e.transform.Position = state.SceneSpawnPoint;
        return e;
    }

    // Shared "create" submenu used by the empty-space context menu and the toolbar + button.
    void DrawCreateMenu(Scene scene) {
        if (ImGui.MenuItem("Create Empty")) {
            // F1 pilot: structural create routed through the EditorCommands choke point (byte-identical
            // to the old "Push(); mutate();" -- the snapshot scope is now chosen by EditorCommands, not here).
            EditorCommands.Structural("Create Empty", () => state.Select(Spawn(scene, "Entity")));
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
                        EditorCommands.Structural($"Create {entry.DisplayName}", () => {
                            Entity e = Spawn(scene, entry.DisplayName);
                            e.AddComponent(entry.Type);
                            state.Select(e);
                        });
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

    // Creates a Terrain entity AND its backing assets: a fresh .terrain heightfield next to the asset
    // browser's current folder, plus a shared checker material in Assets/Default (generated once). Both
    // assets import asynchronously, then bind onto the component so the terrain shows the checker
    // immediately. The checker tiles across the terrain so the grid reads at any size.
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

    void DrawSceneBehaviours() {
        Scene scene = SceneManager.GetCurrentScene();

        if (ImGui.Button($"{EditorIcons.Add}  Add Scene Component", new SysVec2(-1, 0)))
            ImGui.OpenPopup("##addscenebehaviour");

        if (ImGui.BeginPopup("##addscenebehaviour")) {
            foreach (ComponentEntry entry in ComponentRegistry.SceneMenu) {
                (string entryIcon, _) = EditorIcons.ForComponentType(entry.Type);
                if (ImGui.MenuItem($"{entryIcon}  {entry.DisplayName}")) {
                    // Scene-wide edit (a SceneBehaviour lives on the Scene, not an entity) -> EditScene.
                    EditorCommands.EditScene($"Add {entry.DisplayName}",
                        () => state.SelectSceneBehaviour(scene.AddSceneBehaviour(entry.Type)));
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
                    EditorCommands.EditScene($"Remove {behaviour.GetType().Name}", () => {
                        scene.RemoveSceneBehaviour(behaviour);
                        if (ReferenceEquals(state.SelectedSceneBehaviour, behaviour))
                            state.SelectSceneBehaviour(null);
                    });
                }
                ImGui.EndPopup();
            }
        }
    }
}
