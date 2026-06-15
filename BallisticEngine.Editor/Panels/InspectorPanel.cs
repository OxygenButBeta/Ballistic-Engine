using System.Reflection;
using BallisticEngine.AssetPipeline;
using BallisticEngine.AssetPipeline.Loaders;
using BallisticEngine.Serialization;
using Hexa.NET.ImGui;
using OpenTK.Mathematics;
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
internal sealed class InspectorPanel {
    readonly EditorState state;

    // Pending asset-picker request (opened from an asset slot).
    MemberInfo pickerMember;
    object pickerTarget;
    Type pickerType;
    string pickerSearch = "";
    bool openPicker;

    string addComponentSearch = "";

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

    public InspectorPanel(EditorState state) {
        this.state = state;
        // The standalone component window reuses our reflection member renderer.
        ComponentEditorWindow.Configure(DrawMemberList);
    }

    public void DrawContents() {
        ImGui.PushID(instanceId);   // namespace all ids so a 2nd Inspector window doesn't collide
        // Denser rows than the global style so more fits on screen.
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new SysVec2(8, 4));
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new SysVec2(8, 4));

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
                ImGui.TextDisabled($"{EditorIcons.Package}  {state.SelectedEntities.Count} entities selected");
                ImGui.TextDisabled("Edits apply to ALL selected (matching components).");
                ImGui.Separator();
                ImGui.Spacing();
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
            ImGui.OpenPopup("##assetpicker");
        }
        DrawAssetPickerPopup();

        ImGui.PopStyleVar(2);
        ImGui.PopID();
    }

    // A slim right-aligned lock toggle at the top of the inspector. Locking pins the current entity so
    // selecting other objects doesn't change what's shown (Unity's padlock).
    void DrawLockBar() {
        float btn = ImGui.GetFrameHeight();
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - btn);
        if (locked) {
            ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.CheckMark]);
            if (EditorIcons.GhostButtonSmall("inspectorlock", EditorIcons.Lock, "Inspector locked - click to unlock")) {
                locked = false;
                lockedEntity = null;
            }
            ImGui.PopStyleColor();
        }
        else {
            ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
            if (EditorIcons.GhostButtonSmall("inspectorlock", EditorIcons.LockOpen, "Lock inspector to the current entity")) {
                lockedEntity = state.Selected;
                locked = lockedEntity is not null;
            }
            ImGui.PopStyleColor();
        }
    }

    // Centered hint when nothing is selected, instead of a lone text line in the corner.
    static void DrawEmptyState() {
        SysVec2 avail = ImGui.GetContentRegionAvail();
        ImGui.Dummy(new SysVec2(0, avail.Y * 0.38f));
        CenteredIcon(EditorIcons.Search, 34f, new SysVec4(1, 1, 1, 0.08f));
        ImGui.Spacing();
        CenteredDisabledText("Nothing selected");
        CenteredDisabledText("Select an entity or asset to inspect it.");
    }

    static unsafe void CenteredIcon(string icon, float size, SysVec4 tint) {
        if (!ImGuiController.HasIcons)
            return;
        float w = size; // icon glyphs are roughly square
        ImGui.SetCursorPosX((ImGui.GetWindowWidth() - w) * 0.5f);
        SysVec2 pos = ImGui.GetCursorScreenPos();
        ImGui.GetWindowDrawList().AddText(ImGuiController.LargeIcons, size, pos,
            ImGui.GetColorU32(tint), icon);
        ImGui.Dummy(new SysVec2(w, size));
    }

    static void CenteredDisabledText(string text) {
        float w = ImGui.CalcTextSize(text).X;
        ImGui.SetCursorPosX(Math.Max(0, (ImGui.GetWindowWidth() - w) * 0.5f));
        ImGui.TextDisabled(text);
    }

    // ---- Scene behaviour inspector --------------------------------------------

    void DrawSceneBehaviourInspector(SceneBehaviour behaviour) {
        Type type = behaviour.GetType();
        ImGui.PushID(behaviour.InstanceId.GetHashCode());

        bool enabled = behaviour.IsEnabled;
        bool open = ComponentHeader(Prettify(type.Name), type, ref enabled, out bool menuRequested);
        if (enabled != behaviour.IsEnabled) { EditorUndo.Push($"Toggle {Prettify(type.Name)}"); behaviour.IsEnabled = enabled; state.MarkViewportDirty(); }

        if (menuRequested)
            ImGui.OpenPopup("##componentctx");
        var removeClicked = false;
        if (ImGui.BeginPopup("##componentctx")) {
            if (ImGui.MenuItem("Remove Component")) removeClicked = true;
            ImGui.EndPopup();
        }

        if (open)
            DrawMemberList(type, behaviour);

        if (removeClicked) {
            EditorUndo.Push("Remove Component");
            SceneManager.GetCurrentScene().RemoveSceneBehaviour(behaviour);
            state.SelectSceneBehaviour(null);
            state.MarkViewportDirty();
        }

        ImGui.PopID();
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

        ImGui.Spacing();

        DrawTransform(entity.transform);

        var typeIndex = new Dictionary<Type, int>();
        foreach (Behaviour behaviour in behaviours) {
            Type bt = behaviour.GetType();
            int idx = typeIndex.TryGetValue(bt, out int i) ? i : 0;
            typeIndex[bt] = idx + 1;
            DrawComponent(entity, behaviour, idx);
        }

        ImGui.Spacing();
        ImGui.Spacing();
        DrawAddComponent(entity);
        ImGui.Spacing();
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

        ImGui.PushStyleColor(ImGuiCol.ChildBg, new SysVec4(0.16f, 0.22f, 0.34f, 0.55f));
        ImGui.BeginChild("##prefabbar", new SysVec2(0, ImGui.GetFrameHeight() + 10), ImGuiChildFlags.AutoResizeY);
        ImGui.PushStyleColor(ImGuiCol.Text, new SysVec4(0.55f, 0.74f, 1f, 1f));
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted($"{EditorIcons.Package}  Prefab: {name}");
        ImGui.PopStyleColor();

        if (path is not null) {
            ImGui.SameLine();
            if (ImGui.SmallButton("Select")) state.RequestRevealAsset(path);
        }

        bool hasOverrides = PrefabOverrides.HasAnyOverride;
        ImGui.BeginDisabled(!hasOverrides || path is null);
        ImGui.SameLine();
        if (ImGui.SmallButton("Apply All")) PrefabInstanceOps.ApplyAll(entity);
        ImGui.SameLine();
        if (ImGui.SmallButton("Revert All")) { PrefabInstanceOps.RevertAll(entity); state.MarkViewportDirty(); }
        ImGui.EndDisabled();

        ImGui.EndChild();
        ImGui.PopStyleColor();
        ImGui.Spacing();
    }

    // Rounded card with the entity's type icon, active checkbox, name field and a meta line.
    unsafe void DrawEntityHeaderCard(Entity entity, int componentCount) {
        var draw = ImGui.GetWindowDrawList();
        SysVec2 avail = ImGui.GetContentRegionAvail();
        SysVec2 cardMin = ImGui.GetCursorScreenPos();

        float pad = 10f;
        float frameH = ImGui.GetFrameHeight();
        float cardH = pad + frameH + 4 + ImGui.GetTextLineHeight() + pad;
        SysVec2 cardMax = cardMin + new SysVec2(avail.X, cardH);

        draw.AddRectFilled(cardMin, cardMax, ImGui.GetColorU32(new SysVec4(1, 1, 1, 0.035f)), 6f);
        draw.AddRect(cardMin, cardMax, ImGui.GetColorU32(new SysVec4(0, 0, 0, 0.45f)), 6f);

        // Big type icon on the left.
        (string icon, SysVec4 tint) = EditorIcons.ForEntity(entity);
        float iconSize = cardH - pad * 2 + 6;
        float contentX = cardMin.X + pad;
        if (ImGuiController.HasIcons) {
            draw.AddText(ImGuiController.LargeIcons, iconSize,
                new SysVec2(cardMin.X + pad, cardMin.Y + (cardH - iconSize) * 0.5f),
                ImGui.GetColorU32(entity.IsActive ? tint : new SysVec4(tint.X, tint.Y, tint.Z, 0.4f)),
                icon);
            contentX += iconSize + pad;
        }

        // Row 1: active checkbox + name field.
        ImGui.SetCursorScreenPos(new SysVec2(contentX, cardMin.Y + pad));
        bool active = entity.IsActive;
        if (ImGui.Checkbox("##active", ref active)) { }
        if (ImGui.IsItemActivated()) EditorUndo.Push("Toggle Active");
        if (active != entity.IsActive) { entity.SetActive(active); state.MarkViewportDirty(); }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Active");

        ImGui.SameLine();
        ImGui.SetNextItemWidth(cardMax.X - pad - ImGui.GetCursorScreenPos().X);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, new SysVec4(0, 0, 0, 0.30f));
        var name = entity.Name ?? "";
        var renamed = ImGui.InputText("##name", ref name, 128);
        ImGui.PopStyleColor();
        if (ImGui.IsItemActivated()) EditorUndo.Push("Rename");
        if (renamed) entity.Name = name;

        // Row 2: meta line.
        ImGui.SetCursorScreenPos(new SysVec2(contentX, cardMin.Y + pad + frameH + 4));
        ImGui.TextDisabled(componentCount == 1 ? "1 component" : $"{componentCount} components");

        // Reserve the card's space in the layout — and make it a SCRIPT DROP TARGET: dragging a .cs
        // tile from the asset browser onto the header adds that component to the entity (Unity parity;
        // the hierarchy already accepts this, the inspector didn't).
        ImGui.SetCursorScreenPos(cardMin);
        ImGui.Dummy(new SysVec2(avail.X, cardH));
        AcceptScriptDrop(entity);
    }

    // Drop target for .cs script tiles (asset-browser drag payload = ';'-separated GUIDs). Each that
    // resolves to a compiled Behaviour type is added as a component (skipping dupes), one undo step.
    unsafe void AcceptScriptDrop(Entity entity) {
        if (!ImGui.BeginDragDropTarget())
            return;
        ImGuiPayloadPtr payload = ImGui.AcceptDragDropPayload(AssetBrowserPanel.DragType);
        if (!payload.IsNull && payload.Data != null) {
            string text = System.Runtime.InteropServices.Marshal.PtrToStringAnsi((IntPtr)payload.Data, payload.DataSize);
            bool pushed = false;
            foreach (string part in text?.Split(';', StringSplitOptions.RemoveEmptyEntries) ?? []) {
                if (!Guid.TryParse(part, out Guid guid)) continue;
                Type type = HierarchyPanel.ScriptComponentType(guid);
                if (type is null || HasComponentOfType(entity, type)) continue;
                if (!pushed) { EditorUndo.Push("Add Script Component"); pushed = true; }
                entity.AddComponent(type);
            }
            if (pushed) state.MarkViewportDirty();
        }
        ImGui.EndDragDropTarget();
    }

    // Unity-style Tag + Layer row under the entity header. Both are entity state serialized in the
    // scene, so edits push a scene undo and mark the viewport dirty. Tag options come from TagManager;
    // Layer options from LayerManager.DefinedLayers() (named layers only).
    void DrawTagLayerRow(Entity entity) {
        ImGui.Spacing();
        float half = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) * 0.5f;

        // Tag combo.
        ImGui.SetNextItemWidth(half);
        string currentTag = string.IsNullOrEmpty(entity.Tag) ? TagManager.Untagged : entity.Tag;
        if (ImGui.BeginCombo("##tag", $"{EditorIcons.Pin} {currentTag}")) {
            foreach (string tag in TagManager.Tags) {
                if (ImGui.Selectable(tag, tag == currentTag) && tag != entity.Tag) {
                    EditorUndo.Push("Change Tag");
                    entity.Tag = tag;
                    state.MarkViewportDirty();
                }
            }
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Tag");

        ImGui.SameLine();

        // Layer combo (named layers only).
        ImGui.SetNextItemWidth(half);
        string currentLayerName = LayerManager.NameOf(entity.Layer);
        if (ImGui.BeginCombo("##layer", $"{EditorIcons.Grid} {currentLayerName}")) {
            foreach ((int index, string name) in LayerManager.DefinedLayers()) {
                if (ImGui.Selectable($"{index}: {name}", index == entity.Layer) && index != entity.Layer) {
                    EditorUndo.Push("Change Layer");
                    entity.Layer = index;
                    state.MarkViewportDirty();
                }
            }
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Layer");
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
            SysVec2 hp = ImGui.GetItemRectMax();
            ImGui.GetWindowDrawList().AddCircleFilled(
                new SysVec2(hp.X - 12, (ImGui.GetItemRectMin().Y + hp.Y) * 0.5f), 3.5f,
                ImGui.GetColorU32(new SysVec4(0.45f, 0.66f, 1f, 1f)));
        }

        // The other selected entities' transforms, if this is a multi-selection — edits apply to all
        // of them (Unity-style: a field change moves the whole group by the same DELTA, preserving
        // relative offsets). Empty for a single selection.
        var others = MultiTransforms(transform);

        // Right-click the header for Unity-style resets (apply to the whole selection).
        if (ImGui.BeginPopupContextItem("##transformctx")) {
            if (ImGui.MenuItem("Reset Position")) { EditorUndo.Push("Reset Position"); transform.Position = Vector3.Zero; foreach (Transform o in others) o.Position = Vector3.Zero; }
            if (ImGui.MenuItem("Reset Rotation")) { EditorUndo.Push("Reset Rotation"); transform.EulerAngles = Vector3.Zero; foreach (Transform o in others) o.EulerAngles = Vector3.Zero; }
            if (ImGui.MenuItem("Reset Scale")) { EditorUndo.Push("Reset Scale"); transform.Scale = Vector3.One; foreach (Transform o in others) o.Scale = Vector3.One; }
            ImGui.Separator();
            if (ImGui.MenuItem("Reset All")) {
                EditorUndo.Push("Reset Transform");
                transform.Position = Vector3.Zero; transform.EulerAngles = Vector3.Zero; transform.Scale = Vector3.One;
                foreach (Transform o in others) { o.Position = Vector3.Zero; o.EulerAngles = Vector3.Zero; o.Scale = Vector3.One; }
            }
            ImGui.EndPopup();
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
                    Quaternion delta = transform.Rotation * Quaternion.Invert(oldQ);
                    foreach (Transform o in others) o.Rotation = delta * o.Rotation;
                }
            }, 0.5f);
            SysVec3Row("Scale", transform.Scale, v => {
                Vector3 d = v - transform.Scale; transform.Scale = v;
                foreach (Transform o in others) o.Scale += d;
            }, 0.05f, allowUniformLock: true);
            ImGui.EndTable();
        }

        ImGui.Spacing();
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
        ImGui.Spacing();
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new SysVec2(10, 7));
        float labelX = ImGui.GetTreeNodeToLabelSpacing();
        bool open = ImGui.CollapsingHeader($"###hdr_{label}",
            ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.Framed);
        ImGui.PopStyleVar();

        SysVec2 min = ImGui.GetItemRectMin();
        SysVec2 max = ImGui.GetItemRectMax();
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(min, new SysVec2(min.X + 3, max.Y), ImGui.GetColorU32(ImGuiCol.CheckMark));
        draw.AddText(ImGuiController.Bold, ImGui.GetFontSize(),
            new SysVec2(min.X + labelX, min.Y + (max.Y - min.Y - ImGui.GetFontSize()) * 0.5f),
            ImGui.GetColorU32(ImGuiCol.Text), label);
        return open;
    }

    void DrawComponent(Entity entity, Behaviour behaviour, int typeIndex = 0) {
        Type type = behaviour.GetType();
        ImGui.PushID(behaviour.InstanceId.GetHashCode());

        bool enabled = behaviour.IsEnabled;
        bool open = ComponentHeader(Prettify(type.Name), type, ref enabled, out bool menuRequested);

        // Prefab override badge on the component header: a blue dot if ANY member of this component
        // (matched by registry name + type-index) differs from the prefab definition.
        if (entity.IsPrefabInstance && ComponentHasOverride(behaviour, typeIndex)) {
            SysVec2 mx = ImGui.GetItemRectMax();
            ImGui.GetWindowDrawList().AddCircleFilled(
                new SysVec2(mx.X - 30, (ImGui.GetItemRectMin().Y + mx.Y) * 0.5f), 3.5f,
                ImGui.GetColorU32(new SysVec4(0.45f, 0.66f, 1f, 1f)));
        }
        if (enabled != behaviour.IsEnabled) {
            EditorUndo.Push($"Toggle {Prettify(type.Name)}");
            behaviour.IsEnabled = enabled;
            // Multi-selection: toggle the matching component on every selected entity too.
            foreach (Behaviour sibling in MatchingComponents(behaviour))
                sibling.IsEnabled = enabled;
            state.MarkViewportDirty();
        }

        if (menuRequested)
            ImGui.OpenPopup("##componentctx");

        var removeClicked = false;
        if (ImGui.BeginPopup("##componentctx")) {
            int index = entity.Behaviours.IndexOf(behaviour);
            ImGui.BeginDisabled(index <= 0);
            if (ImGui.MenuItem($"{EditorIcons.ChevronRight}  Move Up")) MoveComponent(entity, behaviour, -1);
            ImGui.EndDisabled();
            ImGui.BeginDisabled(index < 0 || index >= entity.Behaviours.Count - 1);
            if (ImGui.MenuItem($"{EditorIcons.ChevronRight}  Move Down")) MoveComponent(entity, behaviour, +1);
            ImGui.EndDisabled();
            ImGui.Separator();
            if (ImGui.MenuItem($"{EditorIcons.Refresh}  Reset")) ResetComponent(behaviour);
            if (ImGui.MenuItem($"{EditorIcons.Document}  Copy Component")) CopyComponent(behaviour);
            ImGui.BeginDisabled(!CanPasteInto(type));
            if (ImGui.MenuItem($"{EditorIcons.Add}  Paste Component Values")) PasteComponent(behaviour);
            ImGui.EndDisabled();

            // [ContextMenu] methods (Unity's): each parameterless [ContextMenu]-marked method shows
            // here and runs on click, ScriptGuard-protected so a throwing one can't take the editor down.
            bool firstCtx = true;
            foreach (MethodInfo ctxMethod in ComponentReflection.InspectorContextMenus(type)) {
                if (firstCtx) { ImGui.Separator(); firstCtx = false; }
                string ctxLabel = ctxMethod.GetCustomAttribute<ContextMenuAttribute>()?.Label ?? Prettify(ctxMethod.Name);
                if (ImGui.MenuItem($"{EditorIcons.Wrench}  {ctxLabel}")) {
                    EditorUndo.Push(ctxLabel);
                    try { ctxMethod.Invoke(behaviour, null); }
                    catch (Exception ex) { Debugging.LogError($"[ContextMenu] '{ctxLabel}' threw: {ex.InnerException?.Message ?? ex.Message}"); }
                    state.MarkViewportDirty();
                }
            }

            // Edit Script — for game-script components (compiled into GameScripts.dll), open the
            // backing .cs in the OS's default C# editor (item 9). Engine components have no source file.
            if (IsGameScript(type)) {
                ImGui.Separator();
                if (ImGui.MenuItem($"{EditorIcons.Code}  Edit Script"))
                    OpenComponentScript(type);
            }

            ImGui.Separator();
            if (ImGui.MenuItem($"{EditorIcons.Delete}  Remove Component")) removeClicked = true;
            ImGui.EndPopup();
        }

        if (removeClicked) {
            EditorUndo.Push("Remove Component");
            // Multi-selection: remove the matching component from every selected entity too.
            foreach (Behaviour sibling in MatchingComponents(behaviour))
                sibling.Entity.RemoveComponent(sibling);
            entity.RemoveComponent(behaviour);
            state.MarkViewportDirty();
            ImGui.PopID();
            return;
        }

        if (open) {
            DrawMemberList(type, behaviour);

            if (behaviour is Renderer renderer && BeginGrid("##submats")) {
                DrawSubMeshMaterials(renderer);
                ImGui.EndTable();
            }

            if (behaviour is Volume volume)
                DrawVolumeProfileSection(entity, volume);

            if (behaviour is Terrain terrain)
                DrawTerrainBrushSection(terrain);

            if (behaviour is AudioSource audioSource)
                DrawAudioSourceSection(audioSource);

            if (behaviour is Animator animator)
                DrawAnimatorSection(animator);

            if (behaviour is AnimatorController controller)
                DrawAnimatorControllerSection(controller);

            if (behaviour is LightAnimator lightAnim)
                DrawLightAnimatorSection(lightAnim);

            if (behaviour is Spawner spawner)
                DrawSpawnerSection(spawner);

            if (behaviour is Health health)
                DrawHealthSection(health);

            if (behaviour is BallisticEngine.UI.UIDocument uiDoc)
                DrawUIDocumentSection(uiDoc);

            if (behaviour is ParticleSystem particles)
                DrawParticleSystemSection(particles);

            if (behaviour is TrailRenderer trail)
                DrawTrailRendererSection(trail);

            ImGui.Spacing();
        }

        ImGui.PopID();
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
        EditorUndo.Push("Reorder Component");
        (list[i], list[j]) = (list[j], list[i]);
        state.MarkViewportDirty();
    }

    // Resets every inspector member to a fresh instance's defaults (Unity's Reset). Lifecycle members
    // (IsEnabled, attach state) are untouched — only the reflected, editable members.
    void ResetComponent(Behaviour behaviour) {
        Type type = behaviour.GetType();
        Behaviour fresh;
        try { fresh = (Behaviour)Activator.CreateInstance(type); }
        catch { return; }
        EditorUndo.Push($"Reset {Prettify(type.Name)}");
        foreach (MemberInfo member in ComponentReflection.InspectorMembers(type)) {
            try { ComponentReflection.SetValue(member, behaviour, ComponentReflection.GetValue(member, fresh)); }
            catch { /* read-only / computed member — skip */ }
        }
        state.MarkViewportDirty();
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
        EditorUndo.Push($"Paste {Prettify(behaviour.GetType().Name)}");
        foreach (MemberInfo member in ComponentReflection.InspectorMembers(behaviour.GetType())) {
            if (clipboardMembers.TryGetValue(member.Name, out object value)) {
                try { ComponentReflection.SetValue(member, behaviour, value); }
                catch { /* incompatible member — skip */ }
            }
        }
        state.MarkViewportDirty();
    }

    // Inline profile editing under a Volume component, Unity-style: the profile's overrides are
    // edited in place (and saved straight back to the .volume asset), or a fresh profile asset
    // can be created and assigned in one click.
    void DrawVolumeProfileSection(Entity entity, Volume volume) {
        ImGui.Spacing();

        if (volume.Profile is null) {
            if (ImGui.Button($"{EditorIcons.Add}  New Profile", new SysVec2(-1, 0)))
                CreateProfileAsset(entity, volume);
            ImGui.TextDisabled("Creates a .volume asset and assigns it.");
            return;
        }

        ImGui.SeparatorText("Overrides");
        // UNDO for volume-profile edits (bug 2b): the profile is a .volume ASSET, outside scene-undo.
        // Snapshot before drawing; if a parameter changed, push a callback undo step when the edit
        // SETTLES (no item active) so a slider drag is one entry, not hundreds. The before-snapshot is
        // captured at the start of a drag (the frame the change first appears) and held until release.
        object beforeSnap = VolumeProfileEditor.Snapshot(volume.Profile);
        if (VolumeProfileEditor.Draw(volume.Profile)) {
            VolumeProfileEditor.SaveToAsset(volume.Profile);
            state.MarkViewportDirty();

            VolumeProfile prof = volume.Profile;
            // Remember the state from BEFORE this drag started (first changed frame).
            volumeUndoBefore ??= volumeUndoLastClean;
            volumeUndoBefore ??= beforeSnap;

            // Commit one undo step when the interaction ends (mouse released / instantaneous widget).
            if (!ImGui.IsAnyItemActive()) {
                object before = volumeUndoBefore;
                object after = VolumeProfileEditor.Snapshot(prof);
                EditorUndo.PushCallback("Edit Volume Override",
                    () => { VolumeProfileEditor.Restore(prof, before); VolumeProfileEditor.SaveToAsset(prof); state.MarkViewportDirty(); },
                    () => { VolumeProfileEditor.Restore(prof, after); VolumeProfileEditor.SaveToAsset(prof); state.MarkViewportDirty(); });
                volumeUndoBefore = null;
            }
        }
        else if (!ImGui.IsAnyItemActive()) {
            // Idle: this clean snapshot is the "before" for the next edit.
            volumeUndoLastClean = beforeSnap;
            volumeUndoBefore = null;
        }
    }

    // Volume-profile undo bookkeeping (see DrawVolumeSection): the snapshot from before the current
    // drag began, and the last settled (clean) snapshot to use as its baseline.
    static object volumeUndoBefore;
    static object volumeUndoLastClean;

    // Terrain sculpting palette: a Sculpt toggle that arms the Scene-view brush, the brush mode, and
    // radius/strength (and a target height for Flatten/Set). Drives TerrainTool's static state; the
    // actual sculpting happens in the viewport. Not part of scene undo — brush settings are editor
    // tool state, and each stroke pushes its own undo + saves the .terrain asset.
    static void DrawTerrainBrushSection(Terrain terrain) {
        ImGui.Spacing();

        if (terrain.Terrain3D is null) {
            ImGui.TextDisabled("Assign a Terrain asset to sculpt (or create one: Assets > New Terrain).");
            TerrainTool.Armed = false;
            return;
        }

        ImGui.SeparatorText("Sculpt");

        bool armed = TerrainTool.Armed;
        if (ImGui.Checkbox("Enable Brush", ref armed))
            TerrainTool.Armed = armed;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Left-drag in the Scene view to sculpt. While on, clicks paint instead of selecting.");

        if (!armed)
            return;

        // Brush mode.
        string[] modes = ["Raise", "Lower", "Smooth", "Flatten", "Set"];
        int mode = (int)TerrainTool.Brush;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.Combo("##terrainbrush", ref mode, modes, modes.Length))
            TerrainTool.Brush = (TerrainSculpt.Brush)mode;

        float radius = TerrainTool.Radius;
        if (ImGui.SliderFloat("Radius", ref radius, 0.5f, 60f, "%.1f"))
            TerrainTool.Radius = radius;

        float strength = TerrainTool.Strength;
        if (ImGui.SliderFloat("Strength", ref strength, 0.01f, 2f, "%.2f"))
            TerrainTool.Strength = strength;

        // Flatten/Set converge toward a target height (0..1 of the terrain's HeightScale).
        if (TerrainTool.Brush is TerrainSculpt.Brush.Flatten or TerrainSculpt.Brush.Set) {
            float target = TerrainTool.TargetHeight;
            if (ImGui.SliderFloat("Target Height", ref target, 0f, 1f, "%.2f"))
                TerrainTool.TargetHeight = target;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Normalized height (x HeightScale) the brush levels toward.");
        }

        ImGui.TextDisabled("Pick Lower to dig; Smooth/Flatten to level.");
    }

    // AudioSource preview: a Preview/Stop button so you can hear a clip without entering play mode.
    // Uses the static Audio facade (play-mode-independent), so it works in edit mode; AudioSource.Play
    // itself is gated to play mode. Graceful no-op when no audio device is present (headless CI).
    static IAudioVoice audioPreviewVoice;
    static float audioPreviewTime;   // scrub-slider position (seconds), persists between previews
    void DrawAudioSourceSection(AudioSource source) {
        ImGui.Spacing();
        ImGui.SeparatorText("Preview");

        if (source.Clip is null) {
            ImGui.TextDisabled("Assign a Clip to preview.");
            return;
        }

        bool playing = audioPreviewVoice is { IsPlaying: true };
        if (ImGui.Button(playing ? $"{EditorIcons.Pause}  Stop" : $"{EditorIcons.Play}  Preview",
                new SysVec2(120, 0))) {
            audioPreviewVoice?.Stop();
            audioPreviewVoice = playing
                ? null
                : Audio.Play(source.Clip, source.Volume, source.Pitch, loop: false);
            playing = !playing;
        }
        ImGui.SameLine();
        ImGui.TextDisabled($"{source.Clip.DurationSeconds:F1}s, {source.Clip.Channels}ch, {source.Clip.SampleRate}Hz");

        DrawAudioScrubber(source.Clip, source.Volume, source.Pitch);

        if (!Audio.IsAvailable)
            ImGui.TextDisabled("(no audio device on this machine — preview is silent)");
    }

    // Time slider under the preview button: shows the play head while previewing and lets you scrub.
    // Dragging seeks the live voice; releasing on a stopped voice restarts playback from that offset
    // (so you can scrub a finished/idle clip to a spot and hear it from there).
    void DrawAudioScrubber(AudioClip clip, float volume, float pitch) {
        float duration = MathF.Max(clip.DurationSeconds, 0.001f);
        bool live = audioPreviewVoice is { IsPlaying: true };

        // While playing, the play head drives the slider; otherwise keep the last scrub position so the
        // handle doesn't snap back to 0 between previews.
        if (live)
            audioPreviewTime = Math.Clamp(audioPreviewVoice.TimeSeconds, 0f, duration);

        ImGui.SetNextItemWidth(-1);
        float t = audioPreviewTime;
        if (ImGui.SliderFloat("##audioScrub", ref t, 0f, duration, "%.2fs")) {
            audioPreviewTime = Math.Clamp(t, 0f, duration);
            if (audioPreviewVoice is { IsPlaying: true })
                audioPreviewVoice.TimeSeconds = audioPreviewTime;   // seek the live voice
            else {
                // Scrubbing an idle clip: start a fresh voice and jump it to the scrub point.
                audioPreviewVoice = Audio.Play(clip, volume, pitch, loop: false);
                if (audioPreviewVoice is not null)
                    audioPreviewVoice.TimeSeconds = audioPreviewTime;
            }
        }

        // Keep the inspector repainting so the play head animates under on-demand rendering.
        if (live)
            state.MarkViewportDirty();
    }

    // Animator preview: a play/pause toggle + a scrub slider that evaluates the clip in edit mode, so
    // you can pose the skinned character without entering play. Drives Animator.EvaluatePreview, which
    // runs the same sample->skeleton->skinning pipeline as play-mode Tick.
    void DrawAnimatorSection(Animator animator) {
        ImGui.Spacing();
        ImGui.SeparatorText("Preview");

        if (animator.Clip is null) {
            ImGui.TextDisabled("Assign a Clip to preview.");
            return;
        }

        float duration = MathF.Max(animator.Clip.DurationSeconds, 0.001f);

        if (ImGui.Button(animatorPreviewPlaying ? $"{EditorIcons.Pause}  Pause" : $"{EditorIcons.Play}  Play",
                new SysVec2(100, 0)))
            animatorPreviewPlaying = !animatorPreviewPlaying;
        ImGui.SameLine();
        if (ImGui.Button($"{EditorIcons.Refresh}  Reset", new SysVec2(100, 0))) {
            animatorPreviewTime = 0f;
            animatorPreviewPlaying = false;
        }

        if (animatorPreviewPlaying) {
            animatorPreviewTime += (float)Time.DeltaTime;
            if (animator.Loop && animatorPreviewTime > duration)
                animatorPreviewTime %= duration;
            state.MarkViewportDirty(); // keep the viewport repainting while previewing
        }

        float t = animatorPreviewTime;
        if (ImGui.SliderFloat("##animScrub", ref t, 0f, duration, "%.2fs")) {
            animatorPreviewTime = t;
            animatorPreviewPlaying = false;
        }

        // Apply the previewed pose this frame (edit mode only — play mode drives it from Tick).
        if (!SceneManager.IsPlaying) {
            animator.EvaluatePreview(animatorPreviewTime);
            state.MarkViewportDirty();
        }

        // Animation events (script-driven). Show the count + the last fired event so you can confirm
        // they're wired and firing in play mode.
        if (animator.EventCount > 0) {
            ImGui.Spacing();
            ImGui.SeparatorText("Events");
            ImGui.TextDisabled($"{animator.EventCount} event(s) registered");
            if (!string.IsNullOrEmpty(animator.LastFiredEvent))
                ImGui.TextDisabled($"Last fired: {animator.LastFiredEvent}");
        }
    }

    static bool animatorPreviewPlaying;
    static float animatorPreviewTime;

    // AnimatorController: a live view of the state machine. The graph is script-built (states +
    // transitions are wired in OnBegin), so this is a runtime DEBUG/DRIVE surface — it lists the states
    // with the current one highlighted, and renders a poker for each declared parameter (checkbox for
    // bool, slider for float/int, a button for triggers) so you can drive the graph from the inspector
    // in play mode without writing test code (very AI-managed-friendly: set "Speed" and watch it cross
    // from idle->walk->run live).
    void DrawAnimatorControllerSection(AnimatorController controller) {
        ImGui.Spacing();
        ImGui.SeparatorText("State Machine");

        if (controller.StateCount == 0) {
            ImGui.TextDisabled("No states. Build the graph in a script's OnBegin:");
            ImGui.TextDisabled("  AddState(name, clip); state.To(target, param, Compare, ...)");
            return;
        }

        if (!SceneManager.IsPlaying)
            ImGui.TextDisabled("Enter play mode to drive the graph.");

        // Current state banner.
        string cur = controller.CurrentStateName ?? "(none)";
        ImGui.Text("Current: ");
        ImGui.SameLine();
        ImGui.TextColored(new SysVec4(0.45f, 0.85f, 1f, 1f), cur);

        // State list with the active one highlighted.
        ImGui.Spacing();
        ImGui.TextDisabled($"States ({controller.StateCount})");
        foreach (AnimatorController.State s in controller.States) {
            bool isCurrent = s.Name == controller.CurrentStateName;
            string label = $"{(isCurrent ? EditorIcons.Play + " " : "   ")}{s.Name}";
            string clipName = s.Clip is not null ? s.Clip.Name : "(no clip)";
            if (isCurrent)
                ImGui.TextColored(new SysVec4(0.45f, 0.85f, 1f, 1f), $"{label}  ->  {clipName}");
            else
                ImGui.TextDisabled($"{label}  ->  {clipName}");
            // A click jumps to the state (play mode) — handy for testing.
            if (SceneManager.IsPlaying && ImGui.IsItemClicked())
                controller.Play(s.Name);
        }

        // Parameter pokers.
        var prms = controller.Parameters;
        if (prms.Count > 0) {
            ImGui.Spacing();
            ImGui.SeparatorText("Parameters");
            foreach (var kv in prms) {
                string name = kv.Key;
                switch (kv.Value) {
                    case AnimatorController.ParamKind.Bool: {
                        bool b = controller.GetBool(name);
                        if (ImGui.Checkbox(name, ref b)) controller.SetBool(name, b);
                        break;
                    }
                    case AnimatorController.ParamKind.Trigger: {
                        if (ImGui.Button($"{EditorIcons.Play} {name}", new SysVec2(140, 0)))
                            controller.SetTrigger(name);
                        ImGui.SameLine();
                        ImGui.TextDisabled(controller.GetTrigger(name) ? "(set)" : "");
                        break;
                    }
                    case AnimatorController.ParamKind.Int: {
                        int iv = controller.GetInt(name);
                        if (ImGui.DragInt(name, ref iv)) controller.SetInt(name, iv);
                        break;
                    }
                    default: { // Float
                        float fv = controller.GetFloat(name);
                        if (ImGui.DragFloat(name, ref fv, 0.05f)) controller.SetFloat(name, fv);
                        break;
                    }
                }
            }
        }

        if (SceneManager.IsPlaying)
            state.MarkViewportDirty(); // keep repainting so transitions show live
    }

    // LightAnimator: a live preview toggle that animates the light IN EDIT MODE (so you can dial in a
    // flicker/pulse without entering play), plus a warning if there's no light on the entity to drive.
    // The IntensityCurve / ColorOverTime members render their curve+gradient widgets automatically via
    // the reflection DrawMember, so this only adds the preview control.
    void DrawLightAnimatorSection(LightAnimator lightAnim) {
        ImGui.Spacing();
        ImGui.SeparatorText("Preview");

        bool hasLight = lightAnim.GetComponent<PointLight>() is not null
                     || lightAnim.GetComponent<SpotLight>() is not null;
        if (!hasLight) {
            ImGui.TextColored(new SysVec4(1f, 0.7f, 0.3f, 1f), "No PointLight or SpotLight on this entity.");
            ImGui.TextDisabled("Add one — the animator drives its Intensity + Color.");
            return;
        }

        if (ImGui.Button(lightAnimPreview ? $"{EditorIcons.Pause}  Stop Preview" : $"{EditorIcons.Play}  Preview",
                new SysVec2(140, 0))) {
            lightAnimPreview = !lightAnimPreview;
            if (lightAnimPreview) lightAnimPreviewClock = 0f;
            else { lightAnim.RestoreBase(); state.MarkViewportDirty(); } // un-dim the light when stopping
        }
        ImGui.SameLine();
        ImGui.TextDisabled(lightAnim.Animation.ToString());

        // Drive the light in edit mode along its own preview clock (play mode runs Tick itself).
        if (lightAnimPreview && !SceneManager.IsPlaying) {
            lightAnimPreviewClock += (float)Time.DeltaTime;
            lightAnim.Apply(lightAnimPreviewClock);
            state.MarkViewportDirty();
        }
    }

    static bool lightAnimPreview;
    static float lightAnimPreviewClock;

    // Spawner: live alive/pooled counts + a manual Spawn One / Clear. Spawning only runs in play mode
    // (Tick), so the manual button is most useful there; in edit mode it instantiates immediately so
    // you can preview the prefab placement, and Clear cleans those up.
    void DrawSpawnerSection(Spawner spawner) {
        ImGui.Spacing();
        ImGui.SeparatorText("Spawner");

        if (spawner.Prefab is null) {
            ImGui.TextColored(new SysVec4(1f, 0.7f, 0.3f, 1f), "Assign a Prefab to spawn.");
            return;
        }

        ImGui.Text($"Alive: {spawner.AliveCount} / {spawner.MaxAlive}");
        ImGui.SameLine();
        ImGui.TextDisabled($"(pooled: {spawner.PooledCount})");

        if (ImGui.Button($"{EditorIcons.Play}  Spawn One", new SysVec2(120, 0))) {
            spawner.Spawn();
            state.MarkViewportDirty();
        }
        ImGui.SameLine();
        if (ImGui.Button($"{EditorIcons.Refresh}  Clear", new SysVec2(120, 0))) {
            spawner.Clear();
            state.MarkViewportDirty();
        }

        if (SceneManager.IsPlaying && spawner.AliveCount > 0)
            state.MarkViewportDirty(); // keep repainting while instances live/expire
    }

    // Health: a current/max bar (green->red by fraction) + Damage/Heal/Kill/Revive test buttons so you
    // can exercise the events without play-mode scripting. The OnDamaged/OnHealed/OnDied BEvents render
    // their own listener editors automatically via the reflection DrawMember.
    // UIDocument's Uxml/Uss are string PATHS; give them drag-drop target fields so you can drop a
    // .uxml/.uss (or .uihtml/.uss) asset from the browser instead of typing the address (item 15).
    void DrawUIDocumentSection(BallisticEngine.UI.UIDocument doc) {
        ImGui.Spacing();
        ImGui.SeparatorText("Markup & Style");
        DrawPathDropField("UXML (markup)", doc.Uxml, [".uxml", ".uihtml", ".html"], p => doc.Uxml = p);
        DrawPathDropField("USS (style)", doc.Uss, [".uss", ".uicss", ".css"], p => doc.Uss = p);
        ImGui.TextDisabled("Drag a markup/style asset here, or type its Assets/... path.");
    }

    // A text field for an asset PATH that also accepts a drag-drop of a matching-extension asset (sets
    // the field to the dropped asset's path). `exts` are the accepted extensions (lowercase, with dot).
    void DrawPathDropField(string label, string current, string[] exts, Action<string> apply) {
        ImGui.PushID(label);
        ImGui.TextDisabled(label);
        var s = current ?? "";
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText("##path", ref s, 256)) {
            EditorUndo.Push($"Edit {label}");
            apply(s);
            state.MarkViewportDirty();
        }
        // Drop target over the field: accept a single matching asset and write its path.
        if (AcceptGuidDrop(out Guid guid)) {
            string path = AssetDatabase.GuidToAssetPath(guid);
            if (path is not null && exts.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase))) {
                EditorUndo.Push($"Assign {label}");
                apply(path);
                state.MarkViewportDirty();
            }
        }
        ImGui.PopID();
    }

    void DrawHealthSection(Health health) {
        ImGui.Spacing();
        ImGui.SeparatorText("Health");

        float frac = health.HealthFraction;
        // Manual bar (green->red by remaining fraction), so it works without ProgressBar styling.
        var draw = ImGui.GetWindowDrawList();
        SysVec2 p = ImGui.GetCursorScreenPos();
        float w = MathF.Max(ImGui.GetContentRegionAvail().X, 60f);
        const float h = 18f;
        draw.AddRectFilled(p, p + new SysVec2(w, h), 0xFF202428, 3f);
        var barCol = ImGui.GetColorU32(new SysVec4(1f - frac, frac, 0.12f, 1f));
        if (frac > 0f)
            draw.AddRectFilled(p, p + new SysVec2(w * frac, h), barCol, 3f);
        draw.AddRect(p, p + new SysVec2(w, h), 0xFF000000, 3f);
        string label = health.IsDead ? "DEAD" : $"{health.CurrentHealth:0} / {health.MaxHealth:0}";
        SysVec2 ts = ImGui.CalcTextSize(label);
        draw.AddText(p + new SysVec2((w - ts.X) * 0.5f, (h - ts.Y) * 0.5f), 0xFFFFFFFF, label);
        ImGui.Dummy(new SysVec2(w, h));

        if (ImGui.Button("Damage 10", new SysVec2(90, 0))) { health.TakeDamage(10f); state.MarkViewportDirty(); }
        ImGui.SameLine();
        if (ImGui.Button("Heal 10", new SysVec2(90, 0))) { health.Heal(10f); state.MarkViewportDirty(); }
        ImGui.SameLine();
        if (ImGui.Button("Kill", new SysVec2(70, 0))) { health.Kill(); state.MarkViewportDirty(); }
        ImGui.SameLine();
        if (ImGui.Button("Revive", new SysVec2(70, 0))) { health.Revive(); state.MarkViewportDirty(); }

        if (!SceneManager.IsPlaying)
            ImGui.TextDisabled("Edit-mode tests don't fire DestroyOnDeath (play only).");
    }

    // ParticleSystem preview: it already animates live in the editor (AdvanceAll runs every editor
    // frame), so this just adds a Restart (clear) + a one-shot Emit test + a live count, and keeps the
    // viewport repainting while particles are alive so you see the motion.
    void DrawParticleSystemSection(ParticleSystem particles) {
        ImGui.Spacing();
        ImGui.SeparatorText("Preview");

        // Two equal half-width buttons that fill the row (auto-width 110px clipped the labels to
        // "Resta.../Emit 5" in a narrow inspector); the live count goes on its own line so nothing
        // gets squeezed off.
        float spacing = ImGui.GetStyle().ItemSpacing.X;
        float w = (ImGui.GetContentRegionAvail().X - spacing) * 0.5f;
        if (ImGui.Button($"{EditorIcons.Refresh}  Restart", new SysVec2(w, 0)))
            particles.Clear();
        ImGui.SameLine();
        if (ImGui.Button($"{EditorIcons.Play}  Emit 50", new SysVec2(w, 0)))
            particles.Emit(50);
        ImGui.TextDisabled($"{particles.LiveCount} live");

        if (particles.LiveCount > 0)
            state.MarkViewportDirty();
    }

    // TrailRenderer preview: also animates live in the editor; add a Clear + a live point count.
    void DrawTrailRendererSection(TrailRenderer trail) {
        ImGui.Spacing();
        ImGui.SeparatorText("Preview");

        if (ImGui.Button($"{EditorIcons.Refresh}  Clear", new SysVec2(-1, 0)))
            trail.Clear();
        ImGui.TextDisabled($"{trail.PointCount} points");

        if (trail.PointCount > 0)
            state.MarkViewportDirty();
    }

    static void CreateProfileAsset(Entity entity, Volume volume) {
        var baseName = entity.Name is { Length: > 0 } entityName ? entityName : "Volume";
        string assetPath = null;
        for (var i = 0; i < 100; i++) {
            var candidate = $"Assets/{baseName} Profile{(i == 0 ? "" : $" {i}")}.volume";
            if (!File.Exists(AssetDatabase.Project.ResolveAbsolute(candidate))) {
                assetPath = candidate;
                break;
            }
        }
        if (assetPath is null)
            return;

        VolumeProfileLoader.Save(new VolumeProfile(), AssetDatabase.Project.ResolveAbsolute(assetPath));

        // The new file needs a refresh pass to get its meta/GUID before it can be loaded + assigned.
        AsyncAssetImport.Request("Importing profile...", onFinished: () => {
            EditorUndo.Push("Assign Profile");
            volume.Profile = AssetDatabase.Load<VolumeProfile>(assetPath);
        });
    }

    // Collapsible component header: framed bar with a category-tinted stripe + type icon, a bold
    // label, an enable checkbox after the arrow, and a "..." menu on the right edge. Right-click
    // opens the same "##componentctx" popup the caller declares. Returns the open state;
    // `enabled` is edited in place and `menuRequested` fires when "..." is clicked.
    static unsafe bool ComponentHeader(string label, Type type, ref bool enabled, out bool menuRequested) {
        menuRequested = false;
        (string icon, SysVec4 tint) = EditorIcons.ForComponentType(type);

        ImGui.Spacing();
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new SysVec2(10, 7));
        float arrowW = ImGui.GetTreeNodeToLabelSpacing();
        bool open = ImGui.CollapsingHeader($"###cmphdr_{label}",
            ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.AllowOverlap | ImGuiTreeNodeFlags.Framed);
        ImGui.PopStyleVar();
        ImGui.OpenPopupOnItemClick("##componentctx", ImGuiPopupFlags.MouseButtonRight);

        SysVec2 min = ImGui.GetItemRectMin();
        SysVec2 max = ImGui.GetItemRectMax();
        float headerH = max.Y - min.Y;
        var draw = ImGui.GetWindowDrawList();

        // Category stripe down the left edge.
        draw.AddRectFilled(min, new SysVec2(min.X + 3, max.Y), ImGui.GetColorU32(tint));

        SysVec2 cursor = ImGui.GetCursorScreenPos();

        // Enable checkbox right after the disclosure arrow.
        float frameH = ImGui.GetFrameHeight();
        float chkX = min.X + arrowW;
        ImGui.SetCursorScreenPos(new SysVec2(chkX, min.Y + (headerH - frameH) * 0.5f));
        ImGui.Checkbox($"##en_{label}", ref enabled);

        // Type icon + bold label after the checkbox.
        float fontSize = ImGui.GetFontSize();
        float textY = min.Y + (headerH - fontSize) * 0.5f;
        float iconX = chkX + frameH + 6;
        var dimmed = enabled ? 1f : 0.45f;
        draw.AddText(new SysVec2(iconX, textY),
            ImGui.GetColorU32(new SysVec4(tint.X, tint.Y, tint.Z, dimmed)), icon);
        draw.AddText(ImGuiController.Bold, fontSize,
            new SysVec2(iconX + fontSize + 6, textY),
            ImGui.GetColorU32(enabled ? ImGuiCol.Text : ImGuiCol.TextDisabled), label);

        // "..." menu pinned to the right edge.
        float moreW = EditorIcons.SmallButtonWidth(EditorIcons.More);
        ImGui.SetCursorScreenPos(new SysVec2(max.X - moreW - 6,
            min.Y + (headerH - ImGui.GetTextLineHeight()) * 0.5f - ImGui.GetStyle().FramePadding.Y * 0.5f + 2));
        ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        if (EditorIcons.GhostButtonSmall($"more_{label}", EditorIcons.More, "Component menu"))
            menuRequested = true;
        ImGui.PopStyleColor();

        ImGui.SetCursorScreenPos(cursor);
        return open;
    }

    // Draws all inspector members of a component, honouring [Header]/[Space] (which break the
    // two-column grid into stacked sub-tables) and the per-member attributes. The caller has
    // already drawn the Enabled row in its own grid and closed it.
    void DrawMemberList(Type type, object target) {
        var gridOpen = false;
        var gridIndex = 0;       // each sub-table (split by Header/Space/foldout) needs a unique id
        string currentGroup = null;  // active [FoldoutGroup] name
        var groupOpen = true;        // is the active foldout expanded (members drawn)?

        void EnsureGrid() {
            if (!gridOpen)
                gridOpen = BeginGrid($"##members{type.Name}{gridIndex++}");
        }

        void CloseGrid() {
            if (gridOpen) { ImGui.EndTable(); gridOpen = false; }
        }

        void EndGroup() {
            if (currentGroup is null)
                return;
            CloseGrid();
            if (groupOpen) ImGui.TreePop();   // balance the open TreeNodeEx
            currentGroup = null;
            groupOpen = true;
        }

        foreach (MemberInfo member in ComponentReflection.InspectorMembers(type)) {
            MemberAttributes attrs = MemberAttributes.For(member);
            string group = attrs.Foldout?.Name;

            // Leaving the current foldout group (different/no group, or a new header) closes it.
            if (group != currentGroup || attrs.Header is not null)
                EndGroup();

            if (attrs.Space is not null) { CloseGrid(); ImGui.Dummy(new SysVec2(0, attrs.Space.Height)); }
            if (attrs.Header is not null) { CloseGrid(); ImGui.SeparatorText(attrs.Header.Text); }

            // Entering a new foldout group: draw its collapsible header once. When open, the matching
            // TreePop happens in EndGroup; when collapsed, TreeNodeEx requires no TreePop.
            if (group is not null && group != currentGroup) {
                CloseGrid();
                var flags = attrs.Foldout.DefaultOpen ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None;
                groupOpen = ImGui.TreeNodeEx($"{group}###fold_{type.Name}_{group}",
                    flags | ImGuiTreeNodeFlags.Framed | ImGuiTreeNodeFlags.SpanAvailWidth);
                currentGroup = group;
            }

            if (currentGroup is not null && !groupOpen)
                continue;                       // member hidden inside a collapsed foldout

            EnsureGrid();
            DrawMember(member, target, attrs);
        }

        EndGroup();
        CloseGrid();

        // [Button] methods render as full-width action buttons below the fields (clearer than
        // self-resetting bool checkboxes for one-shot operations like a probe bake).
        foreach (MethodInfo method in ComponentReflection.InspectorButtons(type)) {
            var label = method.GetCustomAttribute<ButtonAttribute>()?.Label ?? method.Name;
            if (ImGui.Button($"{label}###btn_{type.Name}_{method.Name}", new SysVec2(-1, 0)))
                method.Invoke(target, null);
        }

        // [EditorWindowExecutionPoint] methods: a window-open button that runs the method (state setup)
        // then opens a dedicated big window for this component.
        foreach (MethodInfo method in ComponentReflection.InspectorWindowPoints(type)) {
            var attr = method.GetCustomAttribute<EditorWindowExecutionPointAttribute>();
            string label = attr?.Title ?? $"Open {Prettify(type.Name)} Window";
            if (ImGui.Button($"{EditorIcons.Maximize}  {label}###win_{type.Name}_{method.Name}", new SysVec2(-1, 0))) {
                try { method.Invoke(target, null); }
                catch (Exception e) { Debugging.LogError($"Editor window method threw: {e.Message}"); }
                ComponentEditorWindow.Open(target, attr?.Title ?? Prettify(type.Name));
            }
        }
    }

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
            ImGui.SameLine(0, 4);
            ImGui.TextColored(new SysVec4(1f, 0.72f, 0.25f, 1f), "—");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Values differ across the selection. Editing sets them all to this value.");
        }
    }

    void DrawMember(MemberInfo member, object target, MemberAttributes attrs) {
        Type memberType = ComponentReflection.MemberType(member);
        object value = ComponentReflection.GetValue(member, target);

        RowWithTooltip(Prettify(member.Name), attrs.Tooltip?.Text);
        // Mixed-value marker: in a multi-selection, an amber dash when the selected entities DISAGREE
        // on this member (the field shows the active entity's value; editing it sets them all alike).
        DrawMixedMarker(member, target, value);
        ImGui.PushID(member.Name);
        ImGui.SetNextItemWidth(-1);
        if (attrs.ReadOnly) ImGui.BeginDisabled();

        // Every edit auto-registers ONE undo step via InspectorUndo.Track (snapshot on edit-begin,
        // commit on edit-end) — no per-widget EditorUndo.Push, so no case can forget it. Each change
        // also marks the viewport dirty (on-demand render) since a value edit can alter the picture.
        string label = $"Edit {Prettify(member.Name)}";

        if (typeof(BEvent).IsAssignableFrom(memberType)) {
            // Serialized event (UnityEvent-style): a multi-row listener editor. The component owns
            // the instance (a `public BEvent X = new();` field) so we edit it in place, never reassign.
            BEventEditor.Draw(member.Name, value as BEvent);
        }
        else if (typeof(BObject).IsAssignableFrom(memberType)) {
            DrawAssetSlot(member, target, value as BObject, memberType);
        }
        else {
            switch (value) {
                case float f: {
                    bool changed = InspectorUndo.Track(label, attrs.Range is { } r
                        ? ImGui.SliderFloat("##v", ref f, r.Min, r.Max)
                        : ImGui.DragFloat("##v", ref f, 0.05f));
                    if (changed) {
                        if (attrs.Range is { } rc) f = Math.Clamp(f, rc.Min, rc.Max);
                        ApplyMember(member, target, f);
                        state.MarkViewportDirty();
                    }
                    break;
                }
                case int i: {
                    bool changed = InspectorUndo.Track(label, attrs.Range is { } r
                        ? ImGui.SliderInt("##v", ref i, (int)r.Min, (int)r.Max)
                        : ImGui.DragInt("##v", ref i));
                    if (changed) {
                        if (attrs.Range is { } rc) i = Math.Clamp(i, (int)rc.Min, (int)rc.Max);
                        ApplyMember(member, target, i);
                        state.MarkViewportDirty();
                    }
                    break;
                }
                case bool b: {
                    var changed = InspectorUndo.Track(label, ImGui.Checkbox("##v", ref b));
                    if (changed) { ApplyMember(member, target, b); state.MarkViewportDirty(); }
                    break;
                }
                case string s: {
                    var str = s ?? "";
                    var changed = InspectorUndo.Track(label, ImGui.InputText("##v", ref str, 256));
                    if (changed) { ApplyMember(member, target, str); state.MarkViewportDirty(); }
                    break;
                }
                case Vector3 v3: {
                    var sv = new SysVec3(v3.X, v3.Y, v3.Z);
                    // [ColorUsage] (or a "...Color" name) gets a color picker; HDR allows >1.
                    var isColor = attrs.ColorUsage is not null ||
                                  member.Name.EndsWith("Color", StringComparison.Ordinal);
                    bool changed;
                    if (isColor) {
                        var flags = attrs.ColorUsage?.Hdr == true
                            ? ImGuiColorEditFlags.Hdr | ImGuiColorEditFlags.Float
                            : ImGuiColorEditFlags.None;
                        changed = InspectorUndo.Track(label, ImGui.ColorEdit3("##v", ref sv, flags));
                    }
                    else {
                        changed = AxisVec3("v3", label, ref sv, 0.05f);
                    }
                    if (changed) { ApplyMember(member, target, new Vector3(sv.X, sv.Y, sv.Z)); state.MarkViewportDirty(); }
                    break;
                }
                case Vector2 v2: {
                    var sv = new SysVec2(v2.X, v2.Y);
                    var changed = InspectorUndo.Track(label, ImGui.DragFloat2("##v", ref sv, 0.05f));
                    if (changed) { ApplyMember(member, target, new Vector2(sv.X, sv.Y)); state.MarkViewportDirty(); }
                    break;
                }
                case Enum e: {
                    string[] names = Enum.GetNames(memberType);
                    // IndexOf returns -1 when the value doesn't match a single declared name (a [Flags]
                    // combination or an out-of-range cast); fall back to the first entry so the combo
                    // shows a valid label instead of blank.
                    int current = Math.Max(0, Array.IndexOf(names, e.ToString()));
                    var changed = InspectorUndo.Track(label, ImGui.Combo("##v", ref current, names, names.Length));
                    if (changed) { ApplyMember(member, target, Enum.Parse(memberType, names[current])); state.MarkViewportDirty(); }
                    break;
                }
                case AnimationCurve curve: {
                    // Interactive curve widget — applies to ANY AnimationCurve member with no per-
                    // component wiring. Mutated in place (reference type), so no SetValue needed; an
                    // undo snapshot is pushed when an edit begins. The "Edit" button opens the full
                    // standalone CurveEditorWindow; edits there fire the dirty callback to repaint.
                    if (DrawCurveEditor(member.Name, curve, state.MarkViewportDirty))
                        state.MarkViewportDirty();
                    break;
                }
                case ColorGradient gradient: {
                    // Interactive gradient bar — same auto-apply-to-any-member story as the curve.
                    if (DrawGradientEditor(member.Name, gradient))
                        state.MarkViewportDirty();
                    break;
                }
                default:
                    ImGui.TextDisabled($"({memberType.Name})");
                    break;
            }
        }

        if (attrs.ReadOnly) ImGui.EndDisabled();
        ImGui.PopID();
    }

    // ---- Vector widgets ---------------------------------------------------------

    // Unity-style vector editor: three drag fields, each with a colored X/Y/Z chip fused to its
    // left edge. `label` is the undo-step name; each axis auto-registers one undo entry per drag via
    // InspectorUndo.Track (the single static pending slot is safe — only one axis edits at a time).
    static bool AxisVec3(string id, string label, ref SysVec3 v, float speed) {
        float gap = 4;
        float chipW = MathF.Round(ImGui.GetFontSize() * 0.92f);
        float cellW = (ImGui.GetContentRegionAvail().X - gap * 2) / 3f;
        float fieldW = Math.Max(26f, cellW - chipW);

        var changed = AxisDrag($"##{id}x", "X", label, EditorIcons.AxisX, ref v.X, speed, chipW, fieldW);
        ImGui.SameLine(0, gap);
        changed |= AxisDrag($"##{id}y", "Y", label, EditorIcons.AxisY, ref v.Y, speed, chipW, fieldW);
        ImGui.SameLine(0, gap);
        changed |= AxisDrag($"##{id}z", "Z", label, EditorIcons.AxisZ, ref v.Z, speed, chipW, fieldW);
        return changed;
    }

    static bool AxisDrag(string id, string letter, string label, SysVec4 color, ref float value,
        float speed, float chipW, float fieldW) {
        var draw = ImGui.GetWindowDrawList();
        SysVec2 pos = ImGui.GetCursorScreenPos();
        float h = ImGui.GetFrameHeight();
        float rounding = ImGui.GetStyle().FrameRounding;

        draw.AddRectFilled(pos, pos + new SysVec2(chipW, h), ImGui.GetColorU32(color),
            rounding, ImDrawFlags.RoundCornersLeft);
        SysVec2 ts = ImGui.CalcTextSize(letter);
        draw.AddText(pos + new SysVec2((chipW - ts.X) * 0.5f, (h - ts.Y) * 0.5f),
            ImGui.GetColorU32(new SysVec4(0.07f, 0.08f, 0.09f, 1f)), letter);

        ImGui.Dummy(new SysVec2(chipW, h));
        ImGui.SameLine(0, 0);
        ImGui.SetNextItemWidth(fieldW);
        return InspectorUndo.Track(label, ImGui.DragFloat(id, ref value, speed, 0, 0, "%.2f"));
    }

    // ---- AnimationCurve editor ---------------------------------------------------
    // An interactive curve widget: a plot box that samples the curve into a polyline, draggable
    // keyframe dots (drag to move time+value), double-click empty space to add a key, right-click a
    // key to remove it, and preset buttons (Linear / Ease / Constant). Reusable for ANY AnimationCurve
    // member. The curve is mutated in place; returns true when an edit happened (caller marks dirty).
    // The plot auto-fits its value range to the keys (with a small pad) so any amplitude is visible.
    static int curveDragKey = -1; // index of the key being dragged (-1 = none); single-widget assumption

    static bool DrawCurveEditor(string id, AnimationCurve curve, Action onExternalEdit = null) {
        bool edited = false;
        ImGui.PushID(id);

        float w = ImGui.GetContentRegionAvail().X;
        const float height = 90f;
        SysVec2 origin = ImGui.GetCursorScreenPos();
        var size = new SysVec2(MathF.Max(w, 60f), height);
        var draw = ImGui.GetWindowDrawList();

        // Background + border.
        draw.AddRectFilled(origin, origin + size, ImGui.GetColorU32(new SysVec4(0.10f, 0.11f, 0.13f, 1f)), 4f);
        draw.AddRect(origin, origin + size, ImGui.GetColorU32(new SysVec4(0.30f, 0.32f, 0.36f, 1f)), 4f);

        // Time range = [first key, last key] (default [0,1]); value range auto-fits the keys.
        float t0 = 0f, t1 = 1f, vMin = 0f, vMax = 1f;
        if (curve.Count > 0) {
            t0 = curve.Keys[0].Time;
            t1 = curve.Keys[curve.Count - 1].Time;
            vMin = float.MaxValue; vMax = float.MinValue;
            for (var i = 0; i < curve.Count; i++) {
                vMin = MathF.Min(vMin, curve.Keys[i].Value);
                vMax = MathF.Max(vMax, curve.Keys[i].Value);
            }
        }
        if (t1 <= t0) t1 = t0 + 1f;
        if (vMax <= vMin) { vMin -= 0.5f; vMax += 0.5f; }
        float vPad = (vMax - vMin) * 0.12f;
        vMin -= vPad; vMax += vPad;

        SysVec2 ToScreen(float time, float value) {
            float fx = (time - t0) / (t1 - t0);
            float fy = (value - vMin) / (vMax - vMin);
            return new SysVec2(origin.X + fx * size.X, origin.Y + (1f - fy) * size.Y);
        }

        // Zero line (if 0 is in the value range) for reference.
        if (vMin < 0f && vMax > 0f) {
            float zy = origin.Y + (1f - (0f - vMin) / (vMax - vMin)) * size.Y;
            draw.AddLine(new SysVec2(origin.X, zy), new SysVec2(origin.X + size.X, zy),
                ImGui.GetColorU32(new SysVec4(0.4f, 0.4f, 0.45f, 0.4f)));
        }

        // Sample the curve into a polyline across the box width.
        const int Samples = 64;
        uint curveColor = ImGui.GetColorU32(new SysVec4(0.45f, 0.85f, 1f, 1f));
        SysVec2 prev = default;
        for (var s = 0; s <= Samples; s++) {
            float time = t0 + (t1 - t0) * s / Samples;
            SysVec2 p = ToScreen(time, curve.Evaluate(time));
            if (s > 0) draw.AddLine(prev, p, curveColor, 2f);
            prev = p;
        }

        // An invisible button over the box captures interaction (hover/click/drag).
        ImGui.InvisibleButton("##curvebox", size);
        bool hovered = ImGui.IsItemHovered();
        SysVec2 mouse = ImGui.GetMousePos();

        float SnapTimeFromMouse() => t0 + (t1 - t0) * Math.Clamp((mouse.X - origin.X) / size.X, 0f, 1f);
        float SnapValueFromMouse() => vMax - (vMax - vMin) * Math.Clamp((mouse.Y - origin.Y) / size.Y, 0f, 1f);

        // Draw + hit-test keyframe dots.
        const float dotR = 5f;
        int hoverKey = -1;
        for (var i = 0; i < curve.Count; i++) {
            SysVec2 sp = ToScreen(curve.Keys[i].Time, curve.Keys[i].Value);
            bool near = (mouse - sp).LengthSquared() <= (dotR + 3f) * (dotR + 3f);
            if (near && hovered) hoverKey = i;
            uint dc = (i == curveDragKey || near)
                ? ImGui.GetColorU32(new SysVec4(1f, 0.85f, 0.3f, 1f))
                : ImGui.GetColorU32(new SysVec4(1f, 1f, 1f, 1f));
            draw.AddCircleFilled(sp, dotR, dc);
        }

        // Begin a drag on a key (snapshot for undo once).
        if (hovered && hoverKey >= 0 && ImGui.IsMouseClicked(ImGuiMouseButton.Left)) {
            curveDragKey = hoverKey;
            EditorUndo.Push($"Edit {id}");
        }
        // Drag the held key.
        if (curveDragKey >= 0 && curveDragKey < curve.Count && ImGui.IsMouseDown(ImGuiMouseButton.Left)) {
            curveDragKey = curve.MoveKey(curveDragKey, SnapTimeFromMouse(), SnapValueFromMouse());
            edited = true;
        }
        if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
            curveDragKey = -1;

        // Double-click empty space adds a key on the curve at that time.
        if (hovered && hoverKey < 0 && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) {
            EditorUndo.Push($"Add key {id}");
            curve.AddKey(SnapTimeFromMouse(), SnapValueFromMouse());
            edited = true;
        }
        // Right-click a key removes it (keep at least one).
        if (hovered && hoverKey >= 0 && curve.Count > 1 && ImGui.IsMouseClicked(ImGuiMouseButton.Right)) {
            EditorUndo.Push($"Remove key {id}");
            curve.RemoveKey(hoverKey);
            edited = true;
        }

        // Preset buttons + "open full editor" + key count.
        if (ImGui.SmallButton("Linear")) { EditorUndo.Push($"Preset {id}"); Replace(curve, AnimationCurve.Linear()); edited = true; }
        ImGui.SameLine();
        if (ImGui.SmallButton("Ease")) { EditorUndo.Push($"Preset {id}"); Replace(curve, AnimationCurve.EaseInOut()); edited = true; }
        ImGui.SameLine();
        if (ImGui.SmallButton("Const")) { EditorUndo.Push($"Preset {id}"); Replace(curve, AnimationCurve.Constant()); edited = true; }
        ImGui.SameLine();
        if (ImGui.SmallButton($"{EditorIcons.Maximize} Edit"))
            CurveEditorWindow.Open(curve, id, onExternalEdit ?? (() => { }));
        ImGui.SameLine();
        ImGui.TextDisabled($"{curve.Count} keys");

        ImGui.PopID();
        return edited;
    }

    // Replaces a curve's keys with another curve's (in place — preserves the member's instance).
    static void Replace(AnimationCurve target, AnimationCurve source) {
        target.Clear();
        for (var i = 0; i < source.Count; i++)
            target.AddKey(source.Keys[i]);
        target.PreWrap = source.PreWrap;
        target.PostWrap = source.PostWrap;
    }

    // ---- Gradient editor ---------------------------------------------------------
    // An interactive gradient bar (Unity's gradient editor, trimmed): the bar samples Evaluate across
    // its width; COLOR stops sit as triangles BELOW the bar (drag horizontally to move, click to open a
    // color picker, double-click empty to add, right-click to remove), ALPHA stops as triangles ABOVE
    // (drag horizontally to move, vertical drag to change alpha). Reusable for ANY Gradient member;
    // mutated in place; returns true on edit. The checkerboard behind the bar shows alpha.
    static int gradColorDrag = -1, gradAlphaDrag = -1;
    static int gradColorPick = -1; // color stop whose picker popup is open

    static bool DrawGradientEditor(string id, ColorGradient g) {
        bool edited = false;
        ImGui.PushID(id);

        float w = MathF.Max(ImGui.GetContentRegionAvail().X, 60f);
        const float barH = 22f, stopH = 7f;
        SysVec2 cursor = ImGui.GetCursorScreenPos();
        SysVec2 barOrigin = cursor + new SysVec2(0f, stopH + 2f); // leave room for alpha stops above
        var barSize = new SysVec2(w, barH);
        var draw = ImGui.GetWindowDrawList();

        // Checkerboard so alpha is visible.
        const float check = 6f;
        for (float x = 0; x < w; x += check)
            for (float y = 0; y < barH; y += check) {
                bool dark = (((int)(x / check) + (int)(y / check)) & 1) == 0;
                uint cc = dark ? 0xFF606060 : 0xFF909090;
                SysVec2 a = barOrigin + new SysVec2(x, y);
                SysVec2 b = a + new SysVec2(MathF.Min(check, w - x), MathF.Min(check, barH - y));
                draw.AddRectFilled(a, b, cc);
            }

        // Sample the gradient across the bar width into thin vertical slices.
        const int slices = 96;
        for (var s = 0; s < slices; s++) {
            float t0 = (float)s / slices, t1 = (float)(s + 1) / slices;
            Vector4 c0 = g.Evaluate(t0);
            uint col = ImGui.GetColorU32(new SysVec4(c0.X, c0.Y, c0.Z, c0.W));
            SysVec2 a = barOrigin + new SysVec2(t0 * w, 0f);
            SysVec2 b = barOrigin + new SysVec2(t1 * w, barH);
            draw.AddRectFilled(a, b, col);
        }
        draw.AddRect(barOrigin, barOrigin + barSize, 0xFF202224);

        // Interaction surface covering the bar + both stop rows.
        SysVec2 totalSize = new SysVec2(w, barH + stopH * 2f + 4f);
        ImGui.SetCursorScreenPos(cursor);
        ImGui.InvisibleButton("##gradbar", totalSize);
        bool hovered = ImGui.IsItemHovered();
        SysVec2 mouse = ImGui.GetMousePos();
        float mt = Math.Clamp((mouse.X - barOrigin.X) / w, 0f, 1f);

        float alphaRowY = cursor.Y;                       // alpha stops above the bar
        float colorRowY = barOrigin.Y + barH + 2f;        // color stops below the bar

        // ---- Color stops (below) ----
        int hoverColor = -1;
        for (var i = 0; i < g.ColorKeyCount; i++) {
            float kx = barOrigin.X + g.ColorKeys[i].Time * w;
            var tip = new SysVec2(kx, colorRowY);
            var bl = new SysVec2(kx - stopH * 0.6f, colorRowY + stopH);
            var br = new SysVec2(kx + stopH * 0.6f, colorRowY + stopH);
            Vector3 kc = g.ColorKeys[i].Color;
            uint fill = ImGui.GetColorU32(new SysVec4(kc.X, kc.Y, kc.Z, 1f));
            draw.AddTriangleFilled(tip, bl, br, fill);
            draw.AddTriangle(tip, bl, br, (i == gradColorDrag) ? 0xFF30D0FF : 0xFF202224);
            if (hovered && MathF.Abs(mouse.X - kx) < stopH && mouse.Y >= colorRowY - 2f && mouse.Y <= colorRowY + stopH + 2f)
                hoverColor = i;
        }

        // ---- Alpha stops (above) ----
        int hoverAlpha = -1;
        for (var i = 0; i < g.AlphaKeyCount; i++) {
            float kx = barOrigin.X + g.AlphaKeys[i].Time * w;
            var tip = new SysVec2(kx, alphaRowY + stopH);
            var tl = new SysVec2(kx - stopH * 0.6f, alphaRowY);
            var tr = new SysVec2(kx + stopH * 0.6f, alphaRowY);
            float av = g.AlphaKeys[i].Alpha;
            uint fill = ImGui.GetColorU32(new SysVec4(av, av, av, 1f));
            draw.AddTriangleFilled(tip, tl, tr, fill);
            draw.AddTriangle(tip, tl, tr, (i == gradAlphaDrag) ? 0xFF30D0FF : 0xFF202224);
            if (hovered && MathF.Abs(mouse.X - kx) < stopH && mouse.Y >= alphaRowY - 2f && mouse.Y <= alphaRowY + stopH + 2f)
                hoverAlpha = i;
        }

        // ---- Begin drags / picker / add / remove ----
        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left)) {
            if (hoverColor >= 0) { gradColorDrag = hoverColor; EditorUndo.Push($"Edit {id}"); }
            else if (hoverAlpha >= 0) { gradAlphaDrag = hoverAlpha; EditorUndo.Push($"Edit {id}"); }
        }
        // Open a color picker popup on a color-stop click-release (only if not dragged far).
        if (hoverColor >= 0 && ImGui.IsMouseReleased(ImGuiMouseButton.Left) && gradColorDrag == hoverColor) {
            gradColorPick = hoverColor;
            ImGui.OpenPopup("##gradcolpick");
        }

        if (gradColorDrag >= 0 && gradColorDrag < g.ColorKeyCount && ImGui.IsMouseDown(ImGuiMouseButton.Left)) {
            gradColorDrag = g.MoveColorKey(gradColorDrag, mt, g.ColorKeys[gradColorDrag].Color);
            edited = true;
        }
        if (gradAlphaDrag >= 0 && gradAlphaDrag < g.AlphaKeyCount && ImGui.IsMouseDown(ImGuiMouseButton.Left)) {
            gradAlphaDrag = g.MoveAlphaKey(gradAlphaDrag, mt, g.AlphaKeys[gradAlphaDrag].Alpha);
            edited = true;
        }
        if (ImGui.IsMouseReleased(ImGuiMouseButton.Left)) { gradColorDrag = -1; gradAlphaDrag = -1; }

        // Double-click empty space on the color row adds a color stop (sampled current color).
        if (hovered && hoverColor < 0 && mouse.Y >= colorRowY - 2f && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) {
            EditorUndo.Push($"Add color {id}");
            g.AddColorKey(mt, g.EvaluateColor(mt));
            edited = true;
        }
        // Double-click empty space on the alpha row adds an alpha stop.
        if (hovered && hoverAlpha < 0 && mouse.Y <= alphaRowY + stopH + 2f && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) {
            EditorUndo.Push($"Add alpha {id}");
            g.AddAlphaKey(mt, g.EvaluateAlpha(mt));
            edited = true;
        }
        // Right-click removes the hovered stop (keep at least one of each kind).
        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right)) {
            if (hoverColor >= 0 && g.ColorKeyCount > 1) { EditorUndo.Push($"Remove color {id}"); g.RemoveColorKey(hoverColor); edited = true; }
            else if (hoverAlpha >= 0 && g.AlphaKeyCount > 1) { EditorUndo.Push($"Remove alpha {id}"); g.RemoveAlphaKey(hoverAlpha); edited = true; }
        }

        // Color picker popup for the selected color stop.
        if (ImGui.BeginPopup("##gradcolpick")) {
            if (gradColorPick >= 0 && gradColorPick < g.ColorKeyCount) {
                Vector3 c = g.ColorKeys[gradColorPick].Color;
                var sv = new SysVec3(c.X, c.Y, c.Z);
                if (ImGui.ColorPicker3("##pick", ref sv)) {
                    g.MoveColorKey(gradColorPick, g.ColorKeys[gradColorPick].Time, new Vector3(sv.X, sv.Y, sv.Z));
                    edited = true;
                }
            }
            ImGui.EndPopup();
        }

        // Preset buttons + counts.
        if (ImGui.SmallButton("Fire")) { EditorUndo.Push($"Preset {id}"); ReplaceGradient(g, ColorGradient.Fire()); edited = true; }
        ImGui.SameLine();
        if (ImGui.SmallButton("Fade")) { EditorUndo.Push($"Preset {id}"); ReplaceGradient(g, ColorGradient.FadeOut(new Vector3(1f, 1f, 1f))); edited = true; }
        ImGui.SameLine();
        ImGui.TextDisabled($"{g.ColorKeyCount}c / {g.AlphaKeyCount}a");

        ImGui.PopID();
        return edited;
    }

    static void ReplaceGradient(ColorGradient target, ColorGradient source) {
        target.Clear();
        for (var i = 0; i < source.ColorKeyCount; i++)
            target.AddColorKey(source.ColorKeys[i].Time, source.ColorKeys[i].Color);
        for (var i = 0; i < source.AlphaKeyCount; i++)
            target.AddAlphaKey(source.AlphaKeys[i].Time, source.AlphaKeys[i].Alpha);
    }

    // Multi-material meshes resolve their materials from refs baked into the mesh at import;
    // list them read-only so an empty SharedMaterial slot isn't mistaken for "no materials".
    // (SharedMaterial only overrides slots that have no baked ref.)
    static void DrawSubMeshMaterials(Renderer renderer) {
        Mesh mesh = renderer.SharedMesh;
        if (mesh?.SubMeshes is not { Length: > 1 } subMeshes)
            return;

        // A single-submesh renderer (one entity per source mesh) shows just its own slot.
        var only = renderer.SubMeshIndex;
        if (only >= 0 && only < subMeshes.Length) {
            DrawSubMeshMaterialRow(renderer, subMeshes[only], only, "Material");
            return;
        }

        // Whole-mesh renderers of split imports can have hundreds of submeshes; cap the list
        // (it's read-only info — per-part slots live on the instantiated child entities).
        const int MaxRows = 24;
        var rows = Math.Min(subMeshes.Length, MaxRows);
        for (var i = 0; i < rows; i++)
            DrawSubMeshMaterialRow(renderer, subMeshes[i], i, i == 0 ? $"Materials ({subMeshes.Length})" : "");
        if (subMeshes.Length > MaxRows) {
            Row("");
            ImGui.TextDisabled($"... and {subMeshes.Length - MaxRows} more");
        }
    }

    static void DrawSubMeshMaterialRow(Renderer renderer, SubMeshData sub, int i, string rowLabel) {
        Row(rowLabel);

        var label = string.IsNullOrEmpty(sub.Name) ? $"Submesh {i}" : sub.Name;
        Material material = renderer.MaterialFor(i);

        if (material is null) {
            ImGui.TextDisabled($"{label} — none");
            return;
        }

        var reference = sub.MaterialRef;
        if (reference is null && AssetDatabase.TryGetAssetGuid(material, out Guid guid))
            reference = AssetDatabase.GuidToAssetPath(guid);

        ImGui.TextUnformatted($"{EditorIcons.Color} {Path.GetFileNameWithoutExtension(reference ?? label)}");
        if (reference is not null && ImGui.IsItemHovered())
            ImGui.SetTooltip($"{label}\n{reference}");
    }

    // Asset slot. Assigned: clicking the name PINS the asset in the Inspector (shows its
    // asset view), the chevron button opens the picker. Unassigned: click opens the picker.
    // Either way the slot is a drag-drop target for browser tiles.
    void DrawAssetSlot(MemberInfo member, object target, BObject asset, Type assetType) {
        Guid guid = default;
        var hasGuid = asset is not null && AssetDatabase.TryGetAssetGuid(asset, out guid);

        if (asset is null) {
            ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
            if (ImGui.Button($"None  {EditorIcons.ChevronDown}", new SysVec2(-1, 0)))
                OpenPickerFor(member, target, assetType);
            ImGui.PopStyleColor();
            if (AcceptGuidDrop(out Guid d0))
                AssignAsset(member, target, assetType, d0);
            return;
        }

        var path = hasGuid ? AssetDatabase.GuidToAssetPath(guid) : null;
        var display = path is not null ? Path.GetFileName(path) : asset.GetType().Name;
        (string icon, _) = EditorIcons.ForAssetExtension(
            path is not null ? Path.GetExtension(path).ToLowerInvariant() : "");

        float pickerW = ImGui.GetFrameHeight() + 6;
        if (ImGui.Button($"{icon}  {display}", new SysVec2(-pickerW - 4, 0)) && path is not null)
            state.RequestRevealAsset(path); // jump to it in the asset browser, don't swap the inspector
        if (AcceptGuidDrop(out Guid d1))
            AssignAsset(member, target, assetType, d1);
        if (ImGui.IsItemHovered() && path is not null)
            ImGui.SetTooltip($"{path}\nClick to reveal in the asset browser.");

        ImGui.SameLine();
        if (ImGui.Button(EditorIcons.ChevronDown, new SysVec2(pickerW, 0)))
            OpenPickerFor(member, target, assetType);
        if (AcceptGuidDrop(out Guid d2))
            AssignAsset(member, target, assetType, d2);
    }

    void OpenPickerFor(MemberInfo member, object target, Type assetType) {
        pickerMember = member;
        pickerTarget = target;
        pickerType = assetType;
        openPicker = true;
    }

    void AssignAsset(MemberInfo member, object target, Type assetType, Guid guid) {
        EditorUndo.Push($"Assign {Prettify(member.Name)}");
        MethodInfo load = typeof(AssetDatabase).GetMethod(nameof(AssetDatabase.Load), [typeof(Guid)])!
            .MakeGenericMethod(assetType);
        object loaded = load.Invoke(null, [guid]);
        if (loaded is not null)
            ApplyMember(member, target, loaded); // broadcasts to the multi-selection like value edits
        state.MarkViewportDirty();
    }

    // Mini asset-picker window: search + every compatible asset; click to assign.
    void DrawAssetPickerPopup() {
        float u = ImGui.GetFontSize();
        ImGui.SetNextWindowSize(new SysVec2(u * 28f, u * 30f), ImGuiCond.Appearing);
        if (!ImGui.BeginPopup("##assetpicker"))
            return;

        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new SysVec2(8, 6));

        // Header: "Select <Type>" in bold + a hint of which extensions qualify.
        string typeName = pickerType is null ? "Asset" : Prettify(pickerType.Name);
        ImGui.PushFont(ImGuiController.Bold);
        ImGui.TextUnformatted($"Select {typeName}");
        ImGui.PopFont();

        string[] extensions = CompatibleExtensions(pickerType);
        if (extensions.Length > 0) {
            ImGui.SameLine();
            ImGui.TextDisabled($"({string.Join(" ", extensions)})");
        }
        ImGui.Spacing();

        if (ImGui.IsWindowAppearing())
            ImGui.SetKeyboardFocusHere();
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##search", $"{EditorIcons.Search} Search {typeName.ToLowerInvariant()}s...",
            ref pickerSearch, 128);
        ImGui.Separator();

        ImGui.BeginChild("##list");

        // (None) clears the slot.
        ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        if (ImGui.Selectable($"  (None)", false, ImGuiSelectableFlags.None, new SysVec2(0, ImGui.GetFrameHeight()))) {
            EditorUndo.Push($"Clear {Prettify(pickerMember.Name)}");
            ComponentReflection.SetValue(pickerMember, pickerTarget, null);
            state.MarkViewportDirty();
            ImGui.CloseCurrentPopup();
        }
        ImGui.PopStyleColor();

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
            bool clicked = ImGui.Selectable($"      {Path.GetFileName(path)}##{guid}", false,
                ImGuiSelectableFlags.None, new SysVec2(0, ImGui.GetFrameHeight()));
            SysVec2 rmin = ImGui.GetItemRectMin();
            EditorIcons.DrawAt(new SysVec2(rmin.X + 6,
                rmin.Y + (ImGui.GetFrameHeight() - ImGui.GetTextLineHeight()) * 0.5f), icon, tint);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(path);
            if (clicked) {
                AssignAsset(pickerMember, pickerTarget, pickerType, guid);
                ImGui.CloseCurrentPopup();
            }
        }

        if (!any)
            ImGui.TextDisabled(pickerSearch.Length > 0
                ? "No matching assets."
                : $"No {typeName.ToLowerInvariant()} assets in the project.");

        ImGui.EndChild();
        ImGui.PopStyleVar();
        ImGui.EndPopup();
    }

    // Selected .volume asset: edit the live profile instance directly (every Volume referencing
    // it sees the change immediately) and persist on change.
    static void DrawVolumeProfileAsset(Guid guid) {
        var profile = AssetDatabase.Load<VolumeProfile>(guid);
        if (profile is null) {
            ImGui.TextDisabled("Unreadable volume profile.");
            return;
        }

        if (VolumeProfileEditor.Draw(profile))
            VolumeProfileEditor.SaveToAsset(profile);
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

    // Centered accent-tinted Add Component button with a searchable popup, Unity-style.
    void DrawAddComponent(Entity entity) {
        float avail = ImGui.GetContentRegionAvail().X;
        float w = Math.Clamp(avail * 0.72f, 180f, 320f);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (avail - w) * 0.5f);

        SysVec4 accent = ImGui.GetStyle().Colors[(int)ImGuiCol.CheckMark];
        ImGui.PushStyleColor(ImGuiCol.Button, new SysVec4(accent.X, accent.Y, accent.Z, 0.16f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new SysVec4(accent.X, accent.Y, accent.Z, 0.30f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new SysVec4(accent.X, accent.Y, accent.Z, 0.42f));
        var clicked = ImGui.Button($"{EditorIcons.Add}  Add Component", new SysVec2(w, 0));
        ImGui.PopStyleColor(3);

        if (clicked) {
            addComponentSearch = "";
            ImGui.OpenPopup("##addcomponent");
        }

        DrawAddComponentPopup(entity);
    }

    // Unity-style component browser: a roomy popup with a search box and the registry grouped into
    // collapsible categories (by ComponentEntry.Menu). Searching flattens to a filtered list and
    // Enter adds the top hit. Rows carry the type icon + tint.
    void DrawAddComponentPopup(Entity entity) {
        // Sized off the current font so it scales with DPI/UI-scale without a controller handle.
        float u = ImGui.GetFontSize();
        ImGui.SetNextWindowSize(new SysVec2(u * 26f, u * 31f), ImGuiCond.Appearing);
        if (!ImGui.BeginPopup("##addcomponent"))
            return;

        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new SysVec2(8, 6));

        // Header.
        ImGui.PushFont(ImGuiController.Bold);
        ImGui.TextUnformatted("Add Component");
        ImGui.PopFont();
        ImGui.Spacing();

        if (ImGui.IsWindowAppearing())
            ImGui.SetKeyboardFocusHere();
        ImGui.SetNextItemWidth(-1);
        // NOTE: do NOT use EnterReturnsTrue here — with Hexa's managed ref-string overload that flag
        // defers the buffer write-back until Enter, so live typing wouldn't filter. Detect Enter
        // separately while the field is active.
        ImGui.InputTextWithHint("##addsearch", $"{EditorIcons.Search} Search components...",
            ref addComponentSearch, 128);
        bool enter = ImGui.IsItemFocused() && ImGui.IsKeyPressed(ImGuiKey.Enter);
        ImGui.Separator();

        bool searching = addComponentSearch.Length > 0;
        bool Matches(ComponentEntry e) =>
            !searching || e.DisplayName.Contains(addComponentSearch, StringComparison.OrdinalIgnoreCase);

        void Add(ComponentEntry e) {
            EditorUndo.Push($"Add {e.DisplayName}");
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
            ImGui.CloseCurrentPopup();
        }

        ImGui.BeginChild("##addlist");

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
                ImGui.TextDisabled("No components match.");
            if (enter && first is { } f)
                Add(f);
        }
        else {
            // Grouped by category (Menu); ungrouped entries fall under "General".
            foreach (var group in ComponentRegistry.Menu
                         .GroupBy(e => string.IsNullOrEmpty(e.Menu) ? "General" : e.Menu)
                         .OrderBy(g => g.Key == "General" ? "zzz" : g.Key)) {
                if (!ImGui.CollapsingHeader(group.Key, ImGuiTreeNodeFlags.DefaultOpen))
                    continue;
                foreach (ComponentEntry entry in group)
                    if (AddComponentRow(entry))
                        Add(entry);
            }
        }

        ImGui.EndChild();
        ImGui.PopStyleVar();
        ImGui.EndPopup();
    }

    // One selectable component row: type icon + display name, taller than a default row.
    static bool AddComponentRow(ComponentEntry entry) {
        (string icon, SysVec4 tint) = EditorIcons.ForComponentType(entry.Type);
        bool clicked = ImGui.Selectable($"      {entry.DisplayName}##add{entry.Type.FullName}",
            false, ImGuiSelectableFlags.None, new SysVec2(0, ImGui.GetFrameHeight()));
        SysVec2 min = ImGui.GetItemRectMin();
        EditorIcons.DrawAt(new SysVec2(min.X + 6, min.Y + (ImGui.GetFrameHeight() - ImGui.GetTextLineHeight()) * 0.5f),
            icon, tint);
        return clicked;
    }

    // ---- Multi-asset inspector -------------------------------------------------

    // Shown when the browser has 2+ assets selected: the selection list, batch import settings
    // (texture type, when every selected asset is an image), and batch delete.
    unsafe void DrawMultiAssetInspector() {
        var assets = state.SelectedAssets;

        // Header.
        var draw = ImGui.GetWindowDrawList();
        SysVec2 start = ImGui.GetCursorScreenPos();
        float iconSize = 36f;
        if (ImGuiController.HasIcons) {
            draw.AddText(ImGuiController.LargeIcons, iconSize, start + new SysVec2(0, 2),
                ImGui.GetColorU32(new SysVec4(0.70f, 0.76f, 0.86f, 1f)), EditorIcons.Document);
            ImGui.SetCursorScreenPos(start + new SysVec2(iconSize + 10, 0));
        }
        ImGui.BeginGroup();
        draw.AddText(ImGuiController.Bold, ImGui.GetFontSize(), ImGui.GetCursorScreenPos(),
            ImGui.GetColorU32(ImGuiCol.Text), $"{assets.Count} assets selected");
        ImGui.Dummy(new SysVec2(0, ImGui.GetTextLineHeight()));
        ImGui.TextDisabled("Edits below apply to the whole selection.");
        ImGui.EndGroup();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // BY-TYPE breakdown instead of a flat file list: "3 Volume", "2 Terrain", ... — and clicking
        // a type row narrows the selection to just that type (Unity-style "select all of a kind").
        var byExt = assets
            .GroupBy(a => Path.GetExtension(a.Path).ToLowerInvariant())
            .OrderByDescending(g => g.Count())
            .ToList();
        ImGui.TextDisabled("By type (click to select just that kind):");
        ImGui.Spacing();
        foreach (var group in byExt) {
            string ext = group.Key;
            (string icon, SysVec4 tint) = EditorIcons.ForAssetExtension(ext);
            string typeName = string.IsNullOrEmpty(ext) ? "File" : ext.TrimStart('.');
            int n = group.Count();
            ImGui.PushStyleColor(ImGuiCol.Text, tint);
            ImGui.TextUnformatted(icon);
            ImGui.PopStyleColor();
            ImGui.SameLine(0, 6);
            if (ImGui.Selectable($"{n}  {typeName}{(n == 1 ? "" : "s")}##type{ext}", false)) {
                var ofType = group.ToList();
                state.SelectAssets(ofType, ofType[^1]);
            }
        }

        ImGui.Spacing();

        // Batch texture type — only when every selected asset is an image with a meta file.
        var allmmages = assets.All(a => Path.GetExtension(a.Path).ToLowerInvariant()
            is ".png" or ".jpg" or ".jpeg" or ".tga" or ".bmp" or ".hdr" or ".exr");
        if (allmmages)
            DrawBatchTextureType(assets);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.PushStyleColor(ImGuiCol.Button, new SysVec4(0.55f, 0.20f, 0.16f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new SysVec4(0.68f, 0.26f, 0.20f, 1f));
        if (ImGui.Button($"{EditorIcons.Delete}  Delete {assets.Count} Assets", new SysVec2(-1, 0)))
            AssetOps.DeleteAssets(state, assets);
        ImGui.PopStyleColor(2);
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
        ImGui.SetNextItemWidth(-1);
        if (ImGui.Combo("##batchtextype", ref index, names, names.Length) && index >= 0) {
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

        ImGui.EndTable();
    }

    // ---- Asset inspector -----------------------------------------------------

    void DrawAssetInspector() {
        var path = state.SelectedAssetPath;
        Guid guid = state.SelectedAssetGuid;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        AssetDatabase.TryGetMeta(guid, out MetaFile meta);

        DrawAssetHeader(path, ext, meta);
        ImGui.Spacing();

        switch (ext) {
            case ".png" or ".jpg" or ".jpeg" or ".tga" or ".bmp" or ".hdr" or ".exr":
                DrawTextureImportSettings(path, guid, meta);
                break;
            case ".mat":
                DrawMaterialEditor(path, guid);
                break;
            case ".volume":
                DrawVolumeProfileAsset(guid);
                break;
            case ".scene":
                if (ImGui.Button($"{EditorIcons.Play}  Open Scene", new SysVec2(-1, 0)))
                    OpenScene(path);
                break;
            case ".pyscene":
                ImGui.TextWrapped("Falcor scene. On import it generates a sibling .scene you can open.");
                break;
            case ".shader" or ".glsl" or ".cubemap":
                // Native text assets — show a hint but no noisy "unsupported" line.
                ImGui.TextDisabled("Edit this file in a text editor.");
                if (ImGui.Button($"{EditorIcons.FolderOpen}  Show in Explorer", new SysVec2(-1, 0)))
                    System.Diagnostics.Process.Start("explorer.exe",
                        $"/select,\"{AssetDatabase.Project.ResolveAbsolute(path)}\"");
                break;
            case ".prefab":
                DrawPrefabInspector(path);
                break;
            case ".asset":
                DrawDataAssetInspector(path);
                break;
            case ".wav" or ".wave" or ".ogg":
                DrawAudioClipAsset(path);
                break;
            case ".banim":
                DrawAnimationClipAsset(path);
                break;
            // Everything else (models, etc.): just the file header above — no clutter.
        }
    }

    // Audio asset view: a Preview/Stop button + clip stats, so you can audition a .wav/.ogg straight
    // from the asset browser without dropping it on an AudioSource. Same Audio facade as the component
    // preview (play-mode-independent; silent no-op with no audio device).
    void DrawAudioClipAsset(string path) {
        AudioClip clip = AssetDatabase.Load<AudioClip>(path);
        if (clip is null) {
            ImGui.TextDisabled("Could not load audio clip.");
            return;
        }

        ImGui.SeparatorText("Preview");
        bool playing = audioPreviewVoice is { IsPlaying: true };
        if (ImGui.Button(playing ? $"{EditorIcons.Pause}  Stop" : $"{EditorIcons.Play}  Play",
                new SysVec2(120, 0))) {
            audioPreviewVoice?.Stop();
            audioPreviewVoice = playing ? null : Audio.Play(clip);
        }
        ImGui.SameLine();
        ImGui.TextDisabled($"{clip.DurationSeconds:F1}s  -  {clip.Channels}ch  -  {clip.SampleRate} Hz");
        if (!Audio.IsAvailable)
            ImGui.TextDisabled("(no audio device on this machine - preview is silent)");
    }

    // Animation-clip asset view: clip stats. A skeletal pose preview needs a skinned mesh to drive,
    // which an asset-only view doesn't have - assign the clip to an Animator on a skinned entity and
    // use the Animator scrub. Here we just summarize the clip.
    void DrawAnimationClipAsset(string path) {
        AnimationClip clip = AssetDatabase.Load<AnimationClip>(path);
        if (clip is null) {
            ImGui.TextDisabled("Could not load animation clip.");
            return;
        }

        ImGui.SeparatorText("Animation");
        ImGui.TextDisabled($"Duration: {clip.DurationSeconds:F2}s");
        ImGui.TextDisabled($"Channels (animated bones): {clip.Data.Channels.Length}");
        ImGui.TextDisabled($"Ticks/sec: {clip.TicksPerSecond:F0}");
        ImGui.Spacing();
        ImGui.TextWrapped("Assign this clip to an Animator on a skinned mesh, then use the Animator's " +
            "scrub slider to preview the pose.");
    }

    // Prefab inspector: its captured entity tree (read-only) + an Instantiate-into-scene action.
    // The backend is capture/instantiate (no live instance overrides), so this views the asset and
    // plants copies; editing happens by instantiating, changing in the scene, and re-creating.
    void DrawPrefabInspector(string path) {
        PrefabAsset prefab = AssetDatabase.Load<PrefabAsset>(path);
        if (prefab is null) {
            ImGui.TextDisabled("Could not load prefab.");
            return;
        }

        if (ImGui.Button($"{EditorIcons.Add}  Instantiate into Scene", new SysVec2(-1, 0))) {
            EditorUndo.Push("Instantiate Prefab");
            Entity root = prefab.Instantiate();
            if (root is not null)
                state.Select(root);
            state.MarkViewportDirty();
        }

        ImGui.Spacing();
        ImGui.TextDisabled($"Contents ({prefab.Entities.Count} entit{(prefab.Entities.Count == 1 ? "y" : "ies")})");
        ImGui.Separator();
        foreach (var doc in prefab.Entities) {
            float indent = doc.Transform?.Parent is null ? 0 : 16f;
            if (indent > 0) ImGui.Indent(indent);
            ImGui.TextUnformatted($"{EditorIcons.Package}  {doc.Name}");
            if (indent > 0) ImGui.Unindent(indent);
        }
    }

    // The DataAsset (ScriptableObject-equivalent) currently being edited, cached so edits accumulate
    // on one instance; reloaded when the selected .asset path changes.
    string dataAssetPath;
    object dataAssetInstance;

    // DataAsset inspector: reflect the loaded instance through the SAME member list the component
    // inspector uses (honors [Range]/[Header]/[Tooltip]/[FoldoutGroup]/asset pickers). Edits write
    // straight back to the .asset file via DataAssetSerializer: an asset edit, not scene state, so NO
    // scene undo (the .volume edit-write-back pattern). Change is detected by a serialized-text diff.
    void DrawDataAssetInspector(string path) {
        if (dataAssetPath != path || dataAssetInstance is null) {
            dataAssetPath = path;
            dataAssetInstance = LoadDataAsset(path);
        }
        if (dataAssetInstance is not DataAsset asset) {
            ImGui.TextDisabled("Could not load data asset (unknown or renamed type?).");
            return;
        }

        string before = DataAssetSerializer.Serialize(asset);
        DrawMemberList(asset.GetType(), asset);
        string after = DataAssetSerializer.Serialize(asset);
        if (before != after)
            SaveDataAsset(path, asset);
    }

    static object LoadDataAsset(string path) {
        try { return AssetDatabase.Load<DataAsset>(path); }
        catch { return null; }
    }

    static void SaveDataAsset(string path, DataAsset instance) {
        try {
            File.WriteAllText(AssetDatabase.Project.ResolveAbsolute(path),
                DataAssetSerializer.Serialize(instance));
        }
        catch (Exception e) {
            Debugging.LogError($"Could not save data asset: {e.Message}");
        }
    }

    // Big type icon + bold file name + dim path/importer lines, divided from the body.
    static unsafe void DrawAssetHeader(string path, string ext, MetaFile meta) {
        (string icon, SysVec4 tint) = EditorIcons.ForAssetExtension(ext);
        var draw = ImGui.GetWindowDrawList();
        SysVec2 start = ImGui.GetCursorScreenPos();

        float iconSize = 36f;
        if (ImGuiController.HasIcons) {
            draw.AddText(ImGuiController.LargeIcons, iconSize, start + new SysVec2(0, 2),
                ImGui.GetColorU32(tint), icon);
            ImGui.SetCursorScreenPos(start + new SysVec2(iconSize + 10, 0));
        }

        ImGui.BeginGroup();
        draw.AddText(ImGuiController.Bold, ImGui.GetFontSize(), ImGui.GetCursorScreenPos(),
            ImGui.GetColorU32(ImGuiCol.Text), Path.GetFileName(path));
        ImGui.Dummy(new SysVec2(0, ImGui.GetTextLineHeight()));
        ImGui.TextDisabled(path);
        if (meta is not null)
            ImGui.TextDisabled(meta.Importer);
        ImGui.EndGroup();

        ImGui.Spacing();
        ImGui.Separator();
    }

    static void DrawTextureImportSettings(string path, Guid guid, MetaFile meta) {
        if (meta is null) {
            ImGui.TextDisabled("No import settings.");
            return;
        }

        if (BeginGrid("##texsettings")) {
            Row("Texture Type");
            TextureType current = TextureImporter.TypeFromSettings(meta.Settings);
            string[] names = Enum.GetNames<TextureType>();
            int index = Array.IndexOf(names, current.ToString());
            ImGui.SetNextItemWidth(-1);
            if (ImGui.Combo("##textype", ref index, names, names.Length)) {
                meta.Settings["textureType"] = names[index];
                meta.Save(MetaFile.PathFor(AssetDatabase.Project.ResolveAbsolute(path)));
                Guid reimported = guid;
                AsyncAssetImport.Request("Reimporting texture...",
                    onFinished: () => AssetDatabase.Invalidate(reimported));
            }
            ImGui.EndTable();
        }

        ImGui.Spacing();
        ImGui.TextDisabled("Changing the type reimports. Loaded materials keep the\nold instance until the scene reloads.");
    }

    // Material preview thumbnail state. Re-rendered only when the material (guid) or its serialized
    // content (hash) changes, so the GL pass runs once per edit, not per frame.
    Guid materialPreviewGuid;
    int materialPreviewHash;
    int materialPreviewTex;
    const int MaterialPreviewSize = 128;

    void DrawMaterialPreview(Guid guid, MaterialDefinition definition) {
        // DX12 editor: the material preview uses a GL FBO + GL texture, invalid for the DX12 ImGui backend.
        // Skip it (the inspector just omits the sphere). TEMPORARY — removed when the DX12 preview port lands.
        if (RenderBackendSelector.Selected == RenderBackend.Dx12)
            return;
        // cheap content fingerprint: re-render only when the serialized material changes
        int hash = System.Text.Json.JsonSerializer.Serialize(definition, PipelineJson.Options).GetHashCode();
        if (guid != materialPreviewGuid || hash != materialPreviewHash || materialPreviewTex == 0) {
            try {
                byte[] pixels = MaterialPreviewRenderer.Render(definition, MaterialPreviewSize);
                materialPreviewTex = UploadPreviewTexture(materialPreviewTex, pixels, MaterialPreviewSize);
                materialPreviewGuid = guid;
                materialPreviewHash = hash;
            }
            catch (Exception e) {
                Debugging.LogError($"Material preview failed: {e.Message}");
                materialPreviewTex = 0;
            }
        }

        if (materialPreviewTex != 0) {
            float size = 120f;
            float pad = (ImGui.GetContentRegionAvail().X - size) * 0.5f;
            if (pad > 0) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + pad);
            ImGui.Image(EditorApplication.Tex(materialPreviewTex), new SysVec2(size, size));
            ImGui.Spacing();
        }
    }

    // Uploads RGBA pixels into a (reused) GL texture and returns its id.
    static int UploadPreviewTexture(int existing, byte[] pixels, int size) {
        int tex = existing != 0 ? existing : OpenTK.Graphics.OpenGL4.GL.GenTexture();
        OpenTK.Graphics.OpenGL4.GL.BindTexture(OpenTK.Graphics.OpenGL4.TextureTarget.Texture2D, tex);
        OpenTK.Graphics.OpenGL4.GL.TexImage2D(OpenTK.Graphics.OpenGL4.TextureTarget.Texture2D, 0,
            OpenTK.Graphics.OpenGL4.PixelInternalFormat.Rgba, size, size, 0,
            OpenTK.Graphics.OpenGL4.PixelFormat.Rgba, OpenTK.Graphics.OpenGL4.PixelType.UnsignedByte, pixels);
        OpenTK.Graphics.OpenGL4.GL.TexParameter(OpenTK.Graphics.OpenGL4.TextureTarget.Texture2D,
            OpenTK.Graphics.OpenGL4.TextureParameterName.TextureMinFilter, (int)OpenTK.Graphics.OpenGL4.TextureMinFilter.Linear);
        OpenTK.Graphics.OpenGL4.GL.TexParameter(OpenTK.Graphics.OpenGL4.TextureTarget.Texture2D,
            OpenTK.Graphics.OpenGL4.TextureParameterName.TextureMagFilter, (int)OpenTK.Graphics.OpenGL4.TextureMagFilter.Linear);
        return tex;
    }

    void DrawMaterialEditor(string path, Guid guid) {
        var absolute = AssetDatabase.Project.ResolveAbsolute(path);
        MaterialDefinition definition;
        try {
            definition = PipelineJson.Read<MaterialDefinition>(absolute);
        }
        catch (Exception exception) {
            ImGui.TextDisabled($"Unreadable material: {exception.Message}");
            return;
        }

        // Unity-style preview sphere: render the material to a thumbnail (re-rendered only when the
        // material's serialized state changes), upload to a GL texture, show it centered.
        DrawMaterialPreview(guid, definition);

        ImGui.TextDisabled($"Shader: {definition.Shader ?? "(none)"}");
        ImGui.Spacing();

        var changed = false;
        if (BeginGrid("##matslots")) {
            foreach (TextureType slot in new[] {
                         TextureType.Diffuse, TextureType.Normal, TextureType.Metallic,
                         TextureType.Roughness, TextureType.AO, TextureType.Emissive,
                     }) {
                definition.Textures.TryGetValue(slot.ToString(), out var reference);
                var display = reference is null
                    ? "None"
                    : Path.GetFileName(ReferenceToPath(reference) ?? reference);

                Row(slot.ToString());
                ImGui.PushID((int)slot);
                if (reference is null)
                    ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
                ImGui.Button(display, new SysVec2(-1, 0));
                if (reference is null)
                    ImGui.PopStyleColor();
                if (AcceptGuidDrop(out Guid dropped)) {
                    definition.Textures[slot.ToString()] = AssetRef.FromGuid(dropped);
                    changed = true;
                }
                ImGui.PopID();
            }

            // Scalar material properties (stored in the .mat next to the texture refs).
            // Base color: linear RGBA tint multiplying the albedo map (glTF baseColorFactor).
            // White is the neutral "unstated" default, so it's stored as null and rendering
            // is bit-identical to a .mat without the key.
            Row("Base Color");
            var baseColor = definition.BaseColor switch {
                { Length: >= 4 } bc => new SysVec4(bc[0], bc[1], bc[2], bc[3]),
                { Length: 3 } bc => new SysVec4(bc[0], bc[1], bc[2], 1f),
                _ => SysVec4.One,
            };
            if (ImGui.ColorEdit4("##matbasecolor", ref baseColor)) {
                definition.BaseColor = baseColor == SysVec4.One
                    ? null
                    : [baseColor.X, baseColor.Y, baseColor.Z, baseColor.W];
                changed = true;
            }

            // Packed ORM: metallic texture carries (occlusion, roughness, metallic) in RGB.
            // Auto-detected from "spec" file names when the .mat doesn't say explicitly.
            Row("Packed ORM");
            var packedOrm = MaterialLoader.ResolvePackedOrm(definition);
            if (ImGui.Checkbox("##matpackedorm", ref packedOrm)) {
                definition.PackedOrm = packedOrm;
                changed = true;
            }

            // Alpha cutout: discard below 0.5 diffuse alpha + double-sided (foliage cards).
            // Auto-detected from foliage-style texture names when not set explicitly.
            Row("Alpha Cutout");
            var cutout = MaterialLoader.ResolveCutout(definition);
            if (ImGui.Checkbox("##matcutout", ref cutout)) {
                definition.Cutout = cutout;
                changed = true;
            }

            Row("Transparent");
            var transparent = definition.Transparent;
            if (ImGui.Checkbox("##mattransparent", ref transparent)) {
                definition.Transparent = transparent;
                changed = true;
            }

            if (definition.Transparent) {
                Row("Opacity");
                var opacity = definition.Opacity;
                if (ImGui.SliderFloat("##matopacity", ref opacity, 0f, 1f)) {
                    definition.Opacity = opacity;
                    changed = true;
                }
            }

            Row("Emissive Color");
            var emissive = definition.EmissiveColor is { Length: >= 3 } c
                ? new SysVec3(c[0], c[1], c[2])
                : SysVec3.One;
            if (ImGui.ColorEdit3("##matemissivecolor", ref emissive)) {
                definition.EmissiveColor = [emissive.X, emissive.Y, emissive.Z];
                changed = true;
            }

            Row("Emissive Intensity");
            var emissivemntensity = definition.EmissiveIntensity;
            if (ImGui.DragFloat("##matemissiveintensity", ref emissivemntensity, 0.05f, 0f, 100f)) {
                definition.EmissiveIntensity = emissivemntensity;
                changed = true;
            }

            ImGui.EndTable();
        }

        if (changed) {
            PipelineJson.Write(absolute, definition);
            ApplyLiveMaterial(guid, definition);
            state.MarkViewportDirty();
        }

        ImGui.Spacing();
        ImGui.TextDisabled("Drag textures from the Assets panel onto the slots.");
    }

    static string ReferenceToPath(string reference) =>
        AssetRef.IsGuidRef(reference, out Guid g) ? AssetDatabase.GuidToAssetPath(g) : reference;

    static void ApplyLiveMaterial(Guid materialGuid, MaterialDefinition definition) {
        var material = AssetDatabase.Load<Material>(materialGuid);
        if (material is null)
            return;

        material.Diffuse = LoadSlot(definition, TextureType.Diffuse) ?? material.Diffuse;
        material.Normal = LoadSlot(definition, TextureType.Normal);
        material.Metallic = LoadSlot(definition, TextureType.Metallic);
        material.Roughness = LoadSlot(definition, TextureType.Roughness);
        material.AO = LoadSlot(definition, TextureType.AO);
        material.Emissive = LoadSlot(definition, TextureType.Emissive);
        MaterialLoader.ApplyScalars(material, definition);
    }

    static Texture2D LoadSlot(MaterialDefinition definition, TextureType slot) =>
        definition.Textures.TryGetValue(slot.ToString(), out var reference) && reference is not null
            ? AssetDatabase.LoadRef<Texture2D>(reference)
            : null;

    static unsafe bool AcceptGuidDrop(out Guid guid) {
        guid = Guid.Empty;
        if (!ImGui.BeginDragDropTarget())
            return false;

        ImGuiPayloadPtr payload = ImGui.AcceptDragDropPayload(AssetBrowserPanel.DragType);
        var accepted = false;
        if (!payload.IsNull && payload.Data != null) {
            var text = System.Runtime.InteropServices.Marshal.PtrToStringAnsi((IntPtr)payload.Data, payload.DataSize);
            // Multi-select drags carry several GUIDs separated by ';' — a single slot takes the first.
            var first = text?.Split(';', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            accepted = Guid.TryParse(first, out guid);
        }

        ImGui.EndDragDropTarget();
        return accepted;
    }

    static void OpenScene(string assetPath) => SceneCommands.Open(assetPath);

    // ---- Layout helpers --------------------------------------------------------

    static bool BeginGrid(string id) {
        // PadOuterX keeps the value column off the panel edge; the slight indent (via a leading
        // column) and inner spacing give the rows a calmer, more deliberate rhythm.
        if (!ImGui.BeginTable(id, 2, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.PadOuterX))
            return false;
        ImGui.TableSetupColumn("label", ImGuiTableColumnFlags.WidthStretch, 0.38f);
        ImGui.TableSetupColumn("value", ImGuiTableColumnFlags.WidthStretch, 0.62f);
        return true;
    }

    // Starts a new label/value row and leaves the cursor in the value column.
    static void Row(string label) {
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(label);
        ImGui.TableSetColumnIndex(1);
        ImGui.SetNextItemWidth(-1);
    }

    // Like Row, but appends a "(?)" marker that shows the tooltip on hover (when one is supplied).
    static void RowWithTooltip(string label, string tooltip) {
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(label);
        // Tooltip on the LABEL itself (Unity-style: hover the field name), not just the "(?)" badge —
        // this is what made [Tooltip] feel "broken" (you had to find the tiny marker).
        if (tooltip is not null) {
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(tooltip);
            ImGui.SameLine(0, 4);
            ImGui.TextDisabled("(?)");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(tooltip);
        }
        ImGui.TableSetColumnIndex(1);
        ImGui.SetNextItemWidth(-1);
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
            ImGui.TableSetColumnIndex(0);
            string icon = locked ? EditorIcons.Lock : EditorIcons.LockOpen;
            float btn = ImGui.GetFrameHeight();
            ImGui.SameLine();
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - btn);
            if (EditorIcons.GhostButtonSmall($"ulock_{label}", icon,
                    locked ? "Proportions locked - editing one axis scales the others"
                           : "Lock proportions (uniform scaling)")) {
                uniformLocks[label] = !locked;
                locked = !locked;
            }
            ImGui.TableSetColumnIndex(1);
            ImGui.SetNextItemWidth(-1);
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
