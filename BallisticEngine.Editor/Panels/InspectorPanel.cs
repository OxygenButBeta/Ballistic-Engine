using System.Reflection;
using BallisticEngine.AssetPipeline;
using BallisticEngine.Editor.Inspector;
using BallisticEngine.Editor.Inspector.AssetInspectors;
using BallisticEngine.Serialization;
using SysVec2 = System.Numerics.Vector2;
using SysVec3 = System.Numerics.Vector3;
using SysVec4 = System.Numerics.Vector4;

namespace BallisticEngine.Editor;

internal sealed class InspectorPanel : EditorWindow, IComponentInspectorHost {
    static IEditorGui gui => EditorGui.Shared;

    readonly EditorState state;

    readonly DrawerRegistry memberRegistry = DrawerRegistry.CreatePrimitive();
    readonly DrawerStack componentStack;
    readonly ImGuiComponentGui componentGui;

    MemberInfo pickerMember;
    object pickerTarget;
    Type pickerType;
    string pickerSearch = "";
    bool openPicker;

    Inspector.IProperty pickerProperty;

    Inspector.IProperty sceneRefPickerProperty;
    string sceneRefSearch = "";
    bool openSceneRefPicker;

    string addComponentSearch = "";

    readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, StrBox> memberSearch = new();
    sealed class StrBox { public string Value = ""; }

    string componentListSearch = "";

    bool locked;
    Entity lockedEntity;

    static int instanceCounter;
    readonly int instanceId = instanceCounter++;

    protected override void OnGui(IEditorGui gui) => DrawContents();

    public InspectorPanel(EditorState state) {
        DockKey = EditorLayout.Inspector;
        Title = "Details";
        Icon = EditorIcons.Wrench;
        Singleton = false;

        this.state = state;
        componentGui = new ImGuiComponentGui(this);
        componentStack = DrawerStack.CreateComponent(memberRegistry);
        memberRegistry.Register(new NestedDrawer(this));
        memberRegistry.Register(new BEventDrawer());
        memberRegistry.Register(new AnimationCurveDrawer(this));
        memberRegistry.Register(new ColorGradientDrawer(this));
        memberRegistry.Register(new AssetSlotDrawer(this));
        memberRegistry.Register(new SceneObjectRefDrawer(this));
        memberRegistry.Register(new CollectionDrawer(this));
        memberRegistry.Register(new DictionaryDrawer(this));
        memberRegistry.Register(new PolymorphicDrawer(this));
        ComponentEditorWindow.Configure(DrawMemberList);
    }

    void IComponentInspectorHost.RowWithTooltip(string label, string tooltip) => RowWithTooltip(label, tooltip);
    void IComponentInspectorHost.DrawMixedMarker(MemberInfo member, object target, object value) => DrawMixedMarker(member, target, value);
    bool IComponentInspectorHost.AxisVec3(string id, string label, ref SysVec3 v, float speed) => AxisVec3(id, label, ref v, speed);
    bool IComponentInspectorHost.TrackUndo(string label, bool changed) => InspectorUndo.Track(label, changed);
    void IComponentInspectorHost.MarkViewportDirty() => state.MarkViewportDirty();

    internal void MarkViewportDirty() => state.MarkViewportDirty();

    internal void Select(Entity entity) => state.Select(entity);

    void IComponentInspectorHost.DrawAssetSlot(Inspector.IProperty property) {
        if (property is Inspector.MemberProperty mp)
            DrawAssetSlot(mp.Member, mp.Owner, mp.Get() as BObject, mp.ValueType);
        else
            DrawAssetSlotForProperty(property);
    }

    void IComponentInspectorHost.DrawSceneObjectSlot(Inspector.IProperty property) => DrawSceneObjectSlot(property);

    void IComponentInspectorHost.DrawCollectionSlot(Inspector.IProperty property) => DrawCollectionSlot(property);

    void IComponentInspectorHost.DrawDictionarySlot(Inspector.IProperty property) => DrawDictionarySlot(property);

    void IComponentInspectorHost.DrawPolymorphicSlot(Inspector.IProperty property, Type declaredType) => DrawPolymorphicSlot(property, declaredType);

    void IComponentInspectorHost.DrawNestedSlot(Inspector.IProperty property, Type declaredType) => DrawNestedSlot(property, declaredType);

    public void DrawContents() {
        gui.PushId(instanceId);
        gui.PushItemSpacing(new SysVec2(8, 4));
        gui.PushFramePadding(new SysVec2(8, 4));

        DrawLockBar();

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
            if (state.SelectedEntities.Count > 1) {
                gui.AlignTextToFramePadding();
                gui.TextDisabled($"{EditorIcons.Package}");
                gui.SameLine(0, 6);
                SysVec4 accent = EditorPrefs.Current.Accent;
                EditorDecoration.DrawBadge($"{state.SelectedEntities.Count} entities", new SysVec4(accent.X, accent.Y, accent.Z, 0.30f));
                gui.TextDisabled("Edits apply to ALL selected (matching components).");
                EditorDecoration.DrawDivider();
            }

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

    static void DrawEmptyState() {
        SysVec2 avail = gui.ContentRegionAvail;
        SysVec2 origin = gui.CursorScreenPos;
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
        float w = size;
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

    string addFeatureSearch = "";

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

            bool active = feature.Active;
            if (gui.Checkbox("##featactive", ref active)) {
                EditorUndo.Push(active ? "Enable Render Feature" : "Disable Render Feature");
                feature.Active = active;
                state.MarkViewportDirty();
            }
            gui.SameLine();

            string display = ComponentRegistry.RenderFeatureMenu
                .FirstOrDefault(e => e.Type == ft).DisplayName ?? Prettify(ft.Name);
            gui.PushColor(EditorStyleColor.Text,
                gui.StyleColor(active ? EditorStyleColor.Text : EditorStyleColor.TextDisabled));
            bool open = gui.CollapsingHeader($"{display}###feathdr{i}", defaultOpen: true);
            gui.PopColor();

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

            if (open) {
                gui.Indent();
                DrawMemberList(ft, feature);
                gui.Unindent();
            }

            gui.PopId();
        }

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

    void DrawEntityInspector(Entity entity) {
        Behaviour[] behaviours = entity.Behaviours.ToArray();
        DrawEntityHeaderCard(entity, behaviours.Length);

        PrefabOverrides.Refresh(entity);
        if (entity.IsPrefabInstance)
            DrawPrefabInstanceBar(entity);

        DrawTagLayerRow(entity);

        gui.Spacing();

        DrawTransform(entity.transform);

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
        var componentOrdinal = 0;
        foreach (Behaviour behaviour in behaviours) {
            Type bt = behaviour.GetType();
            int idx = typeIndex.TryGetValue(bt, out int i) ? i : 0;
            typeIndex[bt] = idx + 1;
            if (!ComponentMatch(behaviour)) continue;
            if (componentOrdinal > 0)
                gui.Dummy(new SysVec2(0, 6 * EditorTheme.UiScale));
            DrawComponent(entity, behaviour, idx, componentOrdinal++);
        }

        gui.Spacing();
        gui.Spacing();
        DrawAddComponent(entity);
        gui.Spacing();
    }

    static bool ComponentHasOverride(Behaviour behaviour, int typeIndex) =>
        PrefabOverrides.ComponentHasOverride(ComponentRegistry.NameOf(behaviour), typeIndex);

    void DrawPrefabInstanceBar(Entity entity) {
        string path = AssetDatabase.GuidToAssetPath(entity.PrefabSource);
        string name = path is null ? "(missing prefab)" : Path.GetFileNameWithoutExtension(path);

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

    unsafe void DrawEntityHeaderCard(Entity entity, int componentCount) {
        var draw = gui.WindowDrawList;
        SysVec2 avail = gui.ContentRegionAvail;
        SysVec2 cardMin = gui.CursorScreenPos;

        float pad = 10f;
        float frameH = gui.FrameHeight;
        float headerFrameH = gui.FontSizeOf(EditorFont.Header) + gui.FramePadding.Y * 2;
        float row1H = MathF.Max(frameH, headerFrameH);
        float cardH = pad + row1H + 4 + gui.TextLineHeight + pad;
        SysVec2 cardMax = cardMin + new SysVec2(avail.X, cardH);

        EditorDecoration.DrawCard(cardMin, cardMax, 6f);

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

        gui.SetCursorScreenPos(new SysVec2(contentX, cardMin.Y + pad));
        bool active = entity.IsActive;
        if (gui.Checkbox("##active", ref active)) { }

        if (gui.IsItemActivated()) EditorCommands.EditEntity(entity, "Toggle Active", () => { });
        if (active != entity.IsActive) { entity.SetActive(active); state.MarkViewportDirty(); }
        if (gui.IsItemHovered())
            gui.Tooltip("Active");

        gui.SameLine();
        gui.SetNextItemWidth(cardMax.X - pad - gui.CursorScreenPos.X);
        gui.PushColor(EditorStyleColor.FrameBg, new SysVec4(0, 0, 0, 0.30f));
        gui.PushFont(EditorFont.Header);
        var name = entity.Name ?? "";
        var renamed = gui.InputText("##name", ref name, 128);
        gui.PopFont();
        gui.PopColor();
        if (gui.IsItemActivated()) EditorCommands.EditEntity(entity, "Rename", () => { });
        if (renamed) entity.Name = name;

        gui.SetCursorScreenPos(new SysVec2(contentX, cardMin.Y + pad + row1H + 4));
        gui.PushFont(EditorFont.Caption);
        gui.PushColor(EditorStyleColor.Text, EditorTheme.RowCaption);
        gui.TextUnformatted(componentCount == 1 ? "1 component" : $"{componentCount} components");
        gui.PopColor();
        gui.PopFont();

        gui.SetCursorScreenPos(cardMin);
        gui.Dummy(new SysVec2(avail.X, cardH));
        AcceptScriptDrop(entity);
    }

    void AcceptScriptDrop(Entity entity) {
        if (!gui.BeginDragDropTarget())
            return;
        string text = gui.AcceptDragDropPayloadString(AssetBrowserPanel.DragType);
        if (text is not null) {
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

    void DrawTagLayerRow(Entity entity) {
        gui.Spacing();
        float half = (gui.ContentRegionAvail.X - gui.ItemSpacing.X) * 0.5f;

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

            gui.Separator();
            if (gui.Selectable($"{EditorIcons.Add} Add Tag..."))
                EditorWindows.Open(EditorMenus.WindowKeys.TagsLayers);
            gui.EndCombo();
        }
        if (gui.IsItemHovered()) gui.Tooltip("Tag");

        gui.SameLine();

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

            gui.Separator();
            if (gui.Selectable($"{EditorIcons.Add} Add Layer..."))
                EditorWindows.Open(EditorMenus.WindowKeys.TagsLayers);
            gui.EndCombo();
        }
        if (gui.IsItemHovered()) gui.Tooltip("Layer");
    }

    void DrawTransform(Transform transform) {
        bool open = PlainHeader("Transform");

        bool posOv = PrefabOverrides.IsOverridden(PrefabOverrides.TransformPositionKey);
        bool rotOv = PrefabOverrides.IsOverridden(PrefabOverrides.TransformRotationKey);
        bool sclOv = PrefabOverrides.IsOverridden(PrefabOverrides.TransformScaleKey);
        if (posOv || rotOv || sclOv) {
            SysVec2 hp = gui.ItemRectMax;
            gui.WindowDrawList.AddCircleFilled(
                new SysVec2(hp.X - 12, (gui.ItemRectMin.Y + hp.Y) * 0.5f), 3.5f,
                gui.ColorU32(EditorTheme.PrefabBlue));
        }

        var others = MultiTransforms(transform);

        if (gui.BeginPopupContextItem("##transformctx")) {
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

    List<Transform> MultiTransforms(Transform active) {
        var list = new List<Transform>();
        if (state.SelectedEntities.Count <= 1)
            return list;
        foreach (Entity e in state.SelectedEntities)
            if (e?.transform is { } t && !ReferenceEquals(t, active) && !e.IsDestroyed)
                list.Add(t);
        return list;
    }

    static unsafe bool PlainHeader(string label) {
        gui.Spacing();
        gui.PushFramePadding(new SysVec2(10, 7));
        float labelX = gui.TreeNodeToLabelSpacing;
        bool open = gui.CollapsingHeaderFramed($"###hdr_{label}");
        gui.PopStyleVar();

        SysVec2 min = gui.ItemRectMin;
        SysVec2 max = gui.ItemRectMax;
        var draw = gui.WindowDrawList;
        EditorDecoration.DrawAccentStripe(min, max.Y - min.Y, gui.StyleColor(EditorStyleColor.CheckMark));
        draw.AddText(EditorFont.Bold, gui.FontSize,
            new SysVec2(min.X + labelX, min.Y + (max.Y - min.Y - gui.FontSize) * 0.5f),
            gui.ColorU32(gui.StyleColor(EditorStyleColor.Text)), label);
        return open;
    }

    void DrawComponent(Entity entity, Behaviour behaviour, int typeIndex = 0, int componentOrdinal = 0) {
        Type type = behaviour.GetType();
        gui.PushId(behaviour.InstanceId.GetHashCode());

        var draw = gui.WindowDrawList;
        draw.ChannelsSplit(2);
        draw.ChannelsSetCurrent(1);
        SysVec2 bandStart = gui.CursorScreenPos;

        bool enabled = behaviour.IsEnabled;
        bool open = ComponentHeader(Prettify(type.Name), type, ref enabled, out bool menuRequested);

        if (entity.IsPrefabInstance && ComponentHasOverride(behaviour, typeIndex)) {
            SysVec2 mx = gui.ItemRectMax;
            gui.WindowDrawList.AddCircleFilled(
                new SysVec2(mx.X - 30, (gui.ItemRectMin.Y + mx.Y) * 0.5f), 3.5f,
                gui.ColorU32(EditorTheme.PrefabBlue));
        }
        if (enabled != behaviour.IsEnabled) {
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
            EditorCommands.Structural("Remove Component", () => {
                foreach (Behaviour sibling in MatchingComponents(behaviour))
                    sibling.Entity.RemoveComponent(sibling);
                entity.RemoveComponent(behaviour);
                state.MarkViewportDirty();
            });
            draw.ChannelsMerge();
            gui.PopId();
            return;
        }

        if (open) {
            DrawMemberList(type, behaviour);

            var previewCtx = new Inspector.Preview.ComponentPreviewContext(this, entity, behaviour);
            foreach (var preview in BallisticEngine.Editor.ComponentPreviewRegistry.PreviewsFor(type))
                preview.Draw(in previewCtx);

            gui.Spacing();
        }

        float bandEndY = gui.CursorScreenPos.Y;
        float wx0 = gui.WindowPos.X;
        float wx1 = wx0 + gui.WindowSize.X;
        SysVec4 band = (componentOrdinal & 1) == 0
            ? new SysVec4(1f, 1f, 1f, 0.05f)
            : new SysVec4(0f, 0f, 0f, 0.06f);
        draw.ChannelsSetCurrent(0);
        draw.AddRectFilled(new SysVec2(wx0, bandStart.Y - 2), new SysVec2(wx1, bandEndY + 4),
                           gui.ColorU32(band), 6f);
        draw.ChannelsMerge();

        gui.PopId();
    }

    static Type clipboardType;
    static readonly Dictionary<string, object> clipboardMembers = new();

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

    void ResetComponent(Behaviour behaviour) {
        Type type = behaviour.GetType();
        Behaviour fresh;
        try { fresh = (Behaviour)Activator.CreateInstance(type); }
        catch { return; }

        EditorCommands.EditEntity(behaviour.Entity, $"Reset {Prettify(type.Name)}", () => {
            foreach (MemberInfo member in ComponentReflection.InspectorMembers(type)) {
                try { ComponentReflection.SetValue(member, behaviour, ComponentReflection.GetValue(member, fresh)); }
                catch {
                }
            }
            state.MarkViewportDirty();
        });
    }

    static void CopyComponent(Behaviour behaviour) {
        clipboardType = behaviour.GetType();
        clipboardMembers.Clear();
        foreach (MemberInfo member in ComponentReflection.InspectorMembers(clipboardType))
            clipboardMembers[member.Name] = ComponentReflection.GetValue(member, behaviour);
    }

    static bool CanPasteInto(Type targetType) =>
        clipboardType is not null && targetType.IsAssignableFrom(clipboardType);

    void PasteComponent(Behaviour behaviour) {
        if (!CanPasteInto(behaviour.GetType()))
            return;
        EditorCommands.EditEntity(behaviour.Entity, $"Paste {Prettify(behaviour.GetType().Name)}", () => {
            foreach (MemberInfo member in ComponentReflection.InspectorMembers(behaviour.GetType())) {
                if (clipboardMembers.TryGetValue(member.Name, out object value)) {
                    try { ComponentReflection.SetValue(member, behaviour, value); }
                    catch {
                    }
                }
            }
            state.MarkViewportDirty();
        });
    }

    internal static IAudioVoice audioPreviewVoice;
    internal static float audioPreviewTime;

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

        EditorDecoration.DrawAccentStripe(min, headerH, tint);

        SysVec2 cursor = gui.CursorScreenPos;

        gui.PushFramePadding(new SysVec2(2, 2) * EditorTheme.UiScale);
        float frameH = gui.FrameHeight;
        float chkX = min.X + arrowW;
        gui.SetCursorScreenPos(new SysVec2(chkX, min.Y + (headerH - frameH) * 0.5f));
        gui.Checkbox($"##en_{label}", ref enabled);
        gui.PopStyleVar();

        float headerFontSize = gui.FontSizeOf(EditorFont.Header);
        float textY = min.Y + (headerH - headerFontSize) * 0.5f;
        float iconX = chkX + frameH + 6;
        var dimmed = enabled ? 1f : 0.45f;
        draw.AddText(EditorFont.Header, headerFontSize, new SysVec2(iconX, textY),
            gui.ColorU32(new SysVec4(tint.X, tint.Y, tint.Z, dimmed)), icon);
        draw.AddText(EditorFont.Header, headerFontSize,
            new SysVec2(iconX + headerFontSize + 8, textY),
            gui.ColorU32(gui.StyleColor(enabled ? EditorStyleColor.Text : EditorStyleColor.TextDisabled)), label);

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

    internal void DrawMemberList(Type type, object target) {
        nestDepth = 0;

        var visibleMembers = new List<(MemberInfo Info, MemberAttributes Attrs)>();
        foreach (TypePlan.Member planned in TypePlan.For(type).Members) {
            MemberAttributes a = MemberAttributes.For(planned.Info);
            if (Conditions.Visible(a.Conditionals, target))
                visibleMembers.Add((planned.Info, a));
        }

        string query = "";
        if (visibleMembers.Count > Inspector.InspectorLayout.MemberSearchThreshold) {
            StrBox box = memberSearch.GetValue(target, static _ => new StrBox());
            if (EditorWidgets.SearchField($"##membersearch_{type.Name}", "Search properties...", ref box.Value))
                state.MarkViewportDirty();
            query = box.Value;
            gui.Spacing();
        }

        HashSet<MemberInfo> matches = null;
        if (query.Length > 0) {
            matches = new HashSet<MemberInfo>();
            foreach ((MemberInfo info, MemberAttributes a) in visibleMembers)
                if (MemberLabel(info, a).Contains(query, StringComparison.OrdinalIgnoreCase))
                    matches.Add(info);
        }

        bool MemberVisible(MemberInfo m) => matches is null || matches.Contains(m);

        HashSet<string> groupsWithMatch = null;
        if (matches is not null) {
            groupsWithMatch = new HashSet<string>();
            foreach ((MemberInfo info, MemberAttributes a) in visibleMembers)
                if (a.Foldout?.Name is { } g && matches.Contains(info))
                    groupsWithMatch.Add(g);
        }

        bool GroupVisible(string g) => g is null || groupsWithMatch is null || groupsWithMatch.Contains(g);

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
        var gridIndex = 0;
        string currentGroup = null;
        var groupOpen = true;

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
            if (groupOpen) gui.TreePop();
            currentGroup = null;
            groupOpen = true;
        }

        foreach ((MemberInfo member, MemberAttributes attrs) in visibleMembers) {
            string group = attrs.Foldout?.Name;

            if (!GroupVisible(group))
                continue;

            if (group != currentGroup || attrs.Header is not null)
                EndGroup();

            if (attrs.Space is not null && (MemberVisible(member) || HeaderVisible(member))) { CloseGrid(); gui.Dummy(new SysVec2(0, attrs.Space.Height)); }
            if (attrs.Header is not null && HeaderVisible(member)) { CloseGrid(); EditorDecoration.DrawSectionHeader(attrs.Header.Text); }

            if (group is not null && group != currentGroup) {
                CloseGrid();
                var flags = EditorTreeFlags.Framed | EditorTreeFlags.SpanAvailWidth |
                    (attrs.Foldout.DefaultOpen ? EditorTreeFlags.DefaultOpen : EditorTreeFlags.None);
                groupOpen = gui.TreeNodeEx($"{group}###fold_{type.Name}_{group}", flags);
                currentGroup = group;
            }

            if (currentGroup is not null && !groupOpen)
                continue;

            if (!MemberVisible(member))
                continue;

            EnsureGrid();
            DrawMember(member, target, attrs);
        }

        EndGroup();
        CloseGrid();

        foreach (MethodInfo method in ComponentReflection.InspectorButtons(type)) {
            var label = method.GetCustomAttribute<ButtonAttribute>()?.Label ?? method.Name;
            if (gui.Button($"{label}###btn_{type.Name}_{method.Name}", new SysVec2(-1, 0)))
                method.Invoke(target, null);
        }

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

    static string MemberLabel(MemberInfo member, MemberAttributes attrs) =>
        attrs.LabelText?.Text ?? Inspector.InspectorReflection.Prettify(member.Name);

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
                    catch {
                    }
                    break;
                }
            }
        }
    }

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
        componentStack.Draw(new MemberProperty(member, target,
            v => { ApplyMember(member, target, v); state.MarkViewportDirty(); }), componentGui);
    }

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
            state.RequestRevealAsset(path);
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
        pickerProperty = null;
        openPicker = true;
    }

    void AssignAsset(MemberInfo member, object target, Type assetType, Guid guid) {
        EditorCommands.Structural($"Assign {Prettify(member.Name)}", () => {
            MethodInfo load = typeof(AssetDatabase).GetMethod(nameof(AssetDatabase.Load), [typeof(Guid)])!
                .MakeGenericMethod(assetType);
            object loaded = load.Invoke(null, [guid]);
            if (loaded is not null)
                ApplyMember(member, target, loaded);
            state.MarkViewportDirty();
        });
    }

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

    internal void DrawSubMeshMaterialSlot(Renderer renderer, int submeshIndex, Material baked) {
        var slot = new Inspector.CollectionElementProperty(
            $"Submesh {submeshIndex} Material", typeof(Material),
            () => renderer.GetMaterialOverride(submeshIndex),
            v => EditorCommands.Structural($"Assign Submesh {submeshIndex} Material", () => {
                renderer.SetMaterialOverride(submeshIndex, v as Material);
                state.MarkViewportDirty();
            }));

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
        EditorCommands.Structural($"Assign {p.Label}", () => {
            MethodInfo load = typeof(AssetDatabase).GetMethod(nameof(AssetDatabase.Load), [typeof(Guid)])!
                .MakeGenericMethod(assetType);
            object loaded = load.Invoke(null, [guid]);
            if (loaded is not null)
                p.Set(loaded);
            state.MarkViewportDirty();
        });
    }

    void DrawAssetPickerPopup() {
        float u = gui.FontSize;
        gui.SetNextWindowSizeAppearing(new SysVec2(u * 28f, u * 30f));
        if (!gui.BeginPopup("##assetpicker"))
            return;

        gui.PushItemSpacing(new SysVec2(8, 6));

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

        gui.PushColor(EditorStyleColor.Text, gui.StyleColor(EditorStyleColor.TextDisabled));
        if (gui.Selectable($"  (None)", false, new SysVec2(0, gui.FrameHeight))) {
            if (pickerProperty is not null) {
                EditorCommands.Structural($"Clear {pickerProperty.Label}", () => pickerProperty.Set(null));
            }
            else {
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
                    AssignAssetToProperty(pickerProperty, pickerType, guid);
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

    void DrawSceneObjectSlot(Inspector.IProperty p) {
        bool isComponentRef = p.ValueType == typeof(ComponentRef);
        object boxed = p.Get();
        Guid instanceId = boxed switch {
            EntityRef e => e.InstanceId,
            ComponentRef c => c.InstanceId,
            _ => Guid.Empty,
        };

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
            textCol = EditorTheme.Error;
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

    void AssignSceneRef(Inspector.IProperty p, object refValue) {
        EditorCommands.Structural($"Assign {p.Label}", () => {
            p.Set(refValue);
            state.MarkViewportDirty();
        });
    }

    void DrawCollectionSlot(Inspector.IProperty p) {
        Type collType = p.ValueType;
        bool isArray = collType.IsArray;
        Type elemType = isArray ? collType.GetElementType() : collType.GetGenericArguments()[0];
        object boxed = p.Get();
        System.Collections.IList list = boxed as System.Collections.IList;

        int count = list?.Count ?? 0;

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
            return;
        }
        if (clearClicked) {
            CollectionClear(p, collType, elemType, isArray);
            return;
        }

        if (count == 0)
            return;

        int removeIndex = -1, insertIndex = -1, moveFrom = -1, moveTo = -1;
        float btnW = gui.FrameHeight;
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
                gui.TextDisabled($"({elemType.Name})");
            }

            gui.SameLine(0, 6);
            gui.BeginDisabled(captured == 0);
            if (gui.Button($"{EditorIcons.ChevronUp}##up", new SysVec2(btnW, 0))) { moveFrom = captured; moveTo = captured - 1; }
            gui.EndDisabled();
            if (gui.IsItemHovered() && captured > 0) gui.Tooltip("Move up");

            gui.SameLine(0, 4);
            gui.BeginDisabled(captured == count - 1);
            if (gui.Button($"{EditorIcons.ChevronDown}##down", new SysVec2(btnW, 0))) { moveFrom = captured; moveTo = captured + 1; }
            gui.EndDisabled();
            if (gui.IsItemHovered() && captured < count - 1) gui.Tooltip("Move down");

            gui.SameLine(0, 4);
            if (gui.Button($"{EditorIcons.Add}##ins", new SysVec2(btnW, 0))) insertIndex = captured;
            if (gui.IsItemHovered()) gui.Tooltip("Insert above");

            gui.SameLine(0, 4);
            if (gui.Button($"{EditorIcons.Delete}##rm", new SysVec2(btnW, 0))) removeIndex = captured;
            if (gui.IsItemHovered()) gui.Tooltip("Remove");

            gui.PopId();
        }

        if (moveFrom >= 0 && moveTo >= 0)
            CollectionMove(p, collType, elemType, list, isArray, moveFrom, moveTo);
        else if (insertIndex >= 0)
            CollectionInsertAt(p, collType, elemType, list, isArray, insertIndex);
        else if (removeIndex >= 0)
            CollectionRemoveAt(p, collType, elemType, list, isArray, removeIndex);
    }

    void CollectionAdd(Inspector.IProperty p, Type collType, Type elemType,
        System.Collections.IList list, bool isArray) {
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

    void CollectionRemoveAt(Inspector.IProperty p, Type collType, Type elemType,
        System.Collections.IList list, bool isArray, int index) {
        if (list is null || index < 0 || index >= list.Count) return;
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

    void CollectionClear(Inspector.IProperty p, Type collType, Type elemType, bool isArray) {
        EditorCommands.Structural($"Clear {p.Label}", () => {
            if (isArray)
                p.Set(Array.CreateInstance(elemType, 0));
            else
                p.Set((System.Collections.IList)Activator.CreateInstance(collType));
            state.MarkViewportDirty();
        });
    }

    void CollectionSetElement(Inspector.IProperty p, System.Collections.IList list, int index, object value) {
        if (list is null || index < 0 || index >= list.Count) return;
        list[index] = value;
        p.Set(list);
    }

    static object DefaultElement(Type elemType) {
        if (elemType == typeof(string)) return "";
        if (elemType.IsValueType) return Activator.CreateInstance(elemType);
        return null;
    }

    void DrawDictionarySlot(Inspector.IProperty p) {
        Type dictType = p.ValueType;
        Type[] args = dictType.GetGenericArguments();
        Type keyType = args[0];
        Type valueType = args[1];
        object boxed = p.Get();
        System.Collections.IDictionary dict = boxed as System.Collections.IDictionary;

        int count = dict?.Count ?? 0;

        gui.AlignTextToFramePadding();
        gui.TextDisabled($"{count} {(count == 1 ? "entry" : "entries")}");
        gui.SameLine();
        float addW = gui.FrameHeight + 24;
        gui.CursorPosX = (gui.CursorPosX + Math.Max(0, gui.ContentRegionAvail.X - addW));
        if (gui.Button($"{EditorIcons.Add} Add##adddict_{p.Name}", new SysVec2(addW, 0))) {
            DictionaryAdd(p, dictType, keyType, valueType, dict);
            return;
        }

        if (count == 0)
            return;

        var keys = new System.Collections.Generic.List<object>(count);
        foreach (object k in dict.Keys) keys.Add(k);

        object removeKey = null;
        bool hasRemove = false;
        for (int i = 0; i < keys.Count; i++) {
            gui.PushId(i);
            object key = keys[i];
            float removeW = gui.FrameHeight;
            float avail = gui.ContentRegionAvail.X;
            float keyW = Math.Max(40f, avail * 0.4f);
            float valW = Math.Max(40f, avail - keyW - removeW - 12);

            gui.AlignTextToFramePadding();
            gui.SetNextItemWidth(keyW);
            gui.Text(key?.ToString() ?? "(null)");
            if (gui.IsItemHovered())
                gui.Tooltip("Dictionary key (read-only)");
            gui.SameLine(0, 6);

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
                gui.TextDisabled($"({valueType.Name})");
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

    void DictionaryAdd(Inspector.IProperty p, Type dictType, Type keyType, Type valueType,
        System.Collections.IDictionary dict) {
        System.Collections.IDictionary target = dict ?? (System.Collections.IDictionary)Activator.CreateInstance(dictType);
        object key = UniqueDictKey(target, keyType);
        if (key is null) return;
        EditorCommands.Structural($"Add to {p.Label}", () => {
            target[key] = DefaultElement(valueType);
            p.Set(target);
            state.MarkViewportDirty();
        });
    }

    void DictionaryRemove(Inspector.IProperty p, System.Collections.IDictionary dict, object key) {
        if (dict is null || key is null || !dict.Contains(key)) return;
        EditorCommands.Structural($"Remove from {p.Label}", () => {
            dict.Remove(key);
            p.Set(dict);
            state.MarkViewportDirty();
        });
    }

    void DictionarySetValue(Inspector.IProperty p, System.Collections.IDictionary dict, object key, object value) {
        if (dict is null || key is null || !dict.Contains(key)) return;
        dict[key] = value;
        p.Set(dict);
    }

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
            return dict.Contains(def) ? null : def;
        }
        return null;
    }

    static bool IsIntegralKey(Type t) =>
        t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte) ||
        t == typeof(uint) || t == typeof(ulong) || t == typeof(ushort) || t == typeof(sbyte);

    void DrawPolymorphicSlot(Inspector.IProperty p, Type declaredType) {
        object instance = p.Get();
        Type actual = instance?.GetType();

        System.Collections.Generic.IReadOnlyList<Type> derived = TypeCache.GetTypesDerivedFrom(declaredType);

        string current = actual is null ? "None" : Prettify(actual.Name);
        bool typeChanged = false;
        gui.SetNextItemWidth(-1);
        if (gui.BeginCombo($"##poly_{p.Name}", current)) {
            if (gui.Selectable("None", actual is null) && actual is not null) {
                PolymorphicSet(p, null);
                typeChanged = true;
            }

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

        if (typeChanged || instance is null)
            return;

        if (gui.TreeNodeEx($"{Prettify(actual.Name)}###polybody_{p.Name}",
                EditorTreeFlags.DefaultOpen | EditorTreeFlags.SpanAvailWidth)) {
            object boundInstance = instance;
            DrawNestedBody(() => {
            if (BeginNestedGrid($"##polymembers_{p.Name}_{actual.Name}")) {
                foreach (MemberInfo member in ComponentReflection.InspectorMembers(actual)) {
                    MemberAttributes attrs = MemberAttributes.For(member);
                    if (!Conditions.Visible(attrs.Conditionals, boundInstance))
                        continue;
                    MemberInfo capturedMember = member;
                    componentStack.Draw(new Inspector.MemberProperty(capturedMember, boundInstance,
                        v => {
                            ComponentReflection.SetValue(capturedMember, boundInstance, v);
                            p.Set(boundInstance);
                            state.MarkViewportDirty();
                        }), componentGui);
                }
                gui.EndTable();
            }
            });
            gui.TreePop();
        }
    }

    void PolymorphicSet(Inspector.IProperty p, object instance) {
        EditorCommands.Structural($"Set {p.Label}", () => {
            p.Set(instance);
            state.MarkViewportDirty();
        });
    }

    void DrawNestedSlot(Inspector.IProperty p, Type declaredType) {
        object instance = p.Get();

        if (instance is null) {
            if (declaredType.IsValueType) {
                instance = Activator.CreateInstance(declaredType);
            } else {
                object created;
                try { created = Activator.CreateInstance(declaredType); }
                catch { gui.TextDisabled($"({Prettify(declaredType.Name)})"); return; }

                EditorCommands.Structural($"Set {p.Label}", () => {
                    p.Set(created);
                    state.MarkViewportDirty();
                });
                return;
            }
        }

        Type actual = instance.GetType();
        if (gui.TreeNodeEx($"{Prettify(declaredType.Name)}###nestedbody_{p.Name}",
                EditorTreeFlags.DefaultOpen | EditorTreeFlags.SpanAvailWidth)) {
            object boundInstance = instance;
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
                            p.Set(boundInstance);
                            state.MarkViewportDirty();
                        }), componentGui);
                }
                gui.EndTable();
            }
            });
            gui.TreePop();
        }
    }

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

    static bool SceneRefRow(string icon, SysVec4 tint, string name, Guid instanceId) {
        bool clicked = gui.Selectable($"      {name}##{instanceId:N}", false, new SysVec2(0, gui.FrameHeight));
        SysVec2 rmin = gui.ItemRectMin;
        EditorIcons.DrawAt(new SysVec2(rmin.X + 6,
            rmin.Y + (gui.FrameHeight - gui.TextLineHeight) * 0.5f), icon, tint);
        return clicked;
    }

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

    void DrawAddComponentPopup(Entity entity) {
        float u = gui.FontSize;
        gui.SetNextWindowSizeAppearing(new SysVec2(u * 26f, u * 31f));
        if (!gui.BeginPopup("##addcomponent"))
            return;

        gui.PushItemSpacing(new SysVec2(8, 6));

        gui.PushFont(EditorFont.Bold);
        gui.TextUnformatted("Add Component");
        gui.PopFont();
        gui.Spacing();

        if (gui.IsWindowAppearing())
            gui.SetKeyboardFocusHere();
        gui.SetNextItemWidth(-1);
        gui.InputTextWithHint("##addsearch", $"{EditorIcons.Search} Search components...",
            ref addComponentSearch, 128);
        bool enter = gui.IsItemFocused() && gui.KeyPressed(EditorGuiKey.Enter);
        gui.Separator();

        bool searching = addComponentSearch.Length > 0;
        bool Matches(ComponentEntry e) =>
            !searching || e.DisplayName.Contains(addComponentSearch, StringComparison.OrdinalIgnoreCase);

        void Add(ComponentEntry e) {
            EditorCommands.Structural($"Add {e.DisplayName}", () => {
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

    static bool AddComponentRow(ComponentEntry entry) {
        (string icon, SysVec4 tint) = EditorIcons.ForComponentType(entry.Type);
        bool clicked = gui.Selectable($"      {entry.DisplayName}##add{entry.Type.FullName}",
            false, new SysVec2(0, gui.FrameHeight));
        SysVec2 min = gui.ItemRectMin;
        EditorIcons.DrawAt(new SysVec2(min.X + 6, min.Y + (gui.FrameHeight - gui.TextLineHeight) * 0.5f),
            icon, tint);
        return clicked;
    }

    unsafe void DrawMultiAssetInspector() {
        var assets = state.SelectedAssets;

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

    internal static bool AcceptGuidDrop(out Guid guid) {
        guid = Guid.Empty;
        if (!gui.BeginDragDropTarget())
            return false;

        var accepted = false;
        string text = gui.AcceptDragDropPayloadString(AssetBrowserPanel.DragType);
        if (text is not null) {
            var first = text.Split(';', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            accepted = Guid.TryParse(first, out guid);
        }

        gui.EndDragDropTarget();
        return accepted;
    }

    internal static bool BeginGrid(string id) {
        if (!gui.BeginTable(id, 2, EditorTableFlags.SizingStretchProp | EditorTableFlags.PadOuterX))
            return false;
        gui.TableSetupColumn("label", EditorColumnFlags.WidthStretch, 0.38f);
        gui.TableSetupColumn("value", EditorColumnFlags.WidthStretch, 0.62f);
        ResetRowZebra();
        return true;
    }

    static int nestDepth;

    static bool BeginNestedGrid(string id) {
        float s = EditorTheme.UiScale;
        float valueLeft = Inspector.InspectorLayout.ValueColumnLeft(gui.ContentRegionAvail.X, s);

        if (!gui.BeginTable(id, 2, EditorTableFlags.SizingFixedFit | EditorTableFlags.PadOuterX))
            return false;
        float labelW = Inspector.InspectorLayout.LabelColumnWidth(nestDepth, valueLeft, s);
        gui.TableSetupColumn("label", EditorColumnFlags.WidthFixed, labelW);
        gui.TableSetupColumn("value", EditorColumnFlags.WidthStretch, 1f);
        ResetRowZebra();
        return true;
    }

    static void DrawNestedBody(Action body) {
        float indent = gui.IndentSpacing;
        gui.Unindent(indent);
        nestDepth++;
        try { body(); }
        finally {
            nestDepth--;
            gui.Indent(indent);
        }
    }

    internal static void Row(string label) {
        gui.TableNextRow();
        RowChrome();
        gui.TableSetColumnIndex(0);
        gui.AlignTextToFramePadding();
        DrawRowLabel(label, null);
        gui.TableSetColumnIndex(1);
        gui.SetNextItemWidth(-1);
    }

    static void DrawRowLabel(string label, string tooltip) {
        float columnWidth = gui.ContentRegionAvail.X;
        Inspector.InspectorLayout.DrawLabelCell(label, nestDepth, columnWidth, EditorTheme.UiScale, tooltip);
    }

    static void RowWithTooltip(string label, string tooltip) {
        gui.TableNextRow();
        RowChrome();
        gui.TableSetColumnIndex(0);
        gui.AlignTextToFramePadding();
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

    static int rowZebraIndex;
    internal static void ResetRowZebra() => rowZebraIndex = 0;

    static void RowChrome() {
        SysVec2 rowStart = gui.CursorScreenPos;
        float rowH = gui.FrameHeightWithSpacing;
        float x0 = gui.WindowPos.X;
        float x1 = x0 + gui.WindowSize.X;

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

    readonly Dictionary<string, bool> uniformLocks = new();

    void SysVec3Row(string label, Vector3 value, Action<Vector3> apply, float speed, bool allowUniformLock) {
        Row(label);

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

    static SysVec3 ApplyUniformLock(SysVec3 before, SysVec3 after) {
        float dx = MathF.Abs(after.X - before.X), dy = MathF.Abs(after.Y - before.Y), dz = MathF.Abs(after.Z - before.Z);
        int axis = dx >= dy && dx >= dz ? 0 : dy >= dz ? 1 : 2;
        float oldA = axis == 0 ? before.X : axis == 1 ? before.Y : before.Z;
        float newA = axis == 0 ? after.X : axis == 1 ? after.Y : after.Z;
        if (MathF.Abs(newA - oldA) < 1e-9f) return after;

        if (MathF.Abs(oldA) > 1e-6f) {
            float ratio = newA / oldA;
            return new SysVec3(before.X * ratio, before.Y * ratio, before.Z * ratio);
        }
        float delta = newA - oldA;
        return new SysVec3(before.X + delta, before.Y + delta, before.Z + delta);
    }

    static bool IsGameScript(Type type) =>
        type.Assembly.GetName().Name == BallisticEngine.AssetPipeline.GameScripts.AssemblyName;

    static void OpenComponentScript(Type type) {
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
            var csproj = BallisticEngine.AssetPipeline.GameScripts.EnsureProjectFile(AssetDatabase.Project);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(csproj) { UseShellExecute = true });
            var abs = AssetDatabase.Project.ResolveAbsolute(target);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(abs) { UseShellExecute = true });
        }
        catch (Exception ex) {
            Debugging.LogWarning($"Edit Script: {ex.Message}");
        }
    }

    static bool HasComponentOfType(Entity entity, Type type) {
        foreach (Behaviour b in entity.Behaviours)
            if (b.GetType() == type)
                return true;
        return false;
    }

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
