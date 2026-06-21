using System.Reflection;
using BallisticEngine.AssetPipeline;
using BallisticEngine.AssetPipeline.Loaders;
using BallisticEngine.Editor.Inspector;
using BallisticEngine.Editor.Inspector.AssetInspectors;
using BallisticEngine.Serialization;
using SysVec2 = System.Numerics.Vector2;
using SysVec3 = System.Numerics.Vector3;
using SysVec4 = System.Numerics.Vector4;

namespace BallisticEngine.Editor;

// Inspector: entity editing (transform + reflected component members in a clean two-column
// layout) or asset editing (import settings / material editor) depending on the selection.
// Asset slots support BOTH drag-drop from the Assets panel and a click-to-open picker popup.
// Every interaction pushes an undo snapshot when it starts.
//
// Styling: an entity header card, component headers with type icon + tinted stripe + overlaid
// enable checkbox + a "..." menu, and Unity-style colored X/Y/Z chips on vector rows.
internal sealed class InspectorPanel : EditorWindow, IComponentInspectorHost {
    // Phase-7: the panel draws through the IEditorGui seam (EditorGui.Shared). Static so the internal-static
    // grid/row helpers (Row/BeginGrid/AcceptGuidDrop — shared with ComponentPreviews/AssetInspectors) reach
    // it too. The single stateless seam handle is set once at startup.
    static IEditorGui gui => EditorGui.Shared;

    readonly EditorState state;

    // Shared inspector drawer STACK (Odin-style, B0): one registry of value drawers + a composable, recursive,
    // deterministic drawer stack serve BOTH the component inspector (here) and the volume profile editor, so
    // the two can't drift. The component path keeps its own foldout/grid LAYOUT + [ShowIf]/[Header]/[Space]
    // skip in DrawMemberList (the layout driver); each value ROW runs through componentStack (Enable+terminal,
    // sharing memberRegistry). See BallisticEngine.Editor.Inspector.DrawerStack.
    readonly DrawerRegistry memberRegistry = DrawerRegistry.CreatePrimitive();
    readonly DrawerStack componentStack;
    readonly ImGuiComponentGui componentGui;

    // Pending asset-picker request (opened from an asset slot).
    MemberInfo pickerMember;
    object pickerTarget;
    Type pickerType;
    string pickerSearch = "";
    bool openPicker;
    // G2-editor: when the picker was opened for an IProperty WITHOUT a backing MemberInfo (a collection
    // element asset slot), writes route through this property's Set (-> the collection write-back) instead of
    // the member path. Null for the common member-backed asset slot (which keeps its exact existing path).
    Inspector.IProperty pickerProperty;

    // G1-editor: pending scene-object-picker request (opened from an EntityRef/ComponentRef slot). The slot
    // sets the ref through the IProperty so the picker keeps the property (not the raw member) to write back
    // the chosen EntityRef/ComponentRef value (which routes to ApplyMember + multi-select + dirty).
    Inspector.IProperty sceneRefPickerProperty;
    string sceneRefSearch = "";
    bool openSceneRefPicker;

    string addComponentSearch = "";

    // EF10a — per-component member-search buffers. The search box (shown only on components with enough
    // members — InspectorLayout.MemberSearchThreshold) filters which member rows draw. Keyed by the live
    // component INSTANCE so each visible component keeps its own query independently and the query survives
    // across frames; a ConditionalWeakTable so a removed/destroyed component's entry is collected with it
    // (no manual eviction, no leak). The boxed string holder lets us mutate the value in place by reference.
    readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, StrBox> memberSearch = new();
    sealed class StrBox { public string Value = ""; }

    // EF10b — top-of-inspector component-LIST search. Filters which COMPONENTS draw (by display name) on
    // many-component entities. Unlike the per-component member search there is only ONE component list per
    // shown entity, so a single inspector-owned buffer suffices (no per-instance keying); it carries across
    // selections, which is fine — it's a transient view filter, not persisted state.
    string componentListSearch = "";

    // Inspector lock (Unity's padlock): when on, the inspector pins its current entity so selecting
    // other objects in the hierarchy/viewport doesn't change what's shown. Lock only applies to an
    // entity selection (the common case); asset/scene-behaviour selections always follow.
    bool locked;
    Entity lockedEntity;

    // Distinguishes this inspector's ImGui ids from a second Inspector window's — without it both
    // instances share ids like "inspectorlock", so toggling lock in one toggled the other (and the
    // padlock looked dead). PushID(instanceId) at the top of DrawContents namespaces everything.
    static int instanceCounter;
    readonly int instanceId = instanceCounter++;

    // EditorWindow identity: KEY stays EditorLayout.Inspector (the dock-.ini / sidecar id), DISPLAY is
    // "Details" (EF12). WindowShell owns Begin/End; OnGui routes to the existing DrawContents body.
    protected override void OnGui(IEditorGui gui) => DrawContents();

    public InspectorPanel(EditorState state) {
        DockKey = EditorLayout.Inspector;
        Title = "Details";
        Icon = EditorIcons.Wrench;
        Singleton = false;        // duplicable via the Add-Tab host

        this.state = state;
        componentGui = new ImGuiComponentGui(this);
        // The component-path drawer stack shares memberRegistry as its terminal type-drawer source, so the
        // component inspector and volume profile editor resolve the SAME value drawers (B0).
        componentStack = DrawerStack.CreateComponent(memberRegistry);
        // B4 (Rule 2): the four widget types that used to BYPASS the stack via IsSpecialWidgetType (BEvent /
        // BObject asset-slot / AnimationCurve / ColorGradient) are now TERMINAL drawers on the SAME registry,
        // so the stack resolves them like any primitive and the if/else chain dissolves. They are ImGui-using
        // editor drawers (NOT in CreatePrimitive, which stays headless), registered AFTER the primitives so
        // last-wins resolution prefers them for their (disjoint) types.
        // G4-editor (ch24): a plain nested struct/class member gets the recursive member foldout (was a dead
        // (NestedFoo) Unsupported label). Registered FIRST among the editor drawers so the special CLASS
        // widgets below (BEvent/AnimationCurve/ColorGradient are plain classes -> they too classify Nested)
        // WIN by last-registered-wins -- this drawer is the true fallback for a class/struct with no dedicated
        // drawer.
        memberRegistry.Register(new NestedDrawer(this));
        memberRegistry.Register(new BEventDrawer());
        memberRegistry.Register(new AnimationCurveDrawer(this));
        memberRegistry.Register(new ColorGradientDrawer(this));
        memberRegistry.Register(new AssetSlotDrawer(this));
        // G1-editor: EntityRef/ComponentRef get the interactive scene-object slot (was a dead Unsupported label).
        memberRegistry.Register(new SceneObjectRefDrawer(this));
        // G2-editor: List<T>/T[] get the interactive collection editor (was a dead (List`1) Unsupported label).
        memberRegistry.Register(new CollectionDrawer(this));
        // G2-editor (ch21): Dictionary<K,V> gets the interactive entry editor (was a dead (Dictionary`2) label).
        memberRegistry.Register(new DictionaryDrawer(this));
        // G3-editor (ch23): an interface/abstract [SerializeReference] member gets the implementor dropdown +
        // recursive member foldout (was a dead (IFoo) Unsupported label). Last-wins; no other drawer matches an
        // interface/abstract type, so order vs the primitives is irrelevant.
        memberRegistry.Register(new PolymorphicDrawer(this));
        // The standalone component window reuses our reflection member renderer.
        ComponentEditorWindow.Configure(DrawMemberList);
    }

    // IComponentInspectorHost: lets the shared ImGuiComponentGui adapter reuse this panel's existing
    // helpers (row layout, mixed-value marker, the styled X/Y/Z vector widget, per-widget undo).
    void IComponentInspectorHost.RowWithTooltip(string label, string tooltip) => RowWithTooltip(label, tooltip);
    void IComponentInspectorHost.DrawMixedMarker(MemberInfo member, object target, object value) => DrawMixedMarker(member, target, value);
    bool IComponentInspectorHost.AxisVec3(string id, string label, ref SysVec3 v, float speed) => AxisVec3(id, label, ref v, speed);
    bool IComponentInspectorHost.TrackUndo(string label, bool changed) => InspectorUndo.Track(label, changed);
    void IComponentInspectorHost.MarkViewportDirty() => state.MarkViewportDirty();

    // RW1.1: the relocated component-preview bodies (Inspector/Preview/ComponentPreviews.cs) reach the
    // private EditorState through this so a moved section's `state.MarkViewportDirty()` becomes
    // `ctx.Panel.MarkViewportDirty()` — byte-identical, no behaviour change.
    internal void MarkViewportDirty() => state.MarkViewportDirty();

    // RW1.4: the relocated PrefabAssetInspector body (Inspector/AssetInspectors/) selects the instantiated
    // root through this passthrough (was `state.Select(root)`), same private-EditorState reach as above.
    internal void Select(Entity entity) => state.Select(entity);

    // B4: the AssetSlotDrawer terminal drawer hands its IProperty here; unwrap the reflected member/owner/type
    // and reuse the unchanged DrawAssetSlot rendering (byte-identical to the old IsSpecialWidgetType arm).
    void IComponentInspectorHost.DrawAssetSlot(Inspector.IProperty property) {
        if (property is Inspector.MemberProperty mp)
            DrawAssetSlot(mp.Member, mp.Owner, mp.Get() as BObject, mp.ValueType);
        else
            // G2-editor: a collection element asset slot (no backing MemberInfo) routes through the IProperty,
            // writing the picked/cleared asset via property.Set -> the collection write-back.
            DrawAssetSlotForProperty(property);
    }
    // G1-editor: the SceneObjectRefDrawer terminal drawer hands its IProperty here; the slot reads/writes the
    // EntityRef/ComponentRef value through the IProperty (Get/Set route to ApplyMember + MarkViewportDirty),
    // so a pick/drag broadcasts to the multi-selection exactly like a primitive edit.
    void IComponentInspectorHost.DrawSceneObjectSlot(Inspector.IProperty property) => DrawSceneObjectSlot(property);
    // G2-editor: the CollectionDrawer terminal drawer hands its IProperty here; render the per-element editor.
    void IComponentInspectorHost.DrawCollectionSlot(Inspector.IProperty property) => DrawCollectionSlot(property);
    // G2-editor (ch21): the DictionaryDrawer terminal drawer hands its IProperty here; render the per-entry editor.
    void IComponentInspectorHost.DrawDictionarySlot(Inspector.IProperty property) => DrawDictionarySlot(property);
    // G3-editor (ch23): the PolymorphicDrawer terminal drawer hands its IProperty + declared base type here;
    // render the implementor dropdown + recursive member foldout.
    void IComponentInspectorHost.DrawPolymorphicSlot(Inspector.IProperty property, Type declaredType) => DrawPolymorphicSlot(property, declaredType);
    // G4-editor (ch24): the NestedDrawer terminal drawer hands its IProperty + declared type here; render the
    // recursive member foldout (with struct write-back for value-type instances).
    void IComponentInspectorHost.DrawNestedSlot(Inspector.IProperty property, Type declaredType) => DrawNestedSlot(property, declaredType);

    public void DrawContents() {
        gui.PushId(instanceId);   // namespace all ids so a 2nd Inspector window doesn't collide
        // Denser rows than the global style so more fits on screen.
        gui.PushItemSpacing(new SysVec2(8, 4));
        gui.PushFramePadding(new SysVec2(8, 4));

        DrawLockBar();

        // While locked to a still-alive entity, show IT regardless of the live selection.
        bool showLocked = locked && lockedEntity is not null &&
                          SceneManager.GetCurrentScene().Entities.Contains(lockedEntity);

        if (showLocked) {
            DrawEntityInspector(lockedEntity);
        }
        else if (state.SelectedAssets.Count > 1) {
            DrawMultiAssetInspector();
        }
        else if (state.HasAssetSelection) {
            DrawAssetInspector();
        }
        else if (state.SelectedSceneBehaviour is not null) {
            DrawSceneBehaviourInspector(state.SelectedSceneBehaviour);
        }
        else if (state.Selected is not null) {
            // Multi-selection banner (Unity-style): edit the active entity, with a note that batch
            // hierarchy actions (delete/duplicate/reparent) apply to all selected.
            if (state.SelectedEntities.Count > 1) {
                // RW4: the selection count reads as a small accent chip (badge) instead of inline grey text,
                // so the multi-edit state is glanceable; the explanatory line + divider stay below.
                gui.AlignTextToFramePadding();
                gui.TextDisabled($"{EditorIcons.Package}");
                gui.SameLine(0, 6);
                SysVec4 accent = EditorPrefs.Current.Accent;
                EditorDecoration.DrawBadge($"{state.SelectedEntities.Count} entities", new SysVec4(accent.X, accent.Y, accent.Z, 0.30f));
                gui.TextDisabled("Edits apply to ALL selected (matching components).");
                EditorDecoration.DrawDivider();
            }
            // Scoped undo: ONLY for a single-entity selection (a multi-selection edit broadcasts to
            // several entities, so it must take a full-scene snapshot to undo them all together).
            InspectorUndo.ScopeEntity = state.SelectedEntities.Count == 1 ? state.Selected : null;
            DrawEntityInspector(state.Selected);
            InspectorUndo.ScopeEntity = null;
        }
        else {
            DrawEmptyState();
        }

        if (openPicker) {
            openPicker = false;
            pickerSearch = "";
            gui.OpenPopup("##assetpicker");
        }
        DrawAssetPickerPopup();

        if (openSceneRefPicker) {
            openSceneRefPicker = false;
            sceneRefSearch = "";
            gui.OpenPopup("##scenerefpicker");
        }
        DrawSceneObjectPickerPopup();

        gui.PopStyleVar(2);
        gui.PopId();
    }

    // A slim right-aligned lock toggle at the top of the inspector. Locking pins the current entity so
    // selecting other objects doesn't change what's shown (Unity's padlock).
    void DrawLockBar() {
        float btn = gui.FrameHeight;
        gui.CursorPosX = (gui.CursorPosX + gui.ContentRegionAvail.X - btn);
        if (locked) {
            gui.PushColor(EditorStyleColor.Text, gui.StyleColor(EditorStyleColor.CheckMark));
            if (EditorIcons.GhostButtonSmall("inspectorlock", EditorIcons.Lock, "Inspector locked - click to unlock")) {
                locked = false;
                lockedEntity = null;
            }
            gui.PopColor();
        }
        else {
            gui.PushColor(EditorStyleColor.Text, gui.StyleColor(EditorStyleColor.TextDisabled));
            if (EditorIcons.GhostButtonSmall("inspectorlock", EditorIcons.LockOpen, "Lock inspector to the current entity")) {
                lockedEntity = state.Selected;
                locked = lockedEntity is not null;
            }
            gui.PopColor();
        }
    }

    // Centered hint when nothing is selected, instead of a lone text line in the corner. RW4 (Phase E): the
    // prompt sits inside a faint empty-state card so the panel reads as a crafted surface, not a bare void.
    static void DrawEmptyState() {
        SysVec2 avail = gui.ContentRegionAvail;
        SysVec2 origin = gui.CursorScreenPos;
        // Card spans the content width, centered vertically-ish around the prompt block.
        float cardTop = avail.Y * 0.30f;
        float cardH = MathF.Min(avail.Y * 0.42f, 150f);
        float inset = 14f;
        SysVec2 cardMin = new(origin.X + inset, origin.Y + cardTop);
        SysVec2 cardMax = new(origin.X + avail.X - inset, origin.Y + cardTop + cardH);
        if (cardMax.X > cardMin.X + 8 && cardMax.Y > cardMin.Y + 8)
            EditorDecoration.DrawEmptyCard(cardMin, cardMax);

        gui.Dummy(new SysVec2(0, cardTop + cardH * 0.5f - 34f));
        CenteredIcon(EditorIcons.Search, 34f, new SysVec4(1, 1, 1, 0.08f));
        gui.Spacing();
        CenteredDisabledText("Nothing selected");
        CenteredDisabledText("Select an entity or asset to inspect it.");
    }

    static unsafe void CenteredIcon(string icon, float size, SysVec4 tint) {
        if (!ImGuiController.HasIcons)
            return;
        float w = size; // icon glyphs are roughly square
        gui.CursorPosX = ((gui.WindowWidth - w) * 0.5f);
        SysVec2 pos = gui.CursorScreenPos;
        gui.WindowDrawList.AddText(EditorFont.LargeIcons, size, pos,
            gui.ColorU32(tint), icon);
        gui.Dummy(new SysVec2(w, size));
    }

    static void CenteredDisabledText(string text) {
        float w = gui.CalcTextSize(text).X;
        gui.CursorPosX = (Math.Max(0, (gui.WindowWidth - w) * 0.5f));
        gui.TextDisabled(text);
    }

    // ---- Scene behaviour inspector --------------------------------------------

    void DrawSceneBehaviourInspector(SceneBehaviour behaviour) {
        Type type = behaviour.GetType();
        gui.PushId(behaviour.InstanceId.GetHashCode());

        bool enabled = behaviour.IsEnabled;
        bool open = ComponentHeader(Prettify(type.Name), type, ref enabled, out bool menuRequested);
        if (enabled != behaviour.IsEnabled) EditorCommands.EditScene($"Toggle {Prettify(type.Name)}", () => { behaviour.IsEnabled = enabled; state.MarkViewportDirty(); });

        if (menuRequested)
            gui.OpenPopup("##componentctx");
        var removeClicked = false;
        if (gui.BeginPopup("##componentctx")) {
            if (gui.MenuItem("Remove Component")) removeClicked = true;
            gui.EndPopup();
        }

        if (open) {
            DrawMemberList(type, behaviour);
            // Phase-3 chunk 22: the RenderFeatures scene behaviour gets the dedicated reorderable
            // feature-list widget (its Features member is [HideInInspector], so the generic list above
            // skips it). All other scene behaviours show only their reflected members.
            if (behaviour is RenderFeatures features)
                DrawRenderFeatureList(features);
        }

        if (removeClicked) {
            EditorCommands.EditScene("Remove Component", () => {
                SceneManager.GetCurrentScene().RemoveSceneBehaviour(behaviour);
                state.SelectSceneBehaviour(null);
                state.MarkViewportDirty();
            });
        }

        gui.PopId();
    }

    // ---- Render-feature list (phase-3 chunk 22) -------------------------------

    // Search buffer for the Add Feature popup (mirrors addComponentSearch).
    string addFeatureSearch = "";

    // The reorderable feature list on the RenderFeatures scene behaviour — the editor face of the
    // authored render-feature layer (URP's ScriptableRendererFeature list). Each feature draws as a
    // collapsible sub-card with an Active toggle, up/down/remove controls, and its reflected params via
    // the SAME attribute-driven DrawerPipeline a component uses. Add/remove/reorder/toggle each push
    // EditorUndo and dirty the viewport, exactly like every other editor mutation; the backend bridge
    // rebuilds the graph's feature segment when the active set or order changes (chunk 20).
    void DrawRenderFeatureList(RenderFeatures host) {
        List<RenderFeature> list = host.Features ??= new();

        EditorDecoration.DrawSectionHeader("Render Features");

        if (list.Count == 0)
            gui.TextDisabled("No render features. Add one below.");

        int moveFrom = -1, moveTo = -1, removeAt = -1;

        for (var i = 0; i < list.Count; i++) {
            RenderFeature feature = list[i];
            if (feature is null) continue;
            Type ft = feature.GetType();
            gui.PushId(i);

            // Active toggle (left of the header).
            bool active = feature.Active;
            if (gui.Checkbox("##featactive", ref active)) {
                EditorUndo.Push(active ? "Enable Render Feature" : "Disable Render Feature");
                feature.Active = active;
                state.MarkViewportDirty();
            }
            gui.SameLine();

            // Collapsible feature header carrying the (display) type name.
            string display = ComponentRegistry.RenderFeatureMenu
                .FirstOrDefault(e => e.Type == ft).DisplayName ?? Prettify(ft.Name);
            gui.PushColor(EditorStyleColor.Text,
                gui.StyleColor(active ? EditorStyleColor.Text : EditorStyleColor.TextDisabled));
            bool open = gui.CollapsingHeader($"{display}###feathdr{i}", defaultOpen: true);
            gui.PopColor();

            // Reorder + remove controls (right-aligned on the header row). After SameLine,
            // GetContentRegionAvail().X is the width remaining to the right edge — reserve three
            // small buttons' worth and push the cursor there (same idiom as DrawLockBar).
            float btnW = gui.FrameHeight;
            gui.SameLine();
            gui.CursorPosX = (gui.CursorPosX + gui.ContentRegionAvail.X - btnW * 3 - 8);
            gui.BeginDisabled(i == 0);
            if (gui.SmallButton("^##fup")) { moveFrom = i; moveTo = i - 1; }
            gui.EndDisabled();
            if (gui.IsItemHovered()) gui.Tooltip("Move up");
            gui.SameLine();
            gui.BeginDisabled(i == list.Count - 1);
            if (gui.SmallButton("v##fdn")) { moveFrom = i; moveTo = i + 1; }
            gui.EndDisabled();
            if (gui.IsItemHovered()) gui.Tooltip("Move down");
            gui.SameLine();
            gui.PushColor(EditorStyleColor.Text, EditorIcons.AxisX);
            if (gui.SmallButton($"{EditorIcons.Delete}##frm")) removeAt = i;
            gui.PopColor();
            if (gui.IsItemHovered()) gui.Tooltip("Remove feature");

            // The feature's reflected params (Event/Tint/Strength/…) via the shared drawer pipeline.
            // Undo for a param edit is taken per-widget by the same InspectorUndo path components use.
            if (open) {
                gui.Indent();
                DrawMemberList(ft, feature);
                gui.Unindent();
            }

            gui.PopId();
        }

        // Apply structural changes AFTER the loop (mutating the list mid-iteration is unsafe).
        if (removeAt >= 0) {
            EditorUndo.Push("Remove Render Feature");
            list.RemoveAt(removeAt);
            state.MarkViewportDirty();
        }
        else if (moveFrom >= 0 && moveTo >= 0 && moveTo < list.Count) {
            EditorUndo.Push("Reorder Render Feature");
            (list[moveFrom], list[moveTo]) = (list[moveTo], list[moveFrom]);
            state.MarkViewportDirty();
        }

        gui.Spacing();
        DrawAddFeatureButton(host);
    }

    void DrawAddFeatureButton(RenderFeatures host) {
        float avail = gui.ContentRegionAvail.X;
        float w = Math.Clamp(avail * 0.72f, 180f, 320f);
        gui.CursorPosX = (gui.CursorPosX + (avail - w) * 0.5f);

        SysVec4 accent = gui.StyleColor(EditorStyleColor.CheckMark);
        gui.PushColor(EditorStyleColor.Button, new SysVec4(accent.X, accent.Y, accent.Z, 0.16f));
        gui.PushColor(EditorStyleColor.ButtonHovered, new SysVec4(accent.X, accent.Y, accent.Z, 0.30f));
        gui.PushColor(EditorStyleColor.ButtonActive, new SysVec4(accent.X, accent.Y, accent.Z, 0.42f));
        var clicked = gui.Button($"{EditorIcons.Add}  Add Feature", new SysVec2(w, 0));
        gui.PopColor(3);

        if (clicked) {
            addFeatureSearch = "";
            gui.OpenPopup("##addfeature");
        }

        DrawAddFeaturePopup(host);
    }

    // Mirrors DrawAddComponentPopup, but lists ComponentRegistry.RenderFeatureMenu and appends a fresh
    // instance to the host's feature list (URP lets the same feature type be added multiple times, so no
    // "already present" filtering — duplicates are allowed by design, §5 D1).
    void DrawAddFeaturePopup(RenderFeatures host) {
        float u = gui.FontSize;
        gui.SetNextWindowSizeAppearing(new SysVec2(u * 26f, u * 24f));
        if (!gui.BeginPopup("##addfeature"))
            return;

        gui.PushItemSpacing(new SysVec2(8, 6));

        gui.PushFont(EditorFont.Bold);
        gui.TextUnformatted("Add Render Feature");
        gui.PopFont();
        gui.Spacing();

        if (gui.IsWindowAppearing())
            gui.SetKeyboardFocusHere();
        gui.SetNextItemWidth(-1);
        gui.InputTextWithHint("##addfeatsearch", $"{EditorIcons.Search} Search features...",
            ref addFeatureSearch, 128);
        bool enter = gui.IsItemFocused() && gui.KeyPressed(EditorGuiKey.Enter);
        gui.Separator();

        bool searching = addFeatureSearch.Length > 0;
        bool Matches(ComponentEntry e) =>
            !searching || e.DisplayName.Contains(addFeatureSearch, StringComparison.OrdinalIgnoreCase);

        void Add(ComponentEntry e) {
            if (Activator.CreateInstance(e.Type) is not RenderFeature feature)
                return;
            EditorUndo.Push($"Add {e.DisplayName}");
            (host.Features ??= new()).Add(feature);
            state.MarkViewportDirty();
            gui.CloseCurrentPopup();
        }

        gui.BeginChild("##addfeatlist", default, border: false);

        IReadOnlyList<ComponentEntry> entries = ComponentRegistry.RenderFeatureMenu;
        if (entries.Count == 0) {
            gui.TextDisabled("No render features are defined.");
            gui.TextDisabled("Author a RenderFeature subclass in your project scripts.");
        }
        else {
            ComponentEntry? first = null;
            var any = false;
            foreach (ComponentEntry entry in entries) {
                if (!Matches(entry)) continue;
                any = true;
                first ??= entry;
                if (AddComponentRow(entry))
                    Add(entry);
            }
            if (!any)
                gui.TextDisabled("No features match.");
            if (enter && first is { } f)
                Add(f);
        }

        gui.EndChild();
        gui.PopStyleVar();
        gui.EndPopup();
    }

    // ---- Entity inspector ----------------------------------------------------

    void DrawEntityInspector(Entity entity) {
        Behaviour[] behaviours = entity.Behaviours.ToArray();
        DrawEntityHeaderCard(entity, behaviours.Length);

        // Prefab instance: recompute the override diff (cached per selection) and show the prefab bar
        // with Open / Select / Apply All / Revert All (Unity's prefab instance header).
        PrefabOverrides.Refresh(entity);
        if (entity.IsPrefabInstance)
            DrawPrefabInstanceBar(entity);

        DrawTagLayerRow(entity);

        gui.Spacing();

        DrawTransform(entity.transform);

        // EF10b — conditional component-LIST search. Shown only when the entity carries enough components to
        // be worth filtering (don't clutter a 2-3 component entity — InspectorLayout.ComponentSearchThreshold).
        // The box sits ABOVE the first component header and only decides which whole components draw; it does
        // NOT touch the per-component member search (EF10a) or the column model (EF11/EF16). The filter matches
        // the component's DISPLAYED title (Prettify(type.Name) — the same string ComponentHeader shows). No
        // query → componentMatch passes everyone → byte-identical to pre-EF10b.
        string componentQuery = "";
        if (behaviours.Length > Inspector.InspectorLayout.ComponentSearchThreshold) {
            if (EditorWidgets.SearchField("##componentsearch", "Search components...", ref componentListSearch))
                state.MarkViewportDirty();
            componentQuery = componentListSearch;
            gui.Spacing();
        }

        bool ComponentMatch(Behaviour b) =>
            componentQuery.Length == 0 ||
            Prettify(b.GetType().Name).Contains(componentQuery, StringComparison.OrdinalIgnoreCase);

        var typeIndex = new Dictionary<Type, int>();
        var componentOrdinal = 0;   // running count of DRAWN components, for alternating body-bg banding
        foreach (Behaviour behaviour in behaviours) {
            Type bt = behaviour.GetType();
            int idx = typeIndex.TryGetValue(bt, out int i) ? i : 0;
            typeIndex[bt] = idx + 1;
            // EF10b — keep the type-index counter accurate (prefab-override/multi-select keying uses the
            // Nth-of-type index) even for components the filter hides, so hiding a component never shifts a
            // visible sibling's index. Increment FIRST (above), then skip the draw.
            if (!ComponentMatch(behaviour)) continue;
            // Breathing room between component blocks (the banding reads as separate cards). Skip before
            // the first one so it sits snug under the transform/tag rows.
            if (componentOrdinal > 0)
                gui.Dummy(new SysVec2(0, 6 * EditorTheme.UiScale));
            DrawComponent(entity, behaviour, idx, componentOrdinal++);
        }

        gui.Spacing();
        gui.Spacing();
        DrawAddComponent(entity);
        gui.Spacing();
    }

    // True if any member of this component differs from the prefab definition (drives the header dot).
    static bool ComponentHasOverride(Behaviour behaviour, int typeIndex) =>
        PrefabOverrides.ComponentHasOverride(ComponentRegistry.NameOf(behaviour), typeIndex);

    // Prefab-instance header bar (Unity's blue prefab strip): the source name + Select (reveal the
    // .prefab in the browser), Open (load it for editing), and Apply All / Revert All which push or
    // discard this instance's overrides against the asset. Greyed when there are no overrides.
    void DrawPrefabInstanceBar(Entity entity) {
        string path = AssetDatabase.GuidToAssetPath(entity.PrefabSource);
        string name = path is null ? "(missing prefab)" : Path.GetFileNameWithoutExtension(path);

        // Justified literal (EF5d): a deep, desaturated navy SURFACE backing for Unity's prefab strip — a
        // dark low-alpha bar, NOT an alpha of the bright PrefabBlue foreground; the blue TEXT/dot above use
        // the EditorTheme.PrefabBlue token. (A direct alpha of PrefabBlue would read far too bright here.)
        gui.PushColor(EditorStyleColor.ChildBg, new SysVec4(0.16f, 0.22f, 0.34f, 0.55f));
        gui.BeginChildAutoResizeY("##prefabbar", border: false);
        gui.PushColor(EditorStyleColor.Text, EditorTheme.PrefabBlue);
        gui.AlignTextToFramePadding();
        gui.TextUnformatted($"{EditorIcons.Package}  Prefab: {name}");
        gui.PopColor();

        if (path is not null) {
            gui.SameLine();
            if (gui.SmallButton("Select")) state.RequestRevealAsset(path);
        }

        bool hasOverrides = PrefabOverrides.HasAnyOverride;
        gui.BeginDisabled(!hasOverrides || path is null);
        gui.SameLine();
        if (gui.SmallButton("Apply All")) PrefabInstanceOps.ApplyAll(entity);
        gui.SameLine();
        if (gui.SmallButton("Revert All")) { PrefabInstanceOps.RevertAll(entity); state.MarkViewportDirty(); }
        gui.EndDisabled();

        gui.EndChild();
        gui.PopColor();
        gui.Spacing();
    }

    // Rounded card with the entity's type icon, active checkbox, name field and a meta line.
    unsafe void DrawEntityHeaderCard(Entity entity, int componentCount) {
        var draw = gui.WindowDrawList;
        SysVec2 avail = gui.ContentRegionAvail;
        SysVec2 cardMin = gui.CursorScreenPos;

        float pad = 10f;
        float frameH = gui.FrameHeight;
        // RW2: row 1 hosts the name field in the larger Header font, so its frame is taller than the
        // default frameH — size the card from the header frame height so the bigger title never clips.
        float headerFrameH = gui.FontSizeOf(EditorFont.Header) + gui.FramePadding.Y * 2;
        float row1H = MathF.Max(frameH, headerFrameH);
        float cardH = pad + row1H + 4 + gui.TextLineHeight + pad;
        SysVec2 cardMax = cardMin + new SysVec2(avail.X, cardH);

        // RW4 (Phase E decoration): the entity-header card surface now comes from the shared
        // EditorDecoration primitive (palette-fed) instead of two inline hex AddRectFilled/AddRect — same
        // rect + rounding, so layout is byte-unchanged; the only delta is the border hue now tracks the
        // theme hairline rather than a hard inline black, matching the component-header chrome.
        EditorDecoration.DrawCard(cardMin, cardMax, 6f);

        // Big type icon on the left.
        (string icon, SysVec4 tint) = EditorIcons.ForEntity(entity);
        float iconSize = cardH - pad * 2 + 6;
        float contentX = cardMin.X + pad;
        if (ImGuiController.HasIcons) {
            draw.AddText(EditorFont.LargeIcons, iconSize,
                new SysVec2(cardMin.X + pad, cardMin.Y + (cardH - iconSize) * 0.5f),
                gui.ColorU32(entity.IsActive ? tint : new SysVec4(tint.X, tint.Y, tint.Z, 0.4f)),
                icon);
            contentX += iconSize + pad;
        }

        // Row 1: active checkbox + name field.
        gui.SetCursorScreenPos(new SysVec2(contentX, cardMin.Y + pad));
        bool active = entity.IsActive;
        if (gui.Checkbox("##active", ref active)) { }
        // Snapshot fires on activation; the SetActive mutate lands on a later branch/frame, so the
        // grab-frame snapshot is preserved with a no-op mutate (Push->PushEntity scoping aside, byte-identical).
        if (gui.IsItemActivated()) EditorCommands.EditEntity(entity, "Toggle Active", () => { });
        if (active != entity.IsActive) { entity.SetActive(active); state.MarkViewportDirty(); }
        if (gui.IsItemHovered())
            gui.Tooltip("Active");

        gui.SameLine();
        gui.SetNextItemWidth(cardMax.X - pad - gui.CursorScreenPos.X);
        gui.PushColor(EditorStyleColor.FrameBg, new SysVec4(0, 0, 0, 0.30f));
        // RW2: the entity NAME is the top of the inspector's type hierarchy — draw it in the Header
        // (semibold, larger) font so it reads as the title, not just another body-size field.
        gui.PushFont(EditorFont.Header);
        var name = entity.Name ?? "";
        var renamed = gui.InputText("##name", ref name, 128);
        gui.PopFont();
        gui.PopColor();
        // Snapshot on activation; the rename mutate (entity.Name) lands on a later edit frame, so the
        // grab-frame snapshot is preserved with a no-op mutate.
        if (gui.IsItemActivated()) EditorCommands.EditEntity(entity, "Rename", () => { });
        if (renamed) entity.Name = name;

        // Row 2: meta line. RW2: caption font + recessive caption color so it reads as secondary metadata.
        gui.SetCursorScreenPos(new SysVec2(contentX, cardMin.Y + pad + row1H + 4));
        gui.PushFont(EditorFont.Caption);
        gui.PushColor(EditorStyleColor.Text, EditorTheme.RowCaption);
        gui.TextUnformatted(componentCount == 1 ? "1 component" : $"{componentCount} components");
        gui.PopColor();
        gui.PopFont();

        // Reserve the card's space in the layout — and make it a SCRIPT DROP TARGET: dragging a .cs
        // tile from the asset browser onto the header adds that component to the entity (Unity parity;
        // the hierarchy already accepts this, the inspector didn't).
        gui.SetCursorScreenPos(cardMin);
        gui.Dummy(new SysVec2(avail.X, cardH));
        AcceptScriptDrop(entity);
    }

    // Drop target for .cs script tiles (asset-browser drag payload = ';'-separated GUIDs). Each that
    // resolves to a compiled Behaviour type is added as a component (skipping dupes), one undo step.
    void AcceptScriptDrop(Entity entity) {
        if (!gui.BeginDragDropTarget())
            return;
        string text = gui.AcceptDragDropPayloadString(AssetBrowserPanel.DragType);
        if (text is not null) {
            // Resolve the addable script types FIRST (pure read), so the structural snapshot is taken
            // exactly once before any AddComponent -- byte-identical to the old lazy `pushed` flag, but
            // the snapshot+mutate stay atomic inside EditorCommands.Structural.
            var toAdd = new List<Type>();
            foreach (string part in text?.Split(';', StringSplitOptions.RemoveEmptyEntries) ?? []) {
                if (!Guid.TryParse(part, out Guid guid)) continue;
                Type type = HierarchyPanel.ScriptComponentType(guid);
                if (type is null || HasComponentOfType(entity, type)) continue;
                toAdd.Add(type);
            }
            if (toAdd.Count > 0)
                EditorCommands.Structural("Add Script Component", () => {
                    foreach (Type type in toAdd)
                        entity.AddComponent(type);
                    state.MarkViewportDirty();
                });
        }
        gui.EndDragDropTarget();
    }

    // Unity-style Tag + Layer row under the entity header. Both are entity state serialized in the
    // scene, so edits push a scene undo and mark the viewport dirty. Tag options come from TagManager;
    // Layer options from LayerManager.DefinedLayers() (named layers only).
    void DrawTagLayerRow(Entity entity) {
        gui.Spacing();
        float half = (gui.ContentRegionAvail.X - gui.ItemSpacing.X) * 0.5f;

        // Tag combo.
        gui.SetNextItemWidth(half);
        string currentTag = string.IsNullOrEmpty(entity.Tag) ? TagManager.Untagged : entity.Tag;
        if (gui.BeginCombo("##tag", $"{EditorIcons.Pin} {currentTag}")) {
            foreach (string tag in TagManager.Tags) {
                if (gui.Selectable(tag, tag == currentTag) && tag != entity.Tag) {
                    EditorCommands.EditEntity(entity, "Change Tag", () => {
                        entity.Tag = tag;
                        state.MarkViewportDirty();
                    });
                }
            }
            // EF7: "Add Tag…" at the bottom opens the Tags & Layers project window (where tags are
            // defined). New tags persist in TagManager and appear in this dropdown next frame. Routed
            // through the EditorWindows facade so the inspector needs no reference to the window/app.
            gui.Separator();
            if (gui.Selectable($"{EditorIcons.Add} Add Tag..."))
                EditorWindows.Open(EditorMenus.WindowKeys.TagsLayers);
            gui.EndCombo();
        }
        if (gui.IsItemHovered()) gui.Tooltip("Tag");

        gui.SameLine();

        // Layer combo (named layers only).
        gui.SetNextItemWidth(half);
        string currentLayerName = LayerManager.NameOf(entity.Layer);
        if (gui.BeginCombo("##layer", $"{EditorIcons.Grid} {currentLayerName}")) {
            foreach ((int index, string name) in LayerManager.DefinedLayers()) {
                if (gui.Selectable($"{index}: {name}", index == entity.Layer) && index != entity.Layer) {
                    EditorCommands.EditEntity(entity, "Change Layer", () => {
                        entity.Layer = index;
                        state.MarkViewportDirty();
                    });
                }
            }
            // EF7: "Add Layer…" opens the same Tags & Layers window (layers are named there); a newly
            // named layer shows up in this dropdown next frame via LayerManager.DefinedLayers().
            gui.Separator();
            if (gui.Selectable($"{EditorIcons.Add} Add Layer..."))
                EditorWindows.Open(EditorMenus.WindowKeys.TagsLayers);
            gui.EndCombo();
        }
        if (gui.IsItemHovered()) gui.Tooltip("Layer");
    }

    void DrawTransform(Transform transform) {
        bool open = PlainHeader("Transform");

        // Prefab override marker on the Transform header: a blue dot if any of pos/rot/scale differ
        // from the prefab, with a right-click "Revert" per channel further down (the most common
        // override). Drawn as a small badge after the header so it doesn't disturb layout.
        bool posOv = PrefabOverrides.IsOverridden(PrefabOverrides.TransformPositionKey);
        bool rotOv = PrefabOverrides.IsOverridden(PrefabOverrides.TransformRotationKey);
        bool sclOv = PrefabOverrides.IsOverridden(PrefabOverrides.TransformScaleKey);
        if (posOv || rotOv || sclOv) {
            SysVec2 hp = gui.ItemRectMax;
            gui.WindowDrawList.AddCircleFilled(
                new SysVec2(hp.X - 12, (gui.ItemRectMin.Y + hp.Y) * 0.5f), 3.5f,
                gui.ColorU32(EditorTheme.PrefabBlue));
        }

        // The other selected entities' transforms, if this is a multi-selection — edits apply to all
        // of them (Unity-style: a field change moves the whole group by the same DELTA, preserving
        // relative offsets). Empty for a single selection.
        var others = MultiTransforms(transform);

        // Right-click the header for Unity-style resets (apply to the whole selection).
        if (gui.BeginPopupContextItem("##transformctx")) {
            // These reset the WHOLE multi-selection (transform + every `others`), so they stay a
            // whole-scene structural snapshot (not a single-entity EditEntity).
            if (gui.MenuItem("Reset Position")) EditorCommands.Structural("Reset Position", () => { transform.Position = Vector3.Zero; foreach (Transform o in others) o.Position = Vector3.Zero; });
            if (gui.MenuItem("Reset Rotation")) EditorCommands.Structural("Reset Rotation", () => { transform.EulerAngles = Vector3.Zero; foreach (Transform o in others) o.EulerAngles = Vector3.Zero; });
            if (gui.MenuItem("Reset Scale")) EditorCommands.Structural("Reset Scale", () => { transform.Scale = Vector3.One; foreach (Transform o in others) o.Scale = Vector3.One; });
            gui.Separator();
            if (gui.MenuItem("Reset All")) {
                EditorCommands.Structural("Reset Transform", () => {
                    transform.Position = Vector3.Zero; transform.EulerAngles = Vector3.Zero; transform.Scale = Vector3.One;
                    foreach (Transform o in others) { o.Position = Vector3.Zero; o.EulerAngles = Vector3.Zero; o.Scale = Vector3.One; }
                });
            }
            gui.EndPopup();
        }

        if (!open)
            return;

        if (BeginGrid("##transform")) {
            // The ACTIVE entity always takes the typed value verbatim (no representation drift). The
            // OTHER selected entities receive the same DELTA, so the group moves rigidly and keeps its
            // relative offsets. Rotation composes via QUATERNION (Euler add flips representations).
            SysVec3Row("Position", transform.Position, v => {
                Vector3 d = v - transform.Position; transform.Position = v;
                foreach (Transform o in others) o.Position += d;
            }, 0.05f);
            SysVec3Row("Rotation", transform.EulerAngles, v => {
                Quaternion oldQ = transform.Rotation;
                transform.EulerAngles = v;
                if (others.Count > 0) {
                    Quaternion delta = transform.Rotation * Quaternion.Inverse(oldQ);
                    foreach (Transform o in others) o.Rotation = delta * o.Rotation;
                }
            }, 0.5f);
            SysVec3Row("Scale", transform.Scale, v => {
                Vector3 d = v - transform.Scale; transform.Scale = v;
                foreach (Transform o in others) o.Scale += d;
            }, 0.05f, allowUniformLock: true);
            gui.EndTable();
        }

        gui.Spacing();
    }

    // The transforms of the OTHER selected entities (everything except `active`), when more than one
    // entity is selected. Used to broadcast Transform edits across a multi-selection.
    List<Transform> MultiTransforms(Transform active) {
        var list = new List<Transform>();
        if (state.SelectedEntities.Count <= 1)
            return list;
        foreach (Entity e in state.SelectedEntities)
            if (e?.transform is { } t && !ReferenceEquals(t, active) && !e.IsDestroyed)
                list.Add(t);
        return list;
    }

    // Framed header with an accent stripe and a bold label, no enable checkbox (Transform).
    static unsafe bool PlainHeader(string label) {
        gui.Spacing();
        gui.PushFramePadding(new SysVec2(10, 7));
        float labelX = gui.TreeNodeToLabelSpacing;
        bool open = gui.CollapsingHeaderFramed($"###hdr_{label}");
        gui.PopStyleVar();

        SysVec2 min = gui.ItemRectMin;
        SysVec2 max = gui.ItemRectMax;
        var draw = gui.WindowDrawList;
        // RW4: the accent stripe goes through the shared decoration primitive (same 3px geometry), keeping
        // the CheckMark accent color this header has always used.
        EditorDecoration.DrawAccentStripe(min, max.Y - min.Y, gui.StyleColor(EditorStyleColor.CheckMark));
        draw.AddText(EditorFont.Bold, gui.FontSize,
            new SysVec2(min.X + labelX, min.Y + (max.Y - min.Y - gui.FontSize) * 0.5f),
            gui.ColorU32(gui.StyleColor(EditorStyleColor.Text)), label);
        return open;
    }

    void DrawComponent(Entity entity, Behaviour behaviour, int typeIndex = 0, int componentOrdinal = 0) {
        Type type = behaviour.GetType();
        gui.PushId(behaviour.InstanceId.GetHashCode());

        // Component-level background banding: paint each component's WHOLE body (header + members) in an
        // alternating tint so adjacent components separate as blocks (the grouping the user asked for),
        // replacing the per-row zebra. Immediate mode can't draw behind content we haven't laid out yet,
        // so split the window draw list: content goes on channel 1, and after we know the body's height we
        // fill the band on channel 0 (behind it), then merge. The bands are a hair lighter/darker than the
        // panel so they read as "two close tones", not stripes.
        var draw = gui.WindowDrawList;
        draw.ChannelsSplit(2);
        draw.ChannelsSetCurrent(1);
        SysVec2 bandStart = gui.CursorScreenPos;

        bool enabled = behaviour.IsEnabled;
        bool open = ComponentHeader(Prettify(type.Name), type, ref enabled, out bool menuRequested);

        // Prefab override badge on the component header: a blue dot if ANY member of this component
        // (matched by registry name + type-index) differs from the prefab definition.
        if (entity.IsPrefabInstance && ComponentHasOverride(behaviour, typeIndex)) {
            SysVec2 mx = gui.ItemRectMax;
            gui.WindowDrawList.AddCircleFilled(
                new SysVec2(mx.X - 30, (gui.ItemRectMin.Y + mx.Y) * 0.5f), 3.5f,
                gui.ColorU32(EditorTheme.PrefabBlue));
        }
        if (enabled != behaviour.IsEnabled) {
            // Toggle propagates to the matching component on every selected entity (multi-select), so
            // it stays a whole-scene structural snapshot rather than a single-entity EditEntity.
            EditorCommands.Structural($"Toggle {Prettify(type.Name)}", () => {
                behaviour.IsEnabled = enabled;
                foreach (Behaviour sibling in MatchingComponents(behaviour))
                    sibling.IsEnabled = enabled;
                state.MarkViewportDirty();
            });
        }

        if (menuRequested)
            gui.OpenPopup("##componentctx");

        var removeClicked = false;
        if (gui.BeginPopup("##componentctx")) {
            int index = entity.Behaviours.IndexOf(behaviour);
            gui.BeginDisabled(index <= 0);
            if (gui.MenuItem($"{EditorIcons.ChevronRight}  Move Up")) MoveComponent(entity, behaviour, -1);
            gui.EndDisabled();
            gui.BeginDisabled(index < 0 || index >= entity.Behaviours.Count - 1);
            if (gui.MenuItem($"{EditorIcons.ChevronRight}  Move Down")) MoveComponent(entity, behaviour, +1);
            gui.EndDisabled();
            gui.Separator();
            if (gui.MenuItem($"{EditorIcons.Refresh}  Reset")) ResetComponent(behaviour);
            if (gui.MenuItem($"{EditorIcons.Document}  Copy Component")) CopyComponent(behaviour);
            gui.BeginDisabled(!CanPasteInto(type));
            if (gui.MenuItem($"{EditorIcons.Add}  Paste Component Values")) PasteComponent(behaviour);
            gui.EndDisabled();

            // [ContextMenu] methods (Unity's): each parameterless [ContextMenu]-marked method shows
            // here and runs on click, ScriptGuard-protected so a throwing one can't take the editor down.
            bool firstCtx = true;
            foreach (MethodInfo ctxMethod in ComponentReflection.InspectorContextMenus(type)) {
                if (firstCtx) { gui.Separator(); firstCtx = false; }
                string ctxLabel = ctxMethod.GetCustomAttribute<ContextMenuAttribute>()?.Label ?? Prettify(ctxMethod.Name);
                if (gui.MenuItem($"{EditorIcons.Wrench}  {ctxLabel}")) {
                    EditorCommands.EditEntity(entity, ctxLabel, () => {
                        try { ctxMethod.Invoke(behaviour, null); }
                        catch (Exception ex) { Debugging.LogError($"[ContextMenu] '{ctxLabel}' threw: {ex.InnerException?.Message ?? ex.Message}"); }
                        state.MarkViewportDirty();
                    });
                }
            }

            // Edit Script — for game-script components (compiled into GameScripts.dll), open the
            // backing .cs in the OS's default C# editor (item 9). Engine components have no source file.
            if (IsGameScript(type)) {
                gui.Separator();
                if (gui.MenuItem($"{EditorIcons.Code}  Edit Script"))
                    OpenComponentScript(type);
            }

            gui.Separator();
            if (gui.MenuItem($"{EditorIcons.Delete}  Remove Component")) removeClicked = true;
            gui.EndPopup();
        }

        if (removeClicked) {
            // Removal propagates to the matching component on every selected entity (multi-select), so
            // it stays a whole-scene structural snapshot.
            EditorCommands.Structural("Remove Component", () => {
                foreach (Behaviour sibling in MatchingComponents(behaviour))
                    sibling.Entity.RemoveComponent(sibling);
                entity.RemoveComponent(behaviour);
                state.MarkViewportDirty();
            });
            draw.ChannelsMerge();   // balance the split before the early-out (component is being removed)
            gui.PopId();
            return;
        }

        if (open) {
            DrawMemberList(type, behaviour);

            // Custom per-component preview sections (B1, Rule 1): resolved from ComponentPreviewRegistry by
            // type instead of the old `if (behaviour is Renderer/Volume/Terrain/...)` instanceof chain. Each
            // applicable preview self-registered via [ComponentPreview(typeof(T))]; PreviewsFor caches the
            // ordered list per component type (zero per-frame reflection). A plain component resolves to an
            // empty list and just shows its members above.
            var previewCtx = new Inspector.Preview.ComponentPreviewContext(this, entity, behaviour);
            foreach (var preview in BallisticEngine.Editor.ComponentPreviewRegistry.PreviewsFor(type))
                preview.Draw(in previewCtx);

            gui.Spacing();
        }

        // Fill the component's body band behind everything (channel 0), then merge. Span the full panel
        // width regardless of inner tables. Even/odd ordinal alternates between two close tints so each
        // component is its own block and the next is a near neighbour, not a hard contrast.
        float bandEndY = gui.CursorScreenPos.Y;
        float wx0 = gui.WindowPos.X;
        float wx1 = wx0 + gui.WindowSize.X;
        // Alternate the body wash by ordinal (1-a, 2-b, 3-a ...) so adjacent components separate as two
        // close tones, not one flat wash: even ordinals get the lighter card, odd ones a touch darker.
        SysVec4 band = (componentOrdinal & 1) == 0
            ? new SysVec4(1f, 1f, 1f, 0.05f)
            : new SysVec4(0f, 0f, 0f, 0.06f);
        draw.ChannelsSetCurrent(0);
        draw.AddRectFilled(new SysVec2(wx0, bandStart.Y - 2), new SysVec2(wx1, bandEndY + 4),
                           gui.ColorU32(band), 6f);
        draw.ChannelsMerge();

        gui.PopId();
    }

    // ---- Component reorder / copy-paste / reset (Unity-style "..." menu) ------

    // In-process component clipboard: the source type + a member-name -> value snapshot, captured by
    // reflection. Paste applies matching members onto a same-type (or assignable) component.
    static Type clipboardType;
    static readonly Dictionary<string, object> clipboardMembers = new();

    // Swaps the component with its neighbor in the entity's behaviour list (changes inspector order;
    // serialized so it round-trips). dir = -1 up, +1 down.
    void MoveComponent(Entity entity, Behaviour behaviour, int dir) {
        var list = entity.Behaviours;
        int i = list.IndexOf(behaviour);
        int j = i + dir;
        if (i < 0 || j < 0 || j >= list.Count)
            return;
        EditorCommands.Structural("Reorder Component", () => {
            (list[i], list[j]) = (list[j], list[i]);
            state.MarkViewportDirty();
        });
    }

    // Resets every inspector member to a fresh instance's defaults (Unity's Reset). Lifecycle members
    // (IsEnabled, attach state) are untouched — only the reflected, editable members.
    void ResetComponent(Behaviour behaviour) {
        Type type = behaviour.GetType();
        Behaviour fresh;
        try { fresh = (Behaviour)Activator.CreateInstance(type); }
        catch { return; }
        // Reset rewrites just this one component's members on its own entity -> scoped EditEntity.
        EditorCommands.EditEntity(behaviour.Entity, $"Reset {Prettify(type.Name)}", () => {
            foreach (MemberInfo member in ComponentReflection.InspectorMembers(type)) {
                try { ComponentReflection.SetValue(member, behaviour, ComponentReflection.GetValue(member, fresh)); }
                catch { /* read-only / computed member -- skip */ }
            }
            state.MarkViewportDirty();
        });
    }

    // Snapshots the component's inspector members into the clipboard.
    static void CopyComponent(Behaviour behaviour) {
        clipboardType = behaviour.GetType();
        clipboardMembers.Clear();
        foreach (MemberInfo member in ComponentReflection.InspectorMembers(clipboardType))
            clipboardMembers[member.Name] = ComponentReflection.GetValue(member, behaviour);
    }

    // Paste is allowed when the clipboard holds a type assignable to the target (same component, or a
    // base type's values onto a derived one).
    static bool CanPasteInto(Type targetType) =>
        clipboardType is not null && targetType.IsAssignableFrom(clipboardType);

    // Applies the clipboard's member values onto a compatible component (matched by member name).
    void PasteComponent(Behaviour behaviour) {
        if (!CanPasteInto(behaviour.GetType()))
            return;
        // Paste writes just this one component's members on its own entity -> scoped EditEntity.
        EditorCommands.EditEntity(behaviour.Entity, $"Paste {Prettify(behaviour.GetType().Name)}", () => {
            foreach (MemberInfo member in ComponentReflection.InspectorMembers(behaviour.GetType())) {
                if (clipboardMembers.TryGetValue(member.Name, out object value)) {
                    try { ComponentReflection.SetValue(member, behaviour, value); }
                    catch { /* incompatible member -- skip */ }
                }
            }
            state.MarkViewportDirty();
        });
    }

    // RW1.3: DrawVolumeProfileSection body (+ the volumeUndoBefore/volumeUndoLastClean statics and the
    // CreateProfileAsset helper) MOVED into VolumePreview (Inspector/Preview/ComponentPreviews.cs).

    // RW1.3: DrawTerrainBrushSection body MOVED into TerrainPreview
    // (Inspector/Preview/ComponentPreviews.cs).

    // AudioSource/clip preview voice + scrub state. The component preview (AudioSourcePreview, RW1.3) AND
    // the .wav asset-clip preview (AudioClipAssetInspector, RW1.4) share these, so they stay on InspectorPanel
    // as internal statics and are reached as InspectorPanel.audioPreviewVoice from both relocated bodies.
    internal static IAudioVoice audioPreviewVoice;
    internal static float audioPreviewTime;   // scrub-slider position (seconds), persists between previews

    // RW1.3: DrawAudioSourceSection body MOVED into AudioSourcePreview
    // (Inspector/Preview/ComponentPreviews.cs).

    // RW1.2: DrawAnimatorSection / DrawAnimatorControllerSection / DrawLightAnimatorSection /
    // DrawSpawnerSection bodies MOVED into Animator/AnimatorController/LightAnimator/Spawner Preview
    // (Inspector/Preview/ComponentPreviews.cs).

    // RW1.1: DrawHealthSection body MOVED into HealthPreview (Inspector/Preview/ComponentPreviews.cs).

    // RW1.3: DrawUIDocumentSection body (+ the DrawPathDropField helper) MOVED into UIDocumentPreview
    // (Inspector/Preview/ComponentPreviews.cs); the shared AcceptGuidDrop stays here (internal static).

    // RW1.2: DrawParticleSystemSection body MOVED into ParticleSystemPreview
    // (Inspector/Preview/ComponentPreviews.cs).

    // RW1.1: DrawTrailRendererSection body MOVED into TrailRendererPreview
    // (Inspector/Preview/ComponentPreviews.cs).

    // Collapsible component header: framed bar with a category-tinted stripe + type icon, a bold
    // label, an enable checkbox after the arrow, and a "..." menu on the right edge. Right-click
    // opens the same "##componentctx" popup the caller declares. Returns the open state;
    // `enabled` is edited in place and `menuRequested` fires when "..." is clicked.
    static unsafe bool ComponentHeader(string label, Type type, ref bool enabled, out bool menuRequested) {
        menuRequested = false;
        (string icon, SysVec4 tint) = EditorIcons.ForComponentType(type);

        gui.Spacing();
        gui.PushFramePadding(new SysVec2(10, 7));
        float arrowW = gui.TreeNodeToLabelSpacing;
        bool open = gui.CollapsingHeaderFramedOverlay($"###cmphdr_{label}");
        gui.PopStyleVar();
        gui.OpenPopupOnItemClick("##componentctx");

        SysVec2 min = gui.ItemRectMin;
        SysVec2 max = gui.ItemRectMax;
        float headerH = max.Y - min.Y;
        var draw = gui.WindowDrawList;

        // Category stripe down the left edge. RW4: drawn via the shared EditorDecoration primitive (same
        // 3px geometry as before) so the component-header stripe and any card-with-stripe use ONE source.
        EditorDecoration.DrawAccentStripe(min, headerH, tint);

        SysVec2 cursor = gui.CursorScreenPos;

        // Enable checkbox right after the disclosure arrow. The global FramePadding is roomy for inputs,
        // which makes a checkbox (sized FontSize + FramePadding*2) read as oversized in a header — shrink
        // the padding just for the box so the tick stays proportionate to the title text.
        gui.PushFramePadding(new SysVec2(2, 2) * EditorTheme.UiScale);
        float frameH = gui.FrameHeight;
        float chkX = min.X + arrowW;
        gui.SetCursorScreenPos(new SysVec2(chkX, min.Y + (headerH - frameH) * 0.5f));
        gui.Checkbox($"##en_{label}", ref enabled);
        gui.PopStyleVar();

        // Type icon + label after the checkbox. EF5e: the component title uses the larger semantic HEADER
        // font (not body-size Bold) so a component header reads as a real header — the type-scale hierarchy
        // that breaks the "flat wall of same-size text" feel. (Header has icon glyphs merged in, so the icon
        // renders from the same font at the same size and stays baseline-aligned with the label.)
        float headerFontSize = gui.FontSizeOf(EditorFont.Header);
        float textY = min.Y + (headerH - headerFontSize) * 0.5f;
        float iconX = chkX + frameH + 6;
        var dimmed = enabled ? 1f : 0.45f;
        draw.AddText(EditorFont.Header, headerFontSize, new SysVec2(iconX, textY),
            gui.ColorU32(new SysVec4(tint.X, tint.Y, tint.Z, dimmed)), icon);
        draw.AddText(EditorFont.Header, headerFontSize,
            new SysVec2(iconX + headerFontSize + 8, textY),
            gui.ColorU32(gui.StyleColor(enabled ? EditorStyleColor.Text : EditorStyleColor.TextDisabled)), label);

        // "..." menu pinned to the right edge.
        float moreW = EditorIcons.SmallButtonWidth(EditorIcons.More);
        gui.SetCursorScreenPos(new SysVec2(max.X - moreW - 6,
            min.Y + (headerH - gui.TextLineHeight) * 0.5f - gui.FramePadding.Y * 0.5f + 2));
        gui.PushColor(EditorStyleColor.Text, gui.StyleColor(EditorStyleColor.TextDisabled));
        if (EditorIcons.GhostButtonSmall($"more_{label}", EditorIcons.More, "Component menu"))
            menuRequested = true;
        gui.PopColor();

        gui.SetCursorScreenPos(cursor);
        return open;
    }

    // Draws all inspector members of a component, honouring [Header]/[Space] (which break the
    // two-column grid into stacked sub-tables) and the per-member attributes. The caller has
    // already drawn the Enabled row in its own grid and closed it.
    // internal (RW1.4): the relocated DataAssetInspector body (Inspector/AssetInspectors/) reflects a
    // DataAsset through this same member list via ctx.Panel.DrawMemberList — byte-identical to the inline call.
    internal void DrawMemberList(Type type, object target) {
        // EF16: defensively re-seat the nesting depth to 0 at the top of each component's member list, in case
        // a prior component's recursion was unwound by a caught draw exception (DrawNestedBody's finally
        // restores it on the normal path; this is belt-and-braces against a throw escaping that).
        nestDepth = 0;

        // EF10a — the per-component member SET that survives [ShowIf]/[HideIf] (what the user actually sees).
        // Computed ONCE here so it drives BOTH the search-box visibility threshold AND the filter, and so the
        // forward-scan that hides empty [Header]/[FoldoutGroup] sections under a query has a concrete list to
        // walk. This is the only added per-frame allocation and only over the (already small) member list.
        var visibleMembers = new List<(MemberInfo Info, MemberAttributes Attrs)>();
        foreach (TypePlan.Member planned in TypePlan.For(type).Members) {
            MemberAttributes a = MemberAttributes.For(planned.Info);
            if (Conditions.Visible(a.Conditionals, target))
                visibleMembers.Add((planned.Info, a));
        }

        // EF10a — conditional per-component member search. Shown only when the component has enough members
        // to be worth filtering (don't clutter a 3-field component — InspectorLayout.MemberSearchThreshold).
        // The box sits ABOVE the grid and only decides ROW VISIBILITY; it is NOT part of the column model
        // (EF11/EF16 own that), so it can't collide with the label/value layout.
        string query = "";
        if (visibleMembers.Count > Inspector.InspectorLayout.MemberSearchThreshold) {
            StrBox box = memberSearch.GetValue(target, static _ => new StrBox());
            if (EditorWidgets.SearchField($"##membersearch_{type.Name}", "Search properties...", ref box.Value))
                state.MarkViewportDirty();
            query = box.Value;
            gui.Spacing();
        }

        // EF10a — when a query is active, `matches` is the set of members whose DISPLAYED label (the same
        // [LabelText] ?? Prettify(Name) the row shows) contains the query; a [Header] section or [FoldoutGroup]
        // with no matching member is hidden so only the relevant fields + their group remain. With no query
        // `matches` is null → the predicates below pass everyone → the draw is byte-identical to pre-EF10a.
        HashSet<MemberInfo> matches = null;
        if (query.Length > 0) {
            matches = new HashSet<MemberInfo>();
            foreach ((MemberInfo info, MemberAttributes a) in visibleMembers)
                if (MemberLabel(info, a).Contains(query, StringComparison.OrdinalIgnoreCase))
                    matches.Add(info);
        }

        bool MemberVisible(MemberInfo m) => matches is null || matches.Contains(m);

        // A [FoldoutGroup] is drawn only if it holds a matching member (else the whole collapsible group is
        // hidden under the filter). Precomputed so the draw loop's group-header decision is O(1).
        HashSet<string> groupsWithMatch = null;
        if (matches is not null) {
            groupsWithMatch = new HashSet<string>();
            foreach ((MemberInfo info, MemberAttributes a) in visibleMembers)
                if (a.Foldout?.Name is { } g && matches.Contains(info))
                    groupsWithMatch.Add(g);
        }

        bool GroupVisible(string g) => g is null || groupsWithMatch is null || groupsWithMatch.Contains(g);

        // A [Header] divider is drawn only if at least one member in ITS section (from the header up to the
        // next header) survives the filter — otherwise the divider would orphan above hidden fields. Maps the
        // header-bearing MemberInfo → whether its section has a match.
        HashSet<MemberInfo> headersWithMatch = null;
        if (matches is not null) {
            headersWithMatch = new HashSet<MemberInfo>();
            MemberInfo currentHeader = null;
            bool sectionHasMatch = false;
            foreach ((MemberInfo info, MemberAttributes a) in visibleMembers) {
                if (a.Header is not null) {
                    if (currentHeader is not null && sectionHasMatch) headersWithMatch.Add(currentHeader);
                    currentHeader = info;
                    sectionHasMatch = false;
                }
                if (currentHeader is not null && matches.Contains(info) && GroupVisible(a.Foldout?.Name))
                    sectionHasMatch = true;
            }
            if (currentHeader is not null && sectionHasMatch) headersWithMatch.Add(currentHeader);
        }

        bool HeaderVisible(MemberInfo m) => headersWithMatch is null || headersWithMatch.Contains(m);

        var gridOpen = false;
        var gridIndex = 0;       // each sub-table (split by Header/Space/foldout) needs a unique id
        string currentGroup = null;  // active [FoldoutGroup] name
        var groupOpen = true;        // is the active foldout expanded (members drawn)?

        void EnsureGrid() {
            if (!gridOpen)
                gridOpen = BeginGrid($"##members{type.Name}{gridIndex++}");
        }

        void CloseGrid() {
            if (gridOpen) { gui.EndTable(); gridOpen = false; }
        }

        void EndGroup() {
            if (currentGroup is null)
                return;
            CloseGrid();
            if (groupOpen) gui.TreePop();   // balance the open TreeNodeEx
            currentGroup = null;
            groupOpen = true;
        }

        // Member order is single-sourced engine-side: TypePlan.For(type).Members is already ordered by
        // [PropertyOrder] then declaration order (the same rule this site used to compute inline), so the
        // inspector consumes ONE ordered member list instead of re-sorting -- byte-identical, no drift.
        foreach ((MemberInfo member, MemberAttributes attrs) in visibleMembers) {
            string group = attrs.Foldout?.Name;

            // EF10a: under a filter, a whole [FoldoutGroup] with no matching member is hidden — skip every
            // member in it WITHOUT touching the chrome state (no EndGroup for a group that was never opened).
            // No query → GroupVisible always true → byte-identical to pre-EF10a.
            if (!GroupVisible(group))
                continue;

            // Leaving the current foldout group (different/no group, or a new header) closes it.
            if (group != currentGroup || attrs.Header is not null)
                EndGroup();

            // EF10a: [Header]/[Space] chrome is decoupled from the member's own filter match — the header
            // divider draws when ITS SECTION has a match (HeaderVisible), even if the header-bearing member's
            // label itself doesn't match, so a matched field lower in the section keeps its section title.
            // No query → HeaderVisible always true → byte-identical.
            // A [Space] shows when the member it decorates (or that member's section header) is going to draw,
            // so the gap never orphans above a filtered-out field.
            if (attrs.Space is not null && (MemberVisible(member) || HeaderVisible(member))) { CloseGrid(); gui.Dummy(new SysVec2(0, attrs.Space.Height)); }
            if (attrs.Header is not null && HeaderVisible(member)) { CloseGrid(); EditorDecoration.DrawSectionHeader(attrs.Header.Text); }

            // Entering a new foldout group: draw its collapsible header once. When open, the matching
            // TreePop happens in EndGroup; when collapsed, TreeNodeEx requires no TreePop. (Only reached when
            // the group is visible under the filter — GroupVisible gated above.)
            if (group is not null && group != currentGroup) {
                CloseGrid();
                var flags = EditorTreeFlags.Framed | EditorTreeFlags.SpanAvailWidth |
                    (attrs.Foldout.DefaultOpen ? EditorTreeFlags.DefaultOpen : EditorTreeFlags.None);
                groupOpen = gui.TreeNodeEx($"{group}###fold_{type.Name}_{group}", flags);
                currentGroup = group;
            }

            if (currentGroup is not null && !groupOpen)
                continue;                       // member hidden inside a collapsed foldout

            // EF10a: the member's OWN label-match gate is the last step — its header/group chrome has already
            // been emitted above, so a non-matching member is dropped without orphaning its section title.
            if (!MemberVisible(member))
                continue;

            EnsureGrid();
            DrawMember(member, target, attrs);
        }

        EndGroup();
        CloseGrid();

        // [Button] methods render as full-width action buttons below the fields (clearer than
        // self-resetting bool checkboxes for one-shot operations like a probe bake).
        foreach (MethodInfo method in ComponentReflection.InspectorButtons(type)) {
            var label = method.GetCustomAttribute<ButtonAttribute>()?.Label ?? method.Name;
            if (gui.Button($"{label}###btn_{type.Name}_{method.Name}", new SysVec2(-1, 0)))
                method.Invoke(target, null);
        }

        // [EditorWindowExecutionPoint] methods: a window-open button that runs the method (state setup)
        // then opens a dedicated big window for this component.
        foreach (MethodInfo method in ComponentReflection.InspectorWindowPoints(type)) {
            var attr = method.GetCustomAttribute<EditorWindowExecutionPointAttribute>();
            string label = attr?.Title ?? $"Open {Prettify(type.Name)} Window";
            if (gui.Button($"{EditorIcons.Maximize}  {label}###win_{type.Name}_{method.Name}", new SysVec2(-1, 0))) {
                try { method.Invoke(target, null); }
                catch (Exception e) { Debugging.LogError($"Editor window method threw: {e.Message}"); }
                ComponentEditorWindow.Show(target, attr?.Title ?? Prettify(type.Name));
            }
        }
    }

    // EF10a — the label the search filter matches on: the SAME text the row shows ([LabelText] override,
    // else the prettified member name), so typing "steer" finds "High Speed Steer Scale" exactly as the user
    // reads it. Mirrors MemberProperty.Label / VolumeParamProperty.Label (single source of the display label).
    static string MemberLabel(MemberInfo member, MemberAttributes attrs) =>
        attrs.LabelText?.Text ?? Inspector.InspectorReflection.Prettify(member.Name);

    // Sets a member on the active component AND, in a multi-selection, on the same-named member of the
    // matching component (same type) of every OTHER selected entity — Unity's per-component multi-edit.
    // Only broadcasts when `target` is a Behaviour on the active entity and >1 entity is selected; for
    // anything else (assets, scene behaviours) it just sets the one target.
    void ApplyMember(MemberInfo member, object target, object value) {
        ComponentReflection.SetValue(member, target, value);

        if (state.SelectedEntities.Count <= 1 || target is not Behaviour activeBehaviour)
            return;
        Entity activeEntity = activeBehaviour.Entity;
        Type compType = activeBehaviour.GetType();
        foreach (Entity e in state.SelectedEntities) {
            if (e is null || e.IsDestroyed || ReferenceEquals(e, activeEntity))
                continue;
            foreach (Behaviour b in e.Behaviours) {
                if (b.GetType() == compType) {
                    try { ComponentReflection.SetValue(member, b, value); }
                    catch { /* mismatched/read-only on a sibling — skip, don't break the edit */ }
                    break; // first matching component only
                }
            }
        }
    }

    // Draws a small amber "—" after the field label when the selected entities DISAGREE on this
    // member's value (Unity's mixed-value dash). No-op for single selection or when all agree.
    void DrawMixedMarker(MemberInfo member, object target, object activeValue) {
        if (state.SelectedEntities.Count <= 1 || target is not Behaviour activeBehaviour)
            return;
        Type compType = activeBehaviour.GetType();
        Entity activeEntity = activeBehaviour.Entity;
        bool differs = false;
        foreach (Entity e in state.SelectedEntities) {
            if (e is null || e.IsDestroyed || ReferenceEquals(e, activeEntity))
                continue;
            foreach (Behaviour b in e.Behaviours) {
                if (b.GetType() == compType) {
                    object v = ComponentReflection.GetValue(member, b);
                    if (!Equals(v, activeValue)) differs = true;
                    break;
                }
            }
            if (differs) break;
        }
        if (differs) {
            gui.SameLine(0, 4);
            gui.TextColored(EditorTheme.Warning, "—");
            if (gui.IsItemHovered())
                gui.Tooltip("Values differ across the selection. Editing sets them all to this value.");
        }
    }

    void DrawMember(MemberInfo member, object target, MemberAttributes attrs) {
        // B4 (Rule 2 -- "serialize-a-value == draw-a-value, ONE recursion"): EVERY drawable member now
        // flows through the SAME composable drawer STACK as the volume profile editor (B0). Since chunk15
        // the four ex-special widget types (BEvent / BObject asset-slot / AnimationCurve / ColorGradient)
        // are TERMINAL drawers registered on memberRegistry, so the IsSpecialWidgetType bypass + its
        // if/else chain are GONE -- the stack's TypeDrawerTerminalStep resolves them (and the primitives)
        // uniformly, and an unresolved type falls to gui.Unsupported() == the old ({Type}) TextDisabled
        // row. The component stack owns its own row (PushId/BeginRow = label + mixed-marker + width) and
        // the [ReadOnly]/[EnableIf] disable wrap (EnableStep) -- byte-identical to the old inline scaffold.
        // The [ShowIf]/[HideIf] skip + the out-of-grid [Header]/[Space] separators stay in DrawMemberList
        // (the layout driver), so the component stack registers only Enable+terminal -- see
        // DrawerStack.CreateComponent.
        //
        // Per-edit undo + dirty are unchanged: primitive edits auto-register one InspectorUndo.Track step
        // and mark the viewport dirty via the apply delegate below; the curve/gradient/asset terminal
        // drawers mark dirty through the host exactly as their old arms did (BEvent ignored its result, so
        // it still marks nothing).
        componentStack.Draw(new MemberProperty(member, target,
            v => { ApplyMember(member, target, v); state.MarkViewportDirty(); }), componentGui);
    }

    // ---- Vector widgets ---------------------------------------------------------

    // Unity-style vector editor: three drag fields, each with a colored X/Y/Z chip fused to its
    // left edge. `label` is the undo-step name; each axis auto-registers one undo entry per drag via
    // InspectorUndo.Track (the single static pending slot is safe — only one axis edits at a time).
    static bool AxisVec3(string id, string label, ref SysVec3 v, float speed) {
        float gap = 4;
        float chipW = MathF.Round(gui.FontSize * 0.92f);
        float cellW = (gui.ContentRegionAvail.X - gap * 2) / 3f;
        float fieldW = Math.Max(26f, cellW - chipW);

        var changed = AxisDrag($"##{id}x", "X", label, EditorIcons.AxisX, ref v.X, speed, chipW, fieldW);
        gui.SameLine(0, gap);
        changed |= AxisDrag($"##{id}y", "Y", label, EditorIcons.AxisY, ref v.Y, speed, chipW, fieldW);
        gui.SameLine(0, gap);
        changed |= AxisDrag($"##{id}z", "Z", label, EditorIcons.AxisZ, ref v.Z, speed, chipW, fieldW);
        return changed;
    }

    static bool AxisDrag(string id, string letter, string label, SysVec4 color, ref float value,
        float speed, float chipW, float fieldW) {
        var draw = gui.WindowDrawList;
        SysVec2 pos = gui.CursorScreenPos;
        float h = gui.FrameHeight;
        float rounding = gui.FrameRounding;

        // Calmer than a saturated filled chip (which made vector rows read as noisy): a NEUTRAL chip
        // background (same family as the field) with the axis color carried only by the LETTER. The axis
        // is still instantly readable, but three bright blocks per row no longer fight the rest of the UI.
        SysVec4 chipBg = gui.StyleColor(EditorStyleColor.FrameBg);
        draw.AddRectFilled(pos, pos + new SysVec2(chipW, h),
            gui.ColorU32(new SysVec4(chipBg.X, chipBg.Y, chipBg.Z, 1f)),
            rounding, EditorCorner.Left);
        SysVec2 ts = gui.CalcTextSize(letter);
        draw.AddText(pos + new SysVec2((chipW - ts.X) * 0.5f, (h - ts.Y) * 0.5f),
            gui.ColorU32(color), letter);

        gui.Dummy(new SysVec2(chipW, h));
        gui.SameLine(0, 0);
        gui.SetNextItemWidth(fieldW);
        return InspectorUndo.Track(label, gui.DragFloat(id, ref value, speed, 0, 0, "%.2f"));
    }

    // RW1.1: DrawSubMeshMaterials / DrawSubMeshMaterialRow were MOVED into RendererPreview
    // (Inspector/Preview/ComponentPreviews.cs) so the per-submesh material body lives with the preview
    // that owns it. Behaviour byte-identical; only the row helper (Row) + grid (BeginGrid) stay here,
    // widened to internal static so the relocated body can still call them.

    // Asset slot. Assigned: clicking the name PINS the asset in the Inspector (shows its
    // asset view), the chevron button opens the picker. Unassigned: click opens the picker.
    // Either way the slot is a drag-drop target for browser tiles.
    void DrawAssetSlot(MemberInfo member, object target, BObject asset, Type assetType) {
        Guid guid = default;
        var hasGuid = asset is not null && AssetDatabase.TryGetAssetGuid(asset, out guid);

        if (asset is null) {
            gui.PushColor(EditorStyleColor.Text, gui.StyleColor(EditorStyleColor.TextDisabled));
            if (gui.Button($"None  {EditorIcons.ChevronDown}", new SysVec2(-1, 0)))
                OpenPickerFor(member, target, assetType);
            gui.PopColor();
            if (AcceptGuidDrop(out Guid d0))
                AssignAsset(member, target, assetType, d0);
            return;
        }

        var path = hasGuid ? AssetDatabase.GuidToAssetPath(guid) : null;
        var display = path is not null ? Path.GetFileName(path) : asset.GetType().Name;
        (string icon, _) = EditorIcons.ForAssetExtension(
            path is not null ? Path.GetExtension(path).ToLowerInvariant() : "");

        float pickerW = gui.FrameHeight + 6;
        if (gui.Button($"{icon}  {display}", new SysVec2(-pickerW - 4, 0)) && path is not null)
            state.RequestRevealAsset(path); // jump to it in the asset browser, don't swap the inspector
        if (AcceptGuidDrop(out Guid d1))
            AssignAsset(member, target, assetType, d1);
        if (gui.IsItemHovered() && path is not null)
            gui.Tooltip($"{path}\nClick to reveal in the asset browser.");

        gui.SameLine();
        if (gui.Button(EditorIcons.ChevronDown, new SysVec2(pickerW, 0)))
            OpenPickerFor(member, target, assetType);
        if (AcceptGuidDrop(out Guid d2))
            AssignAsset(member, target, assetType, d2);
    }

    void OpenPickerFor(MemberInfo member, object target, Type assetType) {
        pickerMember = member;
        pickerTarget = target;
        pickerType = assetType;
        pickerProperty = null;   // G2-editor: member-backed slot -> clear any prior property-backed request
        openPicker = true;
    }

    void AssignAsset(MemberInfo member, object target, Type assetType, Guid guid) {
        // ApplyMember broadcasts to the whole multi-selection, so this stays a whole-scene structural
        // snapshot (not a single-entity EditEntity). The asset load is a pure read -- keep it inside the
        // command so the snapshot still fires exactly before the first write.
        EditorCommands.Structural($"Assign {Prettify(member.Name)}", () => {
            MethodInfo load = typeof(AssetDatabase).GetMethod(nameof(AssetDatabase.Load), [typeof(Guid)])!
                .MakeGenericMethod(assetType);
            object loaded = load.Invoke(null, [guid]);
            if (loaded is not null)
                ApplyMember(member, target, loaded); // broadcasts to the multi-selection like value edits
            state.MarkViewportDirty();
        });
    }

    // G2-editor: the IProperty-keyed asset slot (a collection element whose type is a BObject asset, e.g. a
    // List<Material> element). The parallel of DrawAssetSlot(member,...) but every write routes through
    // IProperty.Set (-> the collection write-back) instead of a MemberInfo, and the picker remembers the
    // property (pickerProperty) so its click-to-assign / (None) write through the same path. Mirrors
    // DrawSceneObjectSlot's IProperty approach.
    void DrawAssetSlotForProperty(Inspector.IProperty p) {
        Type assetType = p.ValueType;
        var asset = p.Get() as BObject;
        Guid guid = default;
        bool hasGuid = asset is not null && AssetDatabase.TryGetAssetGuid(asset, out guid);

        if (asset is null) {
            gui.PushColor(EditorStyleColor.Text, gui.StyleColor(EditorStyleColor.TextDisabled));
            if (gui.Button($"None  {EditorIcons.ChevronDown}", new SysVec2(-1, 0)))
                OpenPickerForProperty(p);
            gui.PopColor();
            if (AcceptGuidDrop(out Guid d0))
                AssignAssetToProperty(p, assetType, d0);
            return;
        }

        var path = hasGuid ? AssetDatabase.GuidToAssetPath(guid) : null;
        var display = path is not null ? Path.GetFileName(path) : asset.GetType().Name;
        (string icon, _) = EditorIcons.ForAssetExtension(
            path is not null ? Path.GetExtension(path).ToLowerInvariant() : "");

        float pickerW = gui.FrameHeight + 6;
        if (gui.Button($"{icon}  {display}", new SysVec2(-pickerW - 4, 0)) && path is not null)
            state.RequestRevealAsset(path);
        if (AcceptGuidDrop(out Guid d1))
            AssignAssetToProperty(p, assetType, d1);
        if (gui.IsItemHovered() && path is not null)
            gui.Tooltip($"{path}\nClick to reveal in the asset browser.");

        gui.SameLine();
        if (gui.Button(EditorIcons.ChevronDown, new SysVec2(pickerW, 0)))
            OpenPickerForProperty(p);
        if (AcceptGuidDrop(out Guid d2))
            AssignAssetToProperty(p, assetType, d2);
    }

    // Per-submesh MATERIAL OVERRIDE slot for the RendererPreview's material list (the editable replacement
    // for the old read-only label list). Draws a real asset slot (drag-drop + picker + reveal) bound to the
    // renderer's per-submesh override (Renderer.Get/SetMaterialOverride = Unity's sharedMaterials[i]); a null
    // override shows the BAKED material as a dimmed placeholder so the slot reads "inheriting from the mesh"
    // rather than empty, and assigning one overrides just that submesh. Each write is one undo step
    // (EditorCommands.Structural -> the override array is a serialized member). Reuses the IProperty asset
    // slot so the slot behaves byte-identically to every other Material slot. Internal: the RendererPreview
    // (Inspector.Preview) calls it, the rest of the asset-slot machinery stays private.
    internal void DrawSubMeshMaterialSlot(Renderer renderer, int submeshIndex, Material baked) {
        var slot = new Inspector.CollectionElementProperty(
            $"Submesh {submeshIndex} Material", typeof(Material),
            () => renderer.GetMaterialOverride(submeshIndex),
            v => EditorCommands.Structural($"Assign Submesh {submeshIndex} Material", () => {
                renderer.SetMaterialOverride(submeshIndex, v as Material);
                state.MarkViewportDirty();
            }));

        // When there is no override, hint the inherited (baked) material behind the slot — the asset slot
        // shows "None" for a null value, so a dimmed "(from mesh: X)" caption keeps the slot informative.
        DrawAssetSlotForProperty(slot);
        if (renderer.GetMaterialOverride(submeshIndex) is null && baked is not null) {
            string bakedName = AssetDatabase.TryGetAssetGuid(baked, out Guid g)
                ? Path.GetFileNameWithoutExtension(AssetDatabase.GuidToAssetPath(g))
                : baked.GetType().Name;
            gui.PushColor(EditorStyleColor.Text, gui.StyleColor(EditorStyleColor.TextDisabled));
            gui.TextUnformatted($"{EditorIcons.Color} from mesh: {bakedName}");
            gui.PopColor();
            if (gui.IsItemHovered())
                gui.Tooltip("Inheriting the material baked into the mesh for this submesh.\nAssign one above to override just this slot.");
        }
    }

    void OpenPickerForProperty(Inspector.IProperty p) {
        pickerProperty = p;
        pickerMember = null;
        pickerTarget = null;
        pickerType = p.ValueType;
        openPicker = true;
    }

    void AssignAssetToProperty(Inspector.IProperty p, Type assetType, Guid guid) {
        // IProperty.Set routes through ApplyMember (multi-select broadcast), so whole-scene Structural.
        EditorCommands.Structural($"Assign {p.Label}", () => {
            MethodInfo load = typeof(AssetDatabase).GetMethod(nameof(AssetDatabase.Load), [typeof(Guid)])!
                .MakeGenericMethod(assetType);
            object loaded = load.Invoke(null, [guid]);
            if (loaded is not null)
                p.Set(loaded);
            state.MarkViewportDirty();
        });
    }

    // Mini asset-picker window: search + every compatible asset; click to assign.
    void DrawAssetPickerPopup() {
        float u = gui.FontSize;
        gui.SetNextWindowSizeAppearing(new SysVec2(u * 28f, u * 30f));
        if (!gui.BeginPopup("##assetpicker"))
            return;

        gui.PushItemSpacing(new SysVec2(8, 6));

        // Header: "Select <Type>" in bold + a hint of which extensions qualify.
        string typeName = pickerType is null ? "Asset" : Prettify(pickerType.Name);
        gui.PushFont(EditorFont.Bold);
        gui.TextUnformatted($"Select {typeName}");
        gui.PopFont();

        string[] extensions = CompatibleExtensions(pickerType);
        if (extensions.Length > 0) {
            gui.SameLine();
            gui.TextDisabled($"({string.Join(" ", extensions)})");
        }
        gui.Spacing();

        if (gui.IsWindowAppearing())
            gui.SetKeyboardFocusHere();
        gui.SetNextItemWidth(-1);
        gui.InputTextWithHint("##search", $"{EditorIcons.Search} Search {typeName.ToLowerInvariant()}s...",
            ref pickerSearch, 128);
        gui.Separator();

        gui.BeginChild("##list", default, border: false);

        // (None) clears the slot.
        gui.PushColor(EditorStyleColor.Text, gui.StyleColor(EditorStyleColor.TextDisabled));
        if (gui.Selectable($"  (None)", false, new SysVec2(0, gui.FrameHeight))) {
            if (pickerProperty is not null) {
                // G2-editor: property-backed slot (collection element) -> clear via Set (collection write-back,
                // ApplyMember broadcast) -> whole-scene Structural.
                EditorCommands.Structural($"Clear {pickerProperty.Label}", () => pickerProperty.Set(null));
            }
            else {
                // Direct member clear (single target, no broadcast) but the entity is not reachable from the
                // picker context, so it stays whole-scene Structural -- byte-identical to the old Push.
                EditorCommands.Structural($"Clear {Prettify(pickerMember.Name)}",
                    () => ComponentReflection.SetValue(pickerMember, pickerTarget, null));
            }
            state.MarkViewportDirty();
            gui.CloseCurrentPopup();
        }
        gui.PopColor();

        var any = false;
        foreach ((string path, Guid guid) in AssetDatabase.EnumerateAssets()
                     .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)) {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            // Type filter: only assets whose extension matches the slot's type. (Unknown type => nothing,
            // so an unrecognized slot never floods the picker with every asset in the project.)
            if (!extensions.Contains(ext))
                continue;
            if (pickerSearch.Length > 0 &&
                !Path.GetFileName(path).Contains(pickerSearch, StringComparison.OrdinalIgnoreCase))
                continue;

            any = true;
            (string icon, SysVec4 tint) = EditorIcons.ForAssetExtension(ext);
            bool clicked = gui.Selectable($"      {Path.GetFileName(path)}##{guid}", false, new SysVec2(0, gui.FrameHeight));
            SysVec2 rmin = gui.ItemRectMin;
            EditorIcons.DrawAt(new SysVec2(rmin.X + 6,
                rmin.Y + (gui.FrameHeight - gui.TextLineHeight) * 0.5f), icon, tint);
            if (gui.IsItemHovered())
                gui.Tooltip(path);
            if (clicked) {
                if (pickerProperty is not null)
                    AssignAssetToProperty(pickerProperty, pickerType, guid);  // G2-editor: collection element
                else
                    AssignAsset(pickerMember, pickerTarget, pickerType, guid);
                gui.CloseCurrentPopup();
            }
        }

        if (!any)
            gui.TextDisabled(pickerSearch.Length > 0
                ? "No matching assets."
                : $"No {typeName.ToLowerInvariant()} assets in the project.");

        gui.EndChild();
        gui.PopStyleVar();
        gui.EndPopup();
    }

    // G1-editor: the interactive scene-object SLOT for an EntityRef / ComponentRef member (the parallel of
    // DrawAssetSlot for BObject asset members). Reads the current ref's InstanceId off the boxed value, shows
    // the live target's name (or "None" / a missing-reference marker), accepts a Hierarchy entity-drag, and
    // opens a searchable picker on click. All writes go through the IProperty (Set -> ApplyMember + dirty +
    // multi-select broadcast) so the ref behaves exactly like a primitive edit; each write pushes one undo.
    void DrawSceneObjectSlot(Inspector.IProperty p) {
        bool isComponentRef = p.ValueType == typeof(ComponentRef);
        object boxed = p.Get();
        Guid instanceId = boxed switch {
            EntityRef e => e.InstanceId,
            ComponentRef c => c.InstanceId,
            _ => Guid.Empty,
        };

        // Resolve the live target for display (lazy, like EntityRef.Value): a set-but-deleted ref shows the
        // Unity "Missing" marker, an unset ref shows "None".
        BObject resolved = instanceId == Guid.Empty ? null : SceneManager.FindByInstanceId(instanceId);
        string label;
        SysVec4 textCol;
        string icon;
        SysVec4 iconTint = EditorIcons.TintGeneric;
        if (instanceId == Guid.Empty) {
            label = "None";
            textCol = gui.StyleColor(EditorStyleColor.TextDisabled);
            icon = isComponentRef ? EditorIcons.Wrench : EditorIcons.Package;
        }
        else if (resolved is null) {
            label = $"Missing ({(isComponentRef ? "Component" : "Entity")})";
            textCol = EditorTheme.Error; // amber-red, like a missing reference
            icon = EditorIcons.Warning;
        }
        else if (resolved is Behaviour b) {
            (icon, iconTint) = EditorIcons.ForComponentType(b.GetType());
            label = $"{b.Entity?.Name} ({Prettify(b.GetType().Name)})";
            textCol = gui.StyleColor(EditorStyleColor.Text);
        }
        else {
            (icon, iconTint) = resolved is Entity ent ? EditorIcons.ForEntity(ent) : (EditorIcons.Package, EditorIcons.TintGeneric);
            label = resolved.Name;
            textCol = gui.StyleColor(EditorStyleColor.Text);
        }

        float pickerW = gui.FrameHeight + 6;
        gui.PushColor(EditorStyleColor.Text, textCol);
        bool clicked = gui.Button($"{icon}  {label}", new SysVec2(-pickerW - 4, 0));
        gui.PopColor();
        // The main button opens the picker too (no "reveal" action for scene objects -- selecting one would
        // swap the inspector away from the edited entity, which is surprising; click = pick, like None).
        if (clicked)
            OpenSceneRefPickerFor(p);
        if (AcceptEntityDrop(out Entity dropped) && !isComponentRef)
            AssignSceneRef(p, new EntityRef(dropped));
        else if (resolved is not null && gui.IsItemHovered())
            gui.Tooltip(isComponentRef ? "Click to pick a component." : "Click to pick an entity, or drag a Hierarchy row here.");

        gui.SameLine();
        if (gui.Button(EditorIcons.ChevronDown, new SysVec2(pickerW, 0)))
            OpenSceneRefPickerFor(p);
        if (AcceptEntityDrop(out Entity dropped2) && !isComponentRef)
            AssignSceneRef(p, new EntityRef(dropped2));
    }

    void OpenSceneRefPickerFor(Inspector.IProperty p) {
        sceneRefPickerProperty = p;
        openSceneRefPicker = true;
    }

    // Writes a new EntityRef/ComponentRef (boxed) onto the slot's property. One undo per assignment; the
    // IProperty.Set routes through ApplyMember (multi-select broadcast) + MarkViewportDirty.
    void AssignSceneRef(Inspector.IProperty p, object refValue) {
        // IProperty.Set routes through ApplyMember (multi-select broadcast) -> whole-scene Structural.
        EditorCommands.Structural($"Assign {p.Label}", () => {
            p.Set(refValue);
            state.MarkViewportDirty();
        });
    }

    // G2-editor (Rule 2): the interactive collection editor for a List<T> / T[] member (the parallel of
    // DrawAssetSlot / DrawSceneObjectSlot for the collection category). Renders, inside the value column the
    // BeginRow opened: a "(N items)" header + an "Add" button, then one row per element drawn RECURSIVELY by
    // its own terminal drawer (a List<Vector3> draws Vector3 widgets, a List<Material> asset slots, a
    // List<EntityRef> scene-object slots), each with a Remove (X) button. Every structural change (Add /
    // Remove) copies-mutates-writes the WHOLE collection back through the property (-> ApplyMember broadcast +
    // dirty) under one EditorCommands.Structural; element edits push undo via the element's own terminal drawer
    // (primitives auto-Track; asset/scene-ref slots push their own). The element type's drawer must exist (a
    // struct element with no registered drawer shows Unsupported per-element, like a struct member -- the
    // ch20 scope: List/array of primitives / enums / math-structs / asset refs / scene refs / curves /
    // gradients; Dictionary is ch21, deep nested-struct element write-back is G4).
    void DrawCollectionSlot(Inspector.IProperty p) {
        Type collType = p.ValueType;
        bool isArray = collType.IsArray;
        Type elemType = isArray ? collType.GetElementType() : collType.GetGenericArguments()[0];
        object boxed = p.Get();
        System.Collections.IList list = boxed as System.Collections.IList; // List<T> and T[] both implement IList

        int count = list?.Count ?? 0;

        // Header row inside the value column: item count + a Clear button + an Add button (full row width).
        // Add is right-aligned; Clear sits just left of it (only when the list is non-empty, so an empty list
        // never shows a no-op Clear). Both are structural single-undo edits like Add.
        gui.AlignTextToFramePadding();
        gui.TextDisabled($"{count} item{(count == 1 ? "" : "s")}");
        float addW = gui.FrameHeight + 24;
        float clearW = count > 0 ? gui.FrameHeight + 32 : 0f;
        float gap = count > 0 ? 6f : 0f;
        gui.SameLine();
        gui.CursorPosX = (gui.CursorPosX + Math.Max(0, gui.ContentRegionAvail.X - addW - clearW - gap));
        bool clearClicked = false;
        if (count > 0) {
            if (gui.Button($"{EditorIcons.Delete} Clear##clearcol_{p.Name}", new SysVec2(clearW, 0)))
                clearClicked = true;
            gui.SameLine(0, gap);
        }
        if (gui.Button($"{EditorIcons.Add} Add##addcol_{p.Name}", new SysVec2(addW, 0))) {
            CollectionAdd(p, collType, elemType, list, isArray);
            return; // structural change: redraw next frame against the new collection (avoids a stale row)
        }
        if (clearClicked) {
            CollectionClear(p, collType, elemType, isArray);
            return; // structural change: redraw next frame against the now-empty collection
        }

        if (count == 0)
            return;

        // Per-element rows: each element drawn by its own terminal drawer through a CollectionElementProperty,
        // followed by reorder (up/down), insert, and remove buttons. PushId per index so duplicate element
        // values keep distinct ImGui ids. Structural edits (move/insert/remove) are DEFERRED past the loop so
        // the collection is not mutated mid-iteration (a stale row + redraw next frame, like Add).
        int removeIndex = -1, insertIndex = -1, moveFrom = -1, moveTo = -1;
        float btnW = gui.FrameHeight;
        // 4 trailing buttons (up, down, insert, remove) each btnW wide, with 4px gaps.
        float controlsW = btnW * 4 + 4 * 3 + 6;
        for (int i = 0; i < count; i++) {
            gui.PushId(i);
            int captured = i;
            float elemW = Math.Max(40f, gui.ContentRegionAvail.X - controlsW);

            var elemProp = new Inspector.CollectionElementProperty(
                $"Element {i}", elemType,
                () => captured < (list?.Count ?? 0) ? list[captured] : null,
                v => CollectionSetElement(p, list, captured, v));

            ITypeDrawer drawer = memberRegistry.Resolve(elemType);
            gui.SetNextItemWidth(elemW);
            if (drawer is not null) {
                componentGui.SetUndoLabel($"Edit {p.Label} [{i}]");
                drawer.Draw(elemProp, componentGui);
            }
            else {
                gui.TextDisabled($"({elemType.Name})"); // no drawer for this element type (e.g. nested struct)
            }

            // Move up (disabled on the first row): swap with the previous element.
            gui.SameLine(0, 6);
            gui.BeginDisabled(captured == 0);
            if (gui.Button($"{EditorIcons.ChevronUp}##up", new SysVec2(btnW, 0))) { moveFrom = captured; moveTo = captured - 1; }
            gui.EndDisabled();
            if (gui.IsItemHovered() && captured > 0) gui.Tooltip("Move up");

            // Move down (disabled on the last row): swap with the next element.
            gui.SameLine(0, 4);
            gui.BeginDisabled(captured == count - 1);
            if (gui.Button($"{EditorIcons.ChevronDown}##down", new SysVec2(btnW, 0))) { moveFrom = captured; moveTo = captured + 1; }
            gui.EndDisabled();
            if (gui.IsItemHovered() && captured < count - 1) gui.Tooltip("Move down");

            // Insert a default element BEFORE this row.
            gui.SameLine(0, 4);
            if (gui.Button($"{EditorIcons.Add}##ins", new SysVec2(btnW, 0))) insertIndex = captured;
            if (gui.IsItemHovered()) gui.Tooltip("Insert above");

            // Remove this row.
            gui.SameLine(0, 4);
            if (gui.Button($"{EditorIcons.Delete}##rm", new SysVec2(btnW, 0))) removeIndex = captured;
            if (gui.IsItemHovered()) gui.Tooltip("Remove");

            gui.PopId();
        }

        // Apply at most ONE structural change per frame (the buttons are mutually exclusive in practice — a
        // single click — but order the checks so a deferred change is unambiguous). Each is a single undo.
        if (moveFrom >= 0 && moveTo >= 0)
            CollectionMove(p, collType, elemType, list, isArray, moveFrom, moveTo);
        else if (insertIndex >= 0)
            CollectionInsertAt(p, collType, elemType, list, isArray, insertIndex);
        else if (removeIndex >= 0)
            CollectionRemoveAt(p, collType, elemType, list, isArray, removeIndex);
    }

    // Add a default element. List<T> grows in place then writes back (the same instance, but Set still
    // broadcasts + dirties); an array is immutable-length so a new, one-longer array is built. One undo.
    void CollectionAdd(Inspector.IProperty p, Type collType, Type elemType,
        System.Collections.IList list, bool isArray) {
        // p.Set routes through ApplyMember (multi-select broadcast) -> whole-scene Structural.
        EditorCommands.Structural($"Add to {p.Label}", () => {
            object def = DefaultElement(elemType);
            if (isArray) {
                int n = list?.Count ?? 0;
                var grown = Array.CreateInstance(elemType, n + 1);
                for (int i = 0; i < n; i++) grown.SetValue(list[i], i);
                grown.SetValue(def, n);
                p.Set(grown);
            }
            else {
                System.Collections.IList target = list ?? (System.Collections.IList)Activator.CreateInstance(collType);
                target.Add(def);
                p.Set(target);
            }
            state.MarkViewportDirty();
        });
    }

    // Remove element at index. List<T> removes in place; an array rebuilds one shorter. One undo.
    void CollectionRemoveAt(Inspector.IProperty p, Type collType, Type elemType,
        System.Collections.IList list, bool isArray, int index) {
        if (list is null || index < 0 || index >= list.Count) return;
        // p.Set routes through ApplyMember (multi-select broadcast) -> whole-scene Structural.
        EditorCommands.Structural($"Remove from {p.Label}", () => {
            if (isArray) {
                int n = list.Count;
                var shrunk = Array.CreateInstance(elemType, n - 1);
                for (int i = 0, j = 0; i < n; i++)
                    if (i != index) shrunk.SetValue(list[i], j++);
                p.Set(shrunk);
            }
            else {
                list.RemoveAt(index);
                p.Set(list);
            }
            state.MarkViewportDirty();
        });
    }

    // Move the element at `from` to `to` (reorder). Used for the up/down buttons (adjacent swap), but written
    // generally so a future drag-reorder can reuse it. List<T> moves in place; an array rebuilds (immutable
    // length, but a move is a permutation so the length is unchanged). One undo. The WHOLE collection is
    // written back through the property so ApplyMember broadcasts + dirties, exactly like Add/Remove.
    void CollectionMove(Inspector.IProperty p, Type collType, Type elemType,
        System.Collections.IList list, bool isArray, int from, int to) {
        if (list is null) return;
        int n = list.Count;
        if (from < 0 || from >= n || to < 0 || to >= n || from == to) return;
        EditorCommands.Structural($"Reorder {p.Label}", () => {
            if (isArray) {
                var moved = Array.CreateInstance(elemType, n);
                for (int i = 0; i < n; i++) moved.SetValue(list[i], i);
                object tmp = moved.GetValue(from);
                // Shift the gap between from..to, then drop the moved value at `to` (stable for an adjacent swap).
                if (from < to)
                    for (int i = from; i < to; i++) moved.SetValue(moved.GetValue(i + 1), i);
                else
                    for (int i = from; i > to; i--) moved.SetValue(moved.GetValue(i - 1), i);
                moved.SetValue(tmp, to);
                p.Set(moved);
            }
            else {
                object tmp = list[from];
                list.RemoveAt(from);
                list.Insert(to, tmp);
                p.Set(list);
            }
            state.MarkViewportDirty();
        });
    }

    // Insert a default element BEFORE index. List<T> inserts in place; an array rebuilds one longer. One undo.
    void CollectionInsertAt(Inspector.IProperty p, Type collType, Type elemType,
        System.Collections.IList list, bool isArray, int index) {
        EditorCommands.Structural($"Insert into {p.Label}", () => {
            object def = DefaultElement(elemType);
            if (isArray) {
                int n = list?.Count ?? 0;
                int at = Math.Clamp(index, 0, n);
                var grown = Array.CreateInstance(elemType, n + 1);
                for (int i = 0, j = 0; i < n + 1; i++)
                    grown.SetValue(i == at ? def : list[j++], i);
                p.Set(grown);
            }
            else {
                System.Collections.IList target = list ?? (System.Collections.IList)Activator.CreateInstance(collType);
                target.Insert(Math.Clamp(index, 0, target.Count), def);
                p.Set(target);
            }
            state.MarkViewportDirty();
        });
    }

    // Clear the whole collection. List<T> -> an empty List<T>; an array -> a zero-length array (both keep the
    // member a non-null EMPTY collection, never null, so the editor + serializer treat it as "authored empty").
    // One undo.
    void CollectionClear(Inspector.IProperty p, Type collType, Type elemType, bool isArray) {
        EditorCommands.Structural($"Clear {p.Label}", () => {
            if (isArray)
                p.Set(Array.CreateInstance(elemType, 0));
            else
                p.Set((System.Collections.IList)Activator.CreateInstance(collType));
            state.MarkViewportDirty();
        });
    }

    // Write a single element back. Mutates the backing IList slot (works for both List<T> and T[], which both
    // implement IList) then writes the WHOLE collection through the property so ApplyMember broadcasts the
    // edited collection to the multi-selection + marks dirty (the element terminal drawer already registered
    // the per-drag undo, like a primitive member edit).
    void CollectionSetElement(Inspector.IProperty p, System.Collections.IList list, int index, object value) {
        if (list is null || index < 0 || index >= list.Count) return;
        list[index] = value;
        p.Set(list);
    }

    // A sensible default for a new element: value types get their zero (Activator), reference/string types
    // get null (an empty asset/scene-object slot the user then fills via its picker -- matches Unity adding a
    // null Object slot). EntityRef/ComponentRef are structs -> their None default. Strings start empty.
    static object DefaultElement(Type elemType) {
        if (elemType == typeof(string)) return "";
        if (elemType.IsValueType) return Activator.CreateInstance(elemType);
        return null;
    }

    // G2-editor (ch21, Rule 2): the interactive Dictionary<K,V> editor (the parallel of DrawCollectionSlot for
    // the dictionary category). Renders, inside the value column the BeginRow opened: a "(N entries)" header +
    // an "Add" button, then one row per entry -- a READ-ONLY key label + the VALUE drawn RECURSIVELY by its own
    // terminal drawer (a Dictionary<string,int> draws int widgets, a Dictionary<string,Material> asset slots, a
    // Dictionary<int,EntityRef> scene-object slots) + a Remove (X) button. Structural changes (Add / Remove)
    // mutate the backing dictionary and write it back through the property (-> ApplyMember broadcast + dirty)
    // under one EditorCommands.Structural; value edits push undo via the value's own terminal drawer. Keys are READ-ONLY
    // (Dictionary keys are immutable: in-place key edit = remove-old + add-new with a duplicate-key clash to
    // resolve -- deferred, ch21 scope). A value type with no registered drawer shows Unsupported per-cell, like
    // a struct member (G4). The key snapshot avoids a mid-iteration mutate.
    void DrawDictionarySlot(Inspector.IProperty p) {
        Type dictType = p.ValueType;
        Type[] args = dictType.GetGenericArguments();
        Type keyType = args[0];
        Type valueType = args[1];
        object boxed = p.Get();
        System.Collections.IDictionary dict = boxed as System.Collections.IDictionary;

        int count = dict?.Count ?? 0;

        // Header row inside the value column: entry count + an Add button (full row width split).
        gui.AlignTextToFramePadding();
        gui.TextDisabled($"{count} {(count == 1 ? "entry" : "entries")}");
        gui.SameLine();
        float addW = gui.FrameHeight + 24;
        gui.CursorPosX = (gui.CursorPosX + Math.Max(0, gui.ContentRegionAvail.X - addW));
        if (gui.Button($"{EditorIcons.Add} Add##adddict_{p.Name}", new SysVec2(addW, 0))) {
            DictionaryAdd(p, dictType, keyType, valueType, dict);
            return; // structural change: redraw next frame against the new dictionary (avoids a stale row)
        }

        if (count == 0)
            return;

        // Snapshot the keys so removing/editing inside the loop never mutates the live key collection mid-iter.
        var keys = new System.Collections.Generic.List<object>(count);
        foreach (object k in dict.Keys) keys.Add(k);

        // Per-entry rows: a READ-ONLY key label, then the value drawn by its own terminal drawer through a
        // DictionaryValueProperty, followed by a Remove button. PushId per index so duplicate values keep
        // distinct ImGui ids.
        object removeKey = null;
        bool hasRemove = false;
        for (int i = 0; i < keys.Count; i++) {
            gui.PushId(i);
            object key = keys[i];
            float removeW = gui.FrameHeight;
            float avail = gui.ContentRegionAvail.X;
            float keyW = Math.Max(40f, avail * 0.4f);
            float valW = Math.Max(40f, avail - keyW - removeW - 12);

            // Key: read-only label (Dictionary keys are immutable in this version).
            gui.AlignTextToFramePadding();
            gui.SetNextItemWidth(keyW);
            gui.Text(key?.ToString() ?? "(null)");
            if (gui.IsItemHovered())
                gui.Tooltip("Dictionary key (read-only)");
            gui.SameLine(0, 6);

            // Value: recursive terminal drawer (a value edit writes dict[key] = v then the whole dict back).
            var valProp = new Inspector.DictionaryValueProperty(
                $"Value {i}", valueType,
                () => dict.Contains(key) ? dict[key] : null,
                v => DictionarySetValue(p, dict, key, v));

            ITypeDrawer drawer = memberRegistry.Resolve(valueType);
            gui.SetNextItemWidth(valW);
            if (drawer is not null) {
                componentGui.SetUndoLabel($"Edit {p.Label} [{key}]");
                drawer.Draw(valProp, componentGui);
            }
            else {
                gui.TextDisabled($"({valueType.Name})"); // no drawer for this value type (e.g. nested struct)
            }

            gui.SameLine(0, 6);
            if (gui.Button($"{EditorIcons.Delete}", new SysVec2(removeW, 0))) {
                removeKey = key;
                hasRemove = true;
            }

            gui.PopId();
        }

        if (hasRemove)
            DictionaryRemove(p, dict, removeKey);
    }

    // Add a default entry with a freshly minted UNIQUE key (Dictionary keys must be distinct). The default
    // value follows DefaultElement. One undo; mutate then write back through the property.
    void DictionaryAdd(Inspector.IProperty p, Type dictType, Type keyType, Type valueType,
        System.Collections.IDictionary dict) {
        System.Collections.IDictionary target = dict ?? (System.Collections.IDictionary)Activator.CreateInstance(dictType);
        object key = UniqueDictKey(target, keyType);
        if (key is null) return; // can't synthesize a unique key for this key type -> Add is a no-op
        // p.Set routes through ApplyMember (multi-select broadcast) -> whole-scene Structural.
        EditorCommands.Structural($"Add to {p.Label}", () => {
            target[key] = DefaultElement(valueType);
            p.Set(target);
            state.MarkViewportDirty();
        });
    }

    // Remove the entry for the given key. One undo; mutate then write back through the property.
    void DictionaryRemove(Inspector.IProperty p, System.Collections.IDictionary dict, object key) {
        if (dict is null || key is null || !dict.Contains(key)) return;
        // p.Set routes through ApplyMember (multi-select broadcast) -> whole-scene Structural.
        EditorCommands.Structural($"Remove from {p.Label}", () => {
            dict.Remove(key);
            p.Set(dict);
            state.MarkViewportDirty();
        });
    }

    // Write a single entry's value back. Sets dict[key] = value (key already present), then writes the WHOLE
    // dictionary through the property so ApplyMember broadcasts the edited dictionary to the multi-selection +
    // marks dirty (the value terminal drawer already registered the per-drag undo, like a primitive edit).
    void DictionarySetValue(Inspector.IProperty p, System.Collections.IDictionary dict, object key, object value) {
        if (dict is null || key is null || !dict.Contains(key)) return;
        dict[key] = value;
        p.Set(dict);
    }

    // Mint a key not already present. string -> "" then "key", "key2", ...; integral types -> max existing + 1
    // (or 0 when empty); other value-type keys -> their zero default IF unused else give up (returns null).
    // Keeps Add simple: most dictionaries are keyed by string or int (the common, supported case).
    static object UniqueDictKey(System.Collections.IDictionary dict, Type keyType) {
        if (keyType == typeof(string)) {
            if (!dict.Contains("")) return "";
            for (int i = 1; i < 100000; i++) {
                string cand = "key" + i;
                if (!dict.Contains(cand)) return cand;
            }
            return null;
        }
        if (IsIntegralKey(keyType)) {
            long max = -1;
            foreach (object k in dict.Keys) {
                long v = Convert.ToInt64(k);
                if (v > max) max = v;
            }
            object next = Convert.ChangeType(max + 1, keyType);
            return dict.Contains(next) ? null : next;
        }
        if (keyType.IsValueType) {
            object def = Activator.CreateInstance(keyType);
            return dict.Contains(def) ? null : def; // single zero-default slot; no synthesis for arbitrary structs
        }
        return null; // reference-type keys (rare) -> no synthesizable unique key
    }

    static bool IsIntegralKey(Type t) =>
        t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte) ||
        t == typeof(uint) || t == typeof(ulong) || t == typeof(ushort) || t == typeof(sbyte);

    // G3-editor (ch23, Rule 2): the interactive [SerializeReference] polymorphism editor for an interface /
    // abstract member (the parallel of DrawCollectionSlot / DrawDictionarySlot for the Polymorphic category).
    // Renders, inside the value column the BeginRow opened:
    //   (1) a concrete-type DROPDOWN -- "None" + every instantiable type deriving from the declared base
    //       (TypeCache.GetTypesDerivedFrom, deterministically ordered, concrete + public-ctor only); the live
    //       value's actual type is preselected. Changing it Activator-creates the chosen type (None -> null),
    //       writes it through the property (-> ApplyMember broadcast + dirty) under one EditorCommands.Structural, then
    //       returns so next frame redraws against the new instance.
    //   (2) when a value is set, a foldout whose body draws the instance's members RECURSIVELY through the SAME
    //       component drawer stack as a top-level member (componentStack.Draw(MemberProperty)). Because each
    //       child is a REAL reflected member (a MemberInfo, unlike a collection element / dictionary value), its
    //       [Range]/[Tooltip]/[ShowIf]/[ReadOnly] attributes work, and a nested [SerializeReference] member
    //       resolves THIS drawer again -> nested polymorphism auto-recurses (CompositeModifier.Inner).
    // The instance is a reference type (an interface / abstract base is implemented by a class), so a child
    // member write mutates the instance in place; the apply delegate also writes the WHOLE instance back through
    // the slot's property so the multi-selection broadcast + dirty fire exactly like a primitive member edit.
    void DrawPolymorphicSlot(Inspector.IProperty p, Type declaredType) {
        object instance = p.Get();
        Type actual = instance?.GetType();

        // The implementor options: "None" plus every instantiable concrete derived type (deterministically
        // ordered by TypeCache). The combo shows the current selection by name; per-item equality (`t == actual`)
        // drives the checkmark + suppresses a redundant rebuild when the same type is re-picked.
        System.Collections.Generic.IReadOnlyList<Type> derived = TypeCache.GetTypesDerivedFrom(declaredType);

        string current = actual is null ? "None" : Prettify(actual.Name);
        bool typeChanged = false;
        gui.SetNextItemWidth(-1);
        if (gui.BeginCombo($"##poly_{p.Name}", current)) {
            // None
            if (gui.Selectable("None", actual is null) && actual is not null) {
                PolymorphicSet(p, null);
                typeChanged = true;
            }
            // Each derived concrete type: short Name (more readable), FullName tooltip (disambiguates collisions).
            for (int i = 0; i < derived.Count; i++) {
                Type t = derived[i];
                bool isSel = t == actual;
                if (gui.Selectable($"{Prettify(t.Name)}##{i}", isSel) && !isSel) {
                    PolymorphicSet(p, Activator.CreateInstance(t));
                    typeChanged = true;
                }
                if (gui.IsItemHovered())
                    gui.Tooltip(t.FullName);
            }
            gui.EndCombo();
        }

        // Structural change this frame: the slot's value is a new instance / null now -- redraw next frame
        // against it (the local `instance`/`actual` are stale), exactly like DrawCollectionSlot's Add/Remove.
        if (typeChanged || instance is null)
            return; // None or just-changed: nothing to expand this frame

        // A value is set: draw its members in a collapsible foldout (the recursion). The members go in their own
        // nested grid (BeginGrid) so the shared stack's BeginRow (TableNextRow) has an open table to write into.
        if (gui.TreeNodeEx($"{Prettify(actual.Name)}###polybody_{p.Name}",
                EditorTreeFlags.DefaultOpen | EditorTreeFlags.SpanAvailWidth)) {
            object boundInstance = instance; // capture for the per-member apply delegates
            // EF16: draw the body grid one depth deeper with the TreeNode's full indent cancelled + a
            // fixed-width label column, so the value boxes keep full width at every nesting level.
            DrawNestedBody(() => {
            if (BeginNestedGrid($"##polymembers_{p.Name}_{actual.Name}")) {
                foreach (MemberInfo member in ComponentReflection.InspectorMembers(actual)) {
                    MemberAttributes attrs = MemberAttributes.For(member);
                    // [ShowIf]/[HideIf] at this level too (same skip DrawMemberList does for top-level members).
                    if (!Conditions.Visible(attrs.Conditionals, boundInstance))
                        continue;
                    // Each child member flows through the SAME component stack as a top-level member: a real
                    // MemberProperty (MemberInfo present -> attributes honored). The apply delegate writes the
                    // member on the instance, then writes the WHOLE instance back up through the slot's property
                    // so the edit broadcasts to the multi-selection + marks dirty (the terminal drawer already
                    // registered the per-edit undo, like any primitive member edit).
                    MemberInfo capturedMember = member;
                    componentStack.Draw(new Inspector.MemberProperty(capturedMember, boundInstance,
                        v => {
                            ComponentReflection.SetValue(capturedMember, boundInstance, v);
                            p.Set(boundInstance);            // chain the whole instance up (-> ApplyMember + dirty)
                            state.MarkViewportDirty();
                        }), componentGui);
                }
                gui.EndTable();
            }
            });
            gui.TreePop();
        }
    }

    // Assign a new polymorphic instance (or null for None) onto the slot's property. One undo per type change;
    // IProperty.Set routes through ApplyMember (multi-select broadcast) + dirty. The instance is freshly
    // constructed (or null), so this is a structural change -> the caller returns and redraws next frame.
    void PolymorphicSet(Inspector.IProperty p, object instance) {
        // IProperty.Set routes through ApplyMember (multi-select broadcast) -> whole-scene Structural.
        EditorCommands.Structural($"Set {p.Label}", () => {
            p.Set(instance);
            state.MarkViewportDirty();
        });
    }

    // editor-rework G4-editor (ch24, Rule 2): the recursive nested struct/class editor (the parallel of
    // DrawPolymorphicSlot, but the type is FIXED -- the declared type IS the concrete type, so there is no
    // implementor dropdown, just the member foldout). Renders, inside the value column the BeginRow opened:
    //   (1) for a CLASS member that is null, lazily Activator-creates + writes back the instance so it is
    //       editable (a struct member is a value type -> never null, so this only fires for a reference type
    //       with a public parameterless ctor; one without is left as the dead label, like the polymorphic
    //       drawer's None). The lazy create is a one-time structural change -> return + redraw next frame.
    //   (2) a foldout whose body draws the instance's members RECURSIVELY through the SAME component drawer
    //       stack as a top-level member (componentStack.Draw(MemberProperty)) -- each child is a REAL reflected
    //       member, so [Range]/[Tooltip]/[ShowIf]/[ReadOnly] work and a nested-in-nested member resolves THIS
    //       drawer again (auto-recursion).
    // ** STRUCT WRITE-BACK (the G4 fix ch20/21/23 deferred): the apply delegate writes the inner field on the
    // instance, then writes the WHOLE instance back through the slot's property. For a CLASS the instance is a
    // reference (the field write already landed; p.Set re-broadcasts + dirties). For a STRUCT the instance is a
    // BOXED copy -- ComponentReflection.SetValue mutates the box, and p.Set(boxedInstance) unboxes it back into
    // the parent member (the value-type write-back). The SAME code path serves both because p.Set always writes
    // the boxed/referenced instance up the chain (-> ApplyMember broadcast + dirty), exactly like a primitive.
    void DrawNestedSlot(Inspector.IProperty p, Type declaredType) {
        object instance = p.Get();

        // A null CLASS member: lazily build it so its members are editable (a struct value is never null). A
        // type with no public parameterless ctor stays the dead label (Activator throws -> caught -> Unsupported).
        if (instance is null) {
            if (declaredType.IsValueType) {
                // Defensive: a boxed value type read should never be null, but if a sibling target's value is
                // null (multi-select) fall back to a fresh default so the foldout still draws.
                instance = Activator.CreateInstance(declaredType);
            } else {
                object created;
                try { created = Activator.CreateInstance(declaredType); }
                catch { gui.TextDisabled($"({Prettify(declaredType.Name)})"); return; }
                // p.Set routes through ApplyMember (multi-select broadcast) -> whole-scene Structural.
                EditorCommands.Structural($"Set {p.Label}", () => {
                    p.Set(created);             // structural change: the member is no longer null
                    state.MarkViewportDirty();
                });
                return;                         // redraw next frame against the new instance
            }
        }

        Type actual = instance.GetType();
        // Draw the instance's members in a collapsible foldout (the recursion). The members go in their own
        // nested grid so the shared stack's BeginRow (TableNextRow) has an open table to write into.
        if (gui.TreeNodeEx($"{Prettify(declaredType.Name)}###nestedbody_{p.Name}",
                EditorTreeFlags.DefaultOpen | EditorTreeFlags.SpanAvailWidth)) {
            object boundInstance = instance;    // capture for the per-member apply delegates (boxed for a struct)
            // EF16: body grid drawn one depth deeper with the TreeNode's full indent cancelled + fixed-width
            // label column, so the value boxes keep full width at every nesting level (see DrawNestedBody).
            DrawNestedBody(() => {
            if (BeginNestedGrid($"##nestedmembers_{p.Name}_{actual.Name}")) {
                foreach (MemberInfo member in ComponentReflection.InspectorMembers(actual)) {
                    MemberAttributes attrs = MemberAttributes.For(member);
                    if (!Conditions.Visible(attrs.Conditionals, boundInstance))
                        continue;
                    MemberInfo capturedMember = member;
                    componentStack.Draw(new Inspector.MemberProperty(capturedMember, boundInstance,
                        v => {
                            ComponentReflection.SetValue(capturedMember, boundInstance, v);
                            p.Set(boundInstance);   // chain the WHOLE instance up (struct: unbox write-back; class: re-broadcast)
                            state.MarkViewportDirty();
                        }), componentGui);
                }
                gui.EndTable();
            }
            });
            gui.TreePop();
        }
    }

    // Accepts a Hierarchy entity-drag payload (int = entity InstanceId hash, set by HierarchyPanel's
    // EntityDragType source) onto the current item and resolves it back to the live entity. Mirrors
    // BEventEditor.AcceptEntityDrop exactly so the drag-onto-slot UX matches the event editor.
    static bool AcceptEntityDrop(out Entity entity) {
        entity = null;
        if (!gui.BeginDragDropTarget())
            return false;
        if (gui.AcceptDragDropPayloadInt("BALLISTIC_ENTITY") is { } hash) {
            foreach (Entity e in SceneManager.GetCurrentScene().Entities)
                if (e.InstanceId.GetHashCode() == hash) { entity = e; break; }
        }
        gui.EndDragDropTarget();
        return entity is not null;
    }

    // Scene-object picker popup: search + every live scene entity (EntityRef) or every behaviour under each
    // entity (ComponentRef); click to assign, (None) clears. The parallel of DrawAssetPickerPopup, but over
    // the live scene (SceneManager.GetCurrentScene().Entities) instead of the AssetDatabase.
    void DrawSceneObjectPickerPopup() {
        float u = gui.FontSize;
        gui.SetNextWindowSizeAppearing(new SysVec2(u * 28f, u * 30f));
        if (!gui.BeginPopup("##scenerefpicker"))
            return;

        gui.PushItemSpacing(new SysVec2(8, 6));

        Inspector.IProperty p = sceneRefPickerProperty;
        bool isComponentRef = p is not null && p.ValueType == typeof(ComponentRef);
        string typeName = isComponentRef ? "Component" : "Entity";

        gui.PushFont(EditorFont.Bold);
        gui.TextUnformatted($"Select {typeName}");
        gui.PopFont();
        gui.Spacing();

        if (gui.IsWindowAppearing())
            gui.SetKeyboardFocusHere();
        gui.SetNextItemWidth(-1);
        gui.InputTextWithHint("##search", $"{EditorIcons.Search} Search {typeName.ToLowerInvariant()}s...",
            ref sceneRefSearch, 128);
        gui.Separator();

        gui.BeginChild("##list", default, border: false);

        // (None) clears the slot to the default (Guid.Empty) ref.
        gui.PushColor(EditorStyleColor.Text, gui.StyleColor(EditorStyleColor.TextDisabled));
        if (gui.Selectable("  (None)", false, new SysVec2(0, gui.FrameHeight))) {
            if (p is not null)
                AssignSceneRef(p, isComponentRef ? (object)ComponentRef.None : EntityRef.None);
            gui.CloseCurrentPopup();
        }
        gui.PopColor();

        var any = false;
        if (p is not null) {
            foreach (Entity e in SceneManager.GetCurrentScene().Entities) {
                if (e is null || e.IsDestroyed)
                    continue;
                if (!isComponentRef) {
                    if (!MatchesSearch(e.Name))
                        continue;
                    any = true;
                    (string icon, SysVec4 tint) = EditorIcons.ForEntity(e);
                    if (SceneRefRow(icon, tint, e.Name, e.InstanceId)) {
                        AssignSceneRef(p, new EntityRef(e));
                        gui.CloseCurrentPopup();
                    }
                }
                else {
                    foreach (Behaviour b in e.Behaviours) {
                        if (b is null)
                            continue;
                        string rowName = $"{e.Name} : {Prettify(b.GetType().Name)}";
                        if (!MatchesSearch(rowName))
                            continue;
                        any = true;
                        (string icon, SysVec4 tint) = EditorIcons.ForComponentType(b.GetType());
                        if (SceneRefRow(icon, tint, rowName, b.InstanceId)) {
                            AssignSceneRef(p, new ComponentRef(b));
                            gui.CloseCurrentPopup();
                        }
                    }
                }
            }
        }

        if (!any)
            gui.TextDisabled(sceneRefSearch.Length > 0
                ? "No matching scene objects."
                : $"No {typeName.ToLowerInvariant()}s in the scene.");

        gui.EndChild();
        gui.PopStyleVar();
        gui.EndPopup();
    }

    bool MatchesSearch(string name) =>
        sceneRefSearch.Length == 0 || name.Contains(sceneRefSearch, StringComparison.OrdinalIgnoreCase);

    // One picker row (icon + name), keyed by the target InstanceId so duplicate names stay distinct ids.
    static bool SceneRefRow(string icon, SysVec4 tint, string name, Guid instanceId) {
        bool clicked = gui.Selectable($"      {name}##{instanceId:N}", false, new SysVec2(0, gui.FrameHeight));
        SysVec2 rmin = gui.ItemRectMin;
        EditorIcons.DrawAt(new SysVec2(rmin.X + 6,
            rmin.Y + (gui.FrameHeight - gui.TextLineHeight) * 0.5f), icon, tint);
        return clicked;
    }

    // RW1.4: DrawVolumeProfileAsset body MOVED into VolumeProfileAssetInspector
    // (Inspector/AssetInspectors/AssetInspectors.cs).

    static string[] CompatibleExtensions(Type assetType) {
        if (assetType is null) return [];
        if (typeof(VolumeProfile).IsAssignableFrom(assetType))
            return [".volume"];
        if (typeof(Texture3D).IsAssignableFrom(assetType))
            return [".cubemap", ".hdr", ".exr", ".png", ".jpg", ".jpeg"];
        if (typeof(Texture2D).IsAssignableFrom(assetType))
            return [".png", ".jpg", ".jpeg", ".tga", ".bmp", ".hdr", ".exr"];
        if (typeof(Mesh).IsAssignableFrom(assetType))
            return [".fbx", ".obj"];
        if (typeof(AudioClip).IsAssignableFrom(assetType))
            return [".wav", ".wave", ".ogg"];
        if (typeof(Material).IsAssignableFrom(assetType))
            return [".mat"];
        if (typeof(Shader).IsAssignableFrom(assetType))
            return [".shader"];
        if (typeof(TerrainAsset).IsAssignableFrom(assetType))
            return [".terrain"];
        if (typeof(PrefabAsset).IsAssignableFrom(assetType))
            return [".prefab"];
        if (typeof(DataAsset).IsAssignableFrom(assetType))
            return [".asset"];
        return [];
    }

    // Centered accent-tinted Add Component button with a searchable popup, Unity-style.
    void DrawAddComponent(Entity entity) {
        float avail = gui.ContentRegionAvail.X;
        float w = Math.Clamp(avail * 0.72f, 180f, 320f);
        gui.CursorPosX = (gui.CursorPosX + (avail - w) * 0.5f);

        SysVec4 accent = gui.StyleColor(EditorStyleColor.CheckMark);
        gui.PushColor(EditorStyleColor.Button, new SysVec4(accent.X, accent.Y, accent.Z, 0.16f));
        gui.PushColor(EditorStyleColor.ButtonHovered, new SysVec4(accent.X, accent.Y, accent.Z, 0.30f));
        gui.PushColor(EditorStyleColor.ButtonActive, new SysVec4(accent.X, accent.Y, accent.Z, 0.42f));
        var clicked = gui.Button($"{EditorIcons.Add}  Add Component", new SysVec2(w, 0));
        gui.PopColor(3);

        if (clicked) {
            addComponentSearch = "";
            gui.OpenPopup("##addcomponent");
        }

        DrawAddComponentPopup(entity);
    }

    // Unity-style component browser: a roomy popup with a search box and the registry grouped into
    // collapsible categories (by ComponentEntry.Menu). Searching flattens to a filtered list and
    // Enter adds the top hit. Rows carry the type icon + tint.
    void DrawAddComponentPopup(Entity entity) {
        // Sized off the current font so it scales with DPI/UI-scale without a controller handle.
        float u = gui.FontSize;
        gui.SetNextWindowSizeAppearing(new SysVec2(u * 26f, u * 31f));
        if (!gui.BeginPopup("##addcomponent"))
            return;

        gui.PushItemSpacing(new SysVec2(8, 6));

        // Header.
        gui.PushFont(EditorFont.Bold);
        gui.TextUnformatted("Add Component");
        gui.PopFont();
        gui.Spacing();

        if (gui.IsWindowAppearing())
            gui.SetKeyboardFocusHere();
        gui.SetNextItemWidth(-1);
        // NOTE: do NOT use EnterReturnsTrue here — with Hexa's managed ref-string overload that flag
        // defers the buffer write-back until Enter, so live typing wouldn't filter. Detect Enter
        // separately while the field is active.
        gui.InputTextWithHint("##addsearch", $"{EditorIcons.Search} Search components...",
            ref addComponentSearch, 128);
        bool enter = gui.IsItemFocused() && gui.KeyPressed(EditorGuiKey.Enter);
        gui.Separator();

        bool searching = addComponentSearch.Length > 0;
        bool Matches(ComponentEntry e) =>
            !searching || e.DisplayName.Contains(addComponentSearch, StringComparison.OrdinalIgnoreCase);

        void Add(ComponentEntry e) {
            // Adds a component to the scene graph (the multi-select branch touches N entities), so this is
            // a whole-scene Structural snapshot. CloseCurrentPopup is a UI-state call -> outside the command.
            EditorCommands.Structural($"Add {e.DisplayName}", () => {
                // Multi-selection: add to EVERY selected entity that doesn't already have it (Unity-style),
                // so you can equip a whole group at once. Single selection just adds to this entity.
                if (state.SelectedEntities.Count > 1) {
                    foreach (Entity sel in state.SelectedEntities)
                        if (sel is { IsDestroyed: false } && !HasComponentOfType(sel, e.Type))
                            sel.AddComponent(e.Type);
                }
                else {
                    entity.AddComponent(e.Type);
                }
                state.MarkViewportDirty();
            });
            gui.CloseCurrentPopup();
        }

        gui.BeginChild("##addlist", default, border: false);

        if (searching) {
            // Flat filtered list; Enter adds the first hit.
            ComponentEntry? first = null;
            var any = false;
            foreach (ComponentEntry entry in ComponentRegistry.Menu) {
                if (!Matches(entry)) continue;
                any = true;
                first ??= entry;
                if (AddComponentRow(entry))
                    Add(entry);
            }
            if (!any)
                gui.TextDisabled("No components match.");
            if (enter && first is { } f)
                Add(f);
        }
        else {
            // Grouped by category (Menu); ungrouped entries fall under "General".
            foreach (var group in ComponentRegistry.Menu
                         .GroupBy(e => string.IsNullOrEmpty(e.Menu) ? "General" : e.Menu)
                         .OrderBy(g => g.Key == "General" ? "zzz" : g.Key)) {
                if (!gui.CollapsingHeader(group.Key, defaultOpen: true))
                    continue;
                foreach (ComponentEntry entry in group)
                    if (AddComponentRow(entry))
                        Add(entry);
            }
        }

        gui.EndChild();
        gui.PopStyleVar();
        gui.EndPopup();
    }

    // One selectable component row: type icon + display name, taller than a default row.
    static bool AddComponentRow(ComponentEntry entry) {
        (string icon, SysVec4 tint) = EditorIcons.ForComponentType(entry.Type);
        bool clicked = gui.Selectable($"      {entry.DisplayName}##add{entry.Type.FullName}",
            false, new SysVec2(0, gui.FrameHeight));
        SysVec2 min = gui.ItemRectMin;
        EditorIcons.DrawAt(new SysVec2(min.X + 6, min.Y + (gui.FrameHeight - gui.TextLineHeight) * 0.5f),
            icon, tint);
        return clicked;
    }

    // ---- Multi-asset inspector -------------------------------------------------

    // Shown when the browser has 2+ assets selected: the selection list, batch import settings
    // (texture type, when every selected asset is an image), and batch delete.
    unsafe void DrawMultiAssetInspector() {
        var assets = state.SelectedAssets;

        // Header.
        var draw = gui.WindowDrawList;
        SysVec2 start = gui.CursorScreenPos;
        float iconSize = 36f;
        if (ImGuiController.HasIcons) {
            draw.AddText(EditorFont.LargeIcons, iconSize, start + new SysVec2(0, 2),
                gui.ColorU32(new SysVec4(0.70f, 0.76f, 0.86f, 1f)), EditorIcons.Document);
            gui.SetCursorScreenPos(start + new SysVec2(iconSize + 10, 0));
        }
        gui.BeginGroup();
        draw.AddText(EditorFont.Bold, gui.FontSize, gui.CursorScreenPos,
            gui.ColorU32(gui.StyleColor(EditorStyleColor.Text)), $"{assets.Count} assets selected");
        gui.Dummy(new SysVec2(0, gui.TextLineHeight));
        gui.TextDisabled("Edits below apply to the whole selection.");
        gui.EndGroup();
        gui.Spacing();
        gui.Separator();
        gui.Spacing();

        // BY-TYPE breakdown instead of a flat file list: "3 Volume", "2 Terrain", ... — and clicking
        // a type row narrows the selection to just that type (Unity-style "select all of a kind").
        var byExt = assets
            .GroupBy(a => Path.GetExtension(a.Path).ToLowerInvariant())
            .OrderByDescending(g => g.Count())
            .ToList();
        gui.TextDisabled("By type (click to select just that kind):");
        gui.Spacing();
        foreach (var group in byExt) {
            string ext = group.Key;
            (string icon, SysVec4 tint) = EditorIcons.ForAssetExtension(ext);
            string typeName = string.IsNullOrEmpty(ext) ? "File" : ext.TrimStart('.');
            int n = group.Count();
            gui.PushColor(EditorStyleColor.Text, tint);
            gui.TextUnformatted(icon);
            gui.PopColor();
            gui.SameLine(0, 6);
            if (gui.Selectable($"{n}  {typeName}{(n == 1 ? "" : "s")}##type{ext}", false)) {
                var ofType = group.ToList();
                state.SelectAssets(ofType, ofType[^1]);
            }
        }

        gui.Spacing();

        // Batch texture type — only when every selected asset is an image with a meta file.
        var allmmages = assets.All(a => Path.GetExtension(a.Path).ToLowerInvariant()
            is ".png" or ".jpg" or ".jpeg" or ".tga" or ".bmp" or ".hdr" or ".exr");
        if (allmmages)
            DrawBatchTextureType(assets);

        gui.Spacing();
        gui.Separator();
        gui.Spacing();

        gui.PushColor(EditorStyleColor.Button, EditorTheme.Destructive);
        gui.PushColor(EditorStyleColor.ButtonHovered, EditorTheme.DestructiveHovered);
        if (gui.Button($"{EditorIcons.Delete}  Delete {assets.Count} Assets", new SysVec2(-1, 0)))
            AssetOps.DeleteAssets(state, assets);
        gui.PopColor(2);
    }

    static void DrawBatchTextureType(List<(string Path, Guid Guid)> assets) {
        // Mixed values show no preselection; choosing a type writes every meta and reimports once.
        TextureType? shared = null;
        var mixed = false;
        foreach ((_, Guid guid) in assets) {
            if (!AssetDatabase.TryGetMeta(guid, out MetaFile meta))
                continue;
            TextureType t = TextureImporter.TypeFromSettings(meta.Settings);
            if (shared is null) shared = t;
            else if (shared != t) { mixed = true; break; }
        }

        if (!BeginGrid("##batchtex"))
            return;

        Row(mixed ? "Texture Type (mixed)" : "Texture Type");
        string[] names = Enum.GetNames<TextureType>();
        int index = mixed || shared is null ? -1 : Array.IndexOf(names, shared.ToString());
        gui.SetNextItemWidth(-1);
        if (gui.Combo("##batchtextype", ref index, names) && index >= 0) {
            var reimport = new List<Guid>();
            foreach ((string path, Guid guid) in assets) {
                if (!AssetDatabase.TryGetMeta(guid, out MetaFile meta))
                    continue;
                meta.Settings["textureType"] = names[index];
                meta.Save(MetaFile.PathFor(AssetDatabase.Project.ResolveAbsolute(path)));
                reimport.Add(guid);
            }
            AsyncAssetImport.Request($"Reimporting {reimport.Count} textures...", onFinished: () => {
                foreach (Guid guid in reimport)
                    AssetDatabase.Invalidate(guid);
            });
        }

        gui.EndTable();
    }

    // ---- Asset inspector -----------------------------------------------------

    // B2 (Rule 1): the asset inspector resolves the custom body for the selected asset's extension from
    // AssetInspectorRegistry instead of the old `switch (ext)` god-switch. Each former case is a
    // self-registering [AssetInspector(".ext")] class that OWNS its section body (RW1.4 moved every body out of
    // this panel into the shims; render byte-identical). An extension with NO registered inspector draws only
    // the file header above (R1.9's never-blank fallback, byte-identical to the old "just the file header, no
    // clutter" default for models etc.).
    void DrawAssetInspector() {
        var path = state.SelectedAssetPath;
        Guid guid = state.SelectedAssetGuid;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        AssetDatabase.TryGetMeta(guid, out MetaFile meta);

        DrawAssetHeader(path, ext, meta);
        gui.Spacing();

        IAssetInspector inspector = AssetInspectorRegistry.InspectorFor(ext);
        inspector?.Draw(new AssetInspectorContext(this, path, guid, ext, meta));
    }

    // RW1.4: every former asset-inspector section body (scene/pyscene/text-hint, audio/animation clip,
    // prefab, data-asset + its cache fields & Load/SaveDataAsset helpers) MOVED into its
    // [AssetInspector(".ext")] shim in Inspector/AssetInspectors/AssetInspectors.cs — byte-identical render,
    // only the home changed. The shims reach the panel through ctx.Panel (DrawMemberList / MarkViewportDirty /
    // Select) and the audioPreviewVoice static; the DataAsset cache now lives on the shim's single instance.

    // Big type icon + bold file name + dim path/importer lines, divided from the body.
    static unsafe void DrawAssetHeader(string path, string ext, MetaFile meta) {
        (string icon, SysVec4 tint) = EditorIcons.ForAssetExtension(ext);
        var draw = gui.WindowDrawList;
        SysVec2 start = gui.CursorScreenPos;

        float iconSize = 36f;
        if (ImGuiController.HasIcons) {
            draw.AddText(EditorFont.LargeIcons, iconSize, start + new SysVec2(0, 2),
                gui.ColorU32(tint), icon);
            gui.SetCursorScreenPos(start + new SysVec2(iconSize + 10, 0));
        }

        gui.BeginGroup();
        draw.AddText(EditorFont.Bold, gui.FontSize, gui.CursorScreenPos,
            gui.ColorU32(gui.StyleColor(EditorStyleColor.Text)), Path.GetFileName(path));
        gui.Dummy(new SysVec2(0, gui.TextLineHeight));
        gui.TextDisabled(path);
        if (meta is not null)
            gui.TextDisabled(meta.Importer);
        gui.EndGroup();

        gui.Spacing();
        gui.Separator();
    }

    // RW1.4: DrawTextureImportSettings body MOVED into TextureAssetInspector
    // (Inspector/AssetInspectors/AssetInspectors.cs).

    // DrawMaterialEditor + DrawMaterialPreview + the material-preview cache fields + ReferenceToPath /
    // ApplyLiveMaterial / LoadSlot moved to MaterialAssetInspector (Inspector/AssetInspectors/) in RW1.4 — the
    // body is byte-identical to the old inline material editor; only its home changed (the [AssetInspector(".mat")]
    // shim now owns the single preview cache the panel field used to).

    internal static bool AcceptGuidDrop(out Guid guid) {
        guid = Guid.Empty;
        if (!gui.BeginDragDropTarget())
            return false;

        var accepted = false;
        string text = gui.AcceptDragDropPayloadString(AssetBrowserPanel.DragType);
        if (text is not null) {
            // Multi-select drags carry several GUIDs separated by ';' — a single slot takes the first.
            var first = text.Split(';', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            accepted = Guid.TryParse(first, out guid);
        }

        gui.EndDragDropTarget();
        return accepted;
    }

    // ---- Layout helpers --------------------------------------------------------

    internal static bool BeginGrid(string id) {
        // PadOuterX keeps the value column off the panel edge; the slight indent (via a leading
        // column) and inner spacing give the rows a calmer, more deliberate rhythm.
        if (!gui.BeginTable(id, 2, EditorTableFlags.SizingStretchProp | EditorTableFlags.PadOuterX))
            return false;
        gui.TableSetupColumn("label", EditorColumnFlags.WidthStretch, 0.38f);
        gui.TableSetupColumn("value", EditorColumnFlags.WidthStretch, 0.62f);
        ResetRowZebra();   // EF5e: each grid stripes from row 0 so zebra is stable + per-component
        return true;
    }

    // EF16 — nested-grid layout state. The inspector recurses through DrawNestedSlot / DrawPolymorphicSlot
    // (a struct/class/[SerializeReference] member draws its own members in a child grid). The OLD behaviour
    // put each child grid inside a TreeNode's full IndentSpacing (~21px) that marched the WHOLE table — BOTH
    // columns — right one full step per level AND the proportional 0.38/0.62 split re-shrank the value column
    // at every level, so a `list → element → struct → field` chain pushed the value box off the panel within
    // a few levels (the "never fits" report). `nestDepth` carries the current nesting depth through the
    // synchronous, single-threaded recursion (no reentrancy — drawing is one pass per frame); 0 == a
    // top-level component member, bumped by DrawNestedBody around each slot's body.
    static int nestDepth;

    // EF16: a child grid for a nested member's body. The EF-LAYOUT fix has two halves:
    //   (1) DrawNestedBody cancels the TreeNode's full per-level IndentSpacing so the grid does NOT march one
    //       big step right per level (the value box stops being shoved off-screen);
    //   (2) the grid uses a FIXED-width label column (NOT the proportional 0.38/0.62 split) computed from the
    //       layout model so the value column keeps a usable width at every depth, with only a SMALL fixed
    //       per-depth indent applied to the LABEL (in Row/RowWithTooltip), never the value column.
    // The anchor is recomputed from THIS grid's available width each time rather than threaded from a single
    // panel-level x: structurally each nested foldout renders INSIDE its parent's value cell (column 1), so
    // the grids do not share the panel content-left and a panel-global value-x cannot hold across value-cell
    // nesting. ValueColumnLeft clamps the label column to ≤62% of the current width, so the value box can
    // never vanish however deep the nesting goes — which is exactly the DoD ("one extra level doesn't push
    // values off-screen"). The top-level proportional BeginGrid (:2459) is untouched → depth-0 byte-identical.
    static bool BeginNestedGrid(string id) {
        float s = EditorTheme.UiScale;
        float valueLeft = Inspector.InspectorLayout.ValueColumnLeft(gui.ContentRegionAvail.X, s);

        if (!gui.BeginTable(id, 2, EditorTableFlags.SizingFixedFit | EditorTableFlags.PadOuterX))
            return false;
        // LabelColumnWidth subtracts the small per-depth label indent so the value field's left edge resolves
        // back to `valueLeft` even though Row/RowWithTooltip indent the label text by DepthIndentTotal.
        float labelW = Inspector.InspectorLayout.LabelColumnWidth(nestDepth, valueLeft, s);
        gui.TableSetupColumn("label", EditorColumnFlags.WidthFixed, labelW);
        gui.TableSetupColumn("value", EditorColumnFlags.WidthStretch, 1f);
        ResetRowZebra();
        return true;
    }

    // EF16: run `body` as a nested member foldout one depth deeper, with the TreeNode's full IndentSpacing
    // CANCELLED for the body (the model indents the label by a small fixed step inside the grid instead, so
    // the value column never marches right). Keeps the foldout header at its natural position; only the body
    // (the child grid) is pulled back to the panel content-left. Symmetric Unindent/Indent so a deep chain
    // never leaves the cursor mis-indented. `nestDepth` is bumped for the duration so the child grid + its
    // row labels resolve their column width / label indent at the correct depth.
    static void DrawNestedBody(Action body) {
        float indent = gui.IndentSpacing;
        gui.Unindent(indent);              // cancel the TreeNode's full per-level indent for the body grid
        nestDepth++;
        try { body(); }
        finally {
            nestDepth--;
            gui.Indent(indent);
        }
    }

    // Starts a new label/value row and leaves the cursor in the value column.
    // internal (RW1.1): the relocated RendererPreview body (Inspector/Preview/) calls this to draw its
    // submesh-material rows inside a BeginGrid table.
    // RW2 (Phase E): the label now uses EditorTheme.RowLabel (a legible label color, not the dead
    // TextDisabled grey) and the row gets a hover-accent affordance (faint fill + a left sliver) via
    // RowChrome. ALL member rows route through here (component members via ImGuiComponentGui.BeginRow ->
    // RowWithTooltip, plus every shim Row), so the whole inspector picks up the look in one place.
    internal static void Row(string label) {
        gui.TableNextRow();
        RowChrome();
        gui.TableSetColumnIndex(0);
        gui.AlignTextToFramePadding();
        DrawRowLabel(label, null);
        gui.TableSetColumnIndex(1);
        gui.SetNextItemWidth(-1);
    }

    // EF11: the ONE label-drawing primitive both Row and RowWithTooltip route through. Replaces the old
    // plain TextUnformatted + manual EF16 Indent with InspectorLayout.DrawLabelCell, which (1) applies the
    // small per-depth label indent ITSELF (so the EF16 manual Indent is gone — applying it here too would
    // DOUBLE-indent), (2) ellipsizes a label that overruns its column and shows the full text on hover, so
    // a long member name like "High Speed Steer Scale" is never silently truncated, and (3) shows the
    // [Tooltip] on label hover when one is supplied. `columnWidth` is the label cell's ACTUAL remaining width
    // (`GetContentRegionAvail().X` measured at the cell, before any indent) — it works for BOTH the top-level
    // proportional grid (column 0 ≈ 38%) and the fixed-width nested grid, with no panel-level x threaded down.
    // Depth 0 (top-level + the ComponentPreviews/AssetInspectors shim rows, which never nest) → 0px indent +
    // a label that fits → DrawLabelCell returns the label unchanged → visually equivalent to the old path.
    static void DrawRowLabel(string label, string tooltip) {
        float columnWidth = gui.ContentRegionAvail.X;
        Inspector.InspectorLayout.DrawLabelCell(label, nestDepth, columnWidth, EditorTheme.UiScale, tooltip);
    }

    // Like Row, but appends a "(?)" marker that shows the tooltip on hover (when one is supplied).
    static void RowWithTooltip(string label, string tooltip) {
        gui.TableNextRow();
        RowChrome();
        gui.TableSetColumnIndex(0);
        gui.AlignTextToFramePadding();
        // EF11: the label (with per-depth indent + ellipsis + full-text/[Tooltip] hover) is drawn by the shared
        // DrawLabelCell — including the Unity-style tooltip ON THE LABEL ITSELF (what made [Tooltip] feel
        // "broken" was having to find the tiny "(?)" marker). The "(?)" badge is kept as a redundant affordance.
        DrawRowLabel(label, tooltip);
        if (tooltip is not null) {
            gui.SameLine(0, 4);
            gui.PushColor(EditorStyleColor.Text, EditorTheme.RowCaption);
            gui.TextUnformatted("(?)");
            gui.PopColor();
            if (gui.IsItemHovered())
                gui.Tooltip(tooltip);
        }
        gui.TableSetColumnIndex(1);
        gui.SetNextItemWidth(-1);
    }

    // RW2 (Phase E, drawer-row affordance): on the CURRENT table row, if the mouse hovers it, paint a faint
    // accent row-bg + a left accent sliver so rows have rhythm + a hover affordance (the flat label|widget
    // rows had none). PERFORMANCE (plan §4): hover-gated — the fill is one TableSetBgColor + the sliver is
    // one AddRectFilled, ONLY when hovered; no per-row gradient/shadow, no allocation. Must be called right
    // after TableNextRow(), BEFORE TableSetColumnIndex(0), so the row's screen rect is the fresh row.
    // EF5e: per-grid zebra row counter — alternate member rows get a faint wash so the Details panel reads
    // as a structured grid (the UE5/Unity "Details" signature) instead of a flat wall of text. Reset at the
    // start of each member grid (BeginGrid) so striping is stable frame-to-frame.
    static int rowZebraIndex;
    internal static void ResetRowZebra() => rowZebraIndex = 0;

    static void RowChrome() {
        // The row's vertical band: cursor Y at row start .. + a frame height (the row's content height).
        SysVec2 rowStart = gui.CursorScreenPos;
        float rowH = gui.FrameHeightWithSpacing;
        // Full panel content width (the table spans it); use the window inner rect so the band covers both
        // columns regardless of the table's internal split.
        float x0 = gui.WindowPos.X;
        float x1 = x0 + gui.WindowSize.X;

        // Per-row zebra striping was removed — it read as noisy. Component-level background banding
        // (DrawComponent paints each component's whole body in an alternating tint) gives the grouping
        // rhythm instead. The counter is kept (harmless) so callers/Reset stay valid.
        rowZebraIndex++;

        bool hovered = gui.IsWindowHovered() &&
                       gui.IsMouseHoveringRect(new SysVec2(x0, rowStart.Y),
                                                 new SysVec2(x1, rowStart.Y + rowH), clip: false);
        if (!hovered)
            return;

        SysVec4 accent = EditorPrefs.Current.Accent;
        gui.TableSetRowBgColor(
            gui.ColorU32(EditorTheme.RowHoverFill(accent)));
        var draw = gui.WindowDrawList;
        float w = EditorTheme.RowAccentBarWidth;
        draw.AddRectFilled(new SysVec2(x0, rowStart.Y),
                           new SysVec2(x0 + w, rowStart.Y + rowH),
                           gui.ColorU32(EditorTheme.RowHoverBar(accent)));
    }

    void SysVec3Row(string label, Vector3 value, Action<Vector3> apply, float speed) =>
        SysVec3Row(label, value, apply, speed, allowUniformLock: false);

    // Per-member "lock proportions" (Unity's chain link on Scale): the toggle state is keyed by the
    // member label so each lockable Vector3 row remembers its own setting.
    readonly Dictionary<string, bool> uniformLocks = new();

    void SysVec3Row(string label, Vector3 value, Action<Vector3> apply, float speed, bool allowUniformLock) {
        Row(label);

        // The chain-link toggle lives in the LABEL cell (column 0), right-aligned next to the fields —
        // putting it in front of the X chip shifted Scale's fields right and broke alignment with
        // Position/Rotation. Row() already moved us to column 1; hop back to 0, draw the lock, return.
        bool locked = allowUniformLock && uniformLocks.GetValueOrDefault(label);
        if (allowUniformLock) {
            gui.TableSetColumnIndex(0);
            string icon = locked ? EditorIcons.Lock : EditorIcons.LockOpen;
            float btn = gui.FrameHeight;
            gui.SameLine();
            gui.CursorPosX = (gui.CursorPosX + gui.ContentRegionAvail.X - btn);
            if (EditorIcons.GhostButtonSmall($"ulock_{label}", icon,
                    locked ? "Proportions locked - editing one axis scales the others"
                           : "Lock proportions (uniform scaling)")) {
                uniformLocks[label] = !locked;
                locked = !locked;
            }
            gui.TableSetColumnIndex(1);
            gui.SetNextItemWidth(-1);
        }

        var sv = new SysVec3(value.X, value.Y, value.Z);
        var before = sv;
        if (AxisVec3(label, label, ref sv, speed)) {
            if (locked)
                sv = ApplyUniformLock(before, sv);
            apply(new Vector3(sv.X, sv.Y, sv.Z));
            state.MarkViewportDirty();
        }
    }

    // Given the value before/after an edit where exactly one axis was dragged, scale the OTHER two by
    // the same ratio the edited axis changed by (so proportions hold). Falls back to an additive delta
    // when the edited axis was zero (no ratio is defined). Picks the axis with the largest change.
    static SysVec3 ApplyUniformLock(SysVec3 before, SysVec3 after) {
        float dx = MathF.Abs(after.X - before.X), dy = MathF.Abs(after.Y - before.Y), dz = MathF.Abs(after.Z - before.Z);
        int axis = dx >= dy && dx >= dz ? 0 : dy >= dz ? 1 : 2;   // the dragged axis
        float oldA = axis == 0 ? before.X : axis == 1 ? before.Y : before.Z;
        float newA = axis == 0 ? after.X : axis == 1 ? after.Y : after.Z;
        if (MathF.Abs(newA - oldA) < 1e-9f) return after; // nothing actually changed

        if (MathF.Abs(oldA) > 1e-6f) {
            float ratio = newA / oldA;                      // proportional: multiply the others
            return new SysVec3(before.X * ratio, before.Y * ratio, before.Z * ratio);
        }
        float delta = newA - oldA;                          // old axis was 0: shift the others equally
        return new SysVec3(before.X + delta, before.Y + delta, before.Z + delta);
    }

    // True if a component type is a GAME script (compiled into GameScripts.dll), not an engine type.
    static bool IsGameScript(Type type) =>
        type.Assembly.GetName().Name == BallisticEngine.AssetPipeline.GameScripts.AssemblyName;

    // Finds the component's backing .cs (Unity's rule: file name == class name) and opens it in the OS
    // default C# editor. Also opens the generated Scripts.csproj first so the IDE has project context.
    static void OpenComponentScript(Type type) {
        // Locate <TypeName>.cs anywhere under Assets via the asset database.
        string target = null;
        foreach (var (path, _) in AssetDatabase.EnumerateAssets()) {
            if (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Path.GetFileNameWithoutExtension(path), type.Name, StringComparison.Ordinal)) {
                target = path;
                break;
            }
        }
        if (target is null) {
            Debugging.LogWarning($"Edit Script: no '{type.Name}.cs' found under Assets.");
            return;
        }
        try {
            // Open the project file first (IDE context), then the source file.
            var csproj = BallisticEngine.AssetPipeline.GameScripts.EnsureProjectFile(AssetDatabase.Project);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(csproj) { UseShellExecute = true });
            var abs = AssetDatabase.Project.ResolveAbsolute(target);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(abs) { UseShellExecute = true });
        }
        catch (Exception ex) {
            Debugging.LogWarning($"Edit Script: {ex.Message}");
        }
    }

    // True if the entity already has a behaviour of (exactly) this type — for batch Add Component,
    // so adding to a multi-selection skips entities that already have it (Unity-style).
    static bool HasComponentOfType(Entity entity, Type type) {
        foreach (Behaviour b in entity.Behaviours)
            if (b.GetType() == type)
                return true;
        return false;
    }

    // The same-type component on every OTHER selected entity (for batch enable/disable/remove). Empty
    // for a single selection. First matching component per entity; skips the active behaviour's entity.
    List<Behaviour> MatchingComponents(Behaviour active) {
        var list = new List<Behaviour>();
        if (state.SelectedEntities.Count <= 1)
            return list;
        Type type = active.GetType();
        Entity activeEntity = active.Entity;
        foreach (Entity e in state.SelectedEntities) {
            if (e is null || e.IsDestroyed || ReferenceEquals(e, activeEntity))
                continue;
            foreach (Behaviour b in e.Behaviours)
                if (b.GetType() == type) { list.Add(b); break; }
        }
        return list;
    }

    // "RotationEuler" -> "Rotation Euler", "lightIntensity" -> "Light Intensity"
    static string Prettify(string name) {
        if (string.IsNullOrEmpty(name))
            return name;

        var result = new System.Text.StringBuilder(name.Length + 4);
        result.Append(char.ToUpperInvariant(name[0]));
        for (var i = 1; i < name.Length; i++) {
            if (char.IsUpper(name[i]) && !char.IsUpper(name[i - 1]))
                result.Append(' ');
            result.Append(name[i]);
        }
        return result.ToString();
    }
}
