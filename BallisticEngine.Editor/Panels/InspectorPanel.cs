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

    public InspectorPanel(EditorState state) => this.state = state;

    public void DrawContents() {
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
                ImGui.TextDisabled("Editing the active one; hierarchy actions apply to all.");
                ImGui.Separator();
                ImGui.Spacing();
            }
            DrawEntityInspector(state.Selected);
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

        DrawTagLayerRow(entity);

        ImGui.Spacing();

        DrawTransform(entity.transform);

        foreach (Behaviour behaviour in behaviours)
            DrawComponent(entity, behaviour);

        ImGui.Spacing();
        ImGui.Spacing();
        DrawAddComponent(entity);
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

        // Reserve the card's space in the layout.
        ImGui.SetCursorScreenPos(cardMin);
        ImGui.Dummy(new SysVec2(avail.X, cardH));
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

        // Right-click the header for Unity-style resets.
        if (ImGui.BeginPopupContextItem("##transformctx")) {
            if (ImGui.MenuItem("Reset Position")) { EditorUndo.Push("Reset Position"); transform.Position = Vector3.Zero; }
            if (ImGui.MenuItem("Reset Rotation")) { EditorUndo.Push("Reset Rotation"); transform.EulerAngles = Vector3.Zero; }
            if (ImGui.MenuItem("Reset Scale")) { EditorUndo.Push("Reset Scale"); transform.Scale = Vector3.One; }
            ImGui.Separator();
            if (ImGui.MenuItem("Reset All")) {
                EditorUndo.Push("Reset Transform");
                transform.Position = Vector3.Zero;
                transform.EulerAngles = Vector3.Zero;
                transform.Scale = Vector3.One;
            }
            ImGui.EndPopup();
        }

        if (!open)
            return;

        if (BeginGrid("##transform")) {
            SysVec3Row("Position", transform.Position, v => transform.Position = v, 0.05f);
            SysVec3Row("Rotation", transform.EulerAngles, v => transform.EulerAngles = v, 0.5f);
            SysVec3Row("Scale", transform.Scale, v => transform.Scale = v, 0.05f);
            ImGui.EndTable();
        }

        ImGui.Spacing();
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

    void DrawComponent(Entity entity, Behaviour behaviour) {
        Type type = behaviour.GetType();
        ImGui.PushID(behaviour.InstanceId.GetHashCode());

        bool enabled = behaviour.IsEnabled;
        bool open = ComponentHeader(Prettify(type.Name), type, ref enabled, out bool menuRequested);
        if (enabled != behaviour.IsEnabled) { EditorUndo.Push($"Toggle {Prettify(type.Name)}"); behaviour.IsEnabled = enabled; state.MarkViewportDirty(); }

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
            ImGui.Separator();
            if (ImGui.MenuItem($"{EditorIcons.Delete}  Remove Component")) removeClicked = true;
            ImGui.EndPopup();
        }

        if (removeClicked) {
            EditorUndo.Push("Remove Component");
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
        if (VolumeProfileEditor.Draw(volume.Profile)) {
            VolumeProfileEditor.SaveToAsset(volume.Profile);
            // The viewport repaints on demand; without this a profile edit (toggle a component
            // Active, drag contrast/saturation, ...) saves but never shows — looked "broken".
            state.MarkViewportDirty();
        }
    }

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
        }
        ImGui.SameLine();
        ImGui.TextDisabled($"{source.Clip.DurationSeconds:F1}s, {source.Clip.Channels}ch, {source.Clip.SampleRate}Hz");

        if (!Audio.IsAvailable)
            ImGui.TextDisabled("(no audio device on this machine — preview is silent)");
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

    // ParticleSystem preview: it already animates live in the editor (AdvanceAll runs every editor
    // frame), so this just adds a Restart (clear) + a one-shot Emit test + a live count, and keeps the
    // viewport repainting while particles are alive so you see the motion.
    void DrawParticleSystemSection(ParticleSystem particles) {
        ImGui.Spacing();
        ImGui.SeparatorText("Preview");

        if (ImGui.Button($"{EditorIcons.Refresh}  Restart", new SysVec2(110, 0)))
            particles.Clear();
        ImGui.SameLine();
        if (ImGui.Button($"{EditorIcons.Play}  Emit 50", new SysVec2(110, 0)))
            particles.Emit(50);
        ImGui.SameLine();
        ImGui.TextDisabled($"{particles.LiveCount} live");

        if (particles.LiveCount > 0)
            state.MarkViewportDirty();
    }

    // TrailRenderer preview: also animates live in the editor; add a Clear + a live point count.
    void DrawTrailRendererSection(TrailRenderer trail) {
        ImGui.Spacing();
        ImGui.SeparatorText("Preview");

        if (ImGui.Button($"{EditorIcons.Refresh}  Clear", new SysVec2(110, 0)))
            trail.Clear();
        ImGui.SameLine();
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
    }

    void DrawMember(MemberInfo member, object target, MemberAttributes attrs) {
        Type memberType = ComponentReflection.MemberType(member);
        object value = ComponentReflection.GetValue(member, target);

        RowWithTooltip(Prettify(member.Name), attrs.Tooltip?.Text);
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
                        ComponentReflection.SetValue(member, target, f);
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
                        ComponentReflection.SetValue(member, target, i);
                        state.MarkViewportDirty();
                    }
                    break;
                }
                case bool b: {
                    var changed = InspectorUndo.Track(label, ImGui.Checkbox("##v", ref b));
                    if (changed) { ComponentReflection.SetValue(member, target, b); state.MarkViewportDirty(); }
                    break;
                }
                case string s: {
                    var str = s ?? "";
                    var changed = InspectorUndo.Track(label, ImGui.InputText("##v", ref str, 256));
                    if (changed) { ComponentReflection.SetValue(member, target, str); state.MarkViewportDirty(); }
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
                    if (changed) { ComponentReflection.SetValue(member, target, new Vector3(sv.X, sv.Y, sv.Z)); state.MarkViewportDirty(); }
                    break;
                }
                case Vector2 v2: {
                    var sv = new SysVec2(v2.X, v2.Y);
                    var changed = InspectorUndo.Track(label, ImGui.DragFloat2("##v", ref sv, 0.05f));
                    if (changed) { ComponentReflection.SetValue(member, target, new Vector2(sv.X, sv.Y)); state.MarkViewportDirty(); }
                    break;
                }
                case Enum e: {
                    string[] names = Enum.GetNames(memberType);
                    int current = Array.IndexOf(names, e.ToString());
                    var changed = InspectorUndo.Track(label, ImGui.Combo("##v", ref current, names, names.Length));
                    if (changed) { ComponentReflection.SetValue(member, target, Enum.Parse(memberType, names[current])); state.MarkViewportDirty(); }
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
            state.SelectAsset(path, guid); // pin the referenced asset in the Inspector
        if (AcceptGuidDrop(out Guid d1))
            AssignAsset(member, target, assetType, d1);
        if (ImGui.IsItemHovered() && path is not null)
            ImGui.SetTooltip(path);

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
            ComponentReflection.SetValue(member, target, loaded);
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
            entity.AddComponent(e.Type);
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

        // The selection, capped so huge selections stay readable.
        const int maxListed = 12;
        for (var i = 0; i < assets.Count && i < maxListed; i++) {
            (string path, _) = assets[i];
            (string icon, SysVec4 tint) = EditorIcons.ForAssetExtension(Path.GetExtension(path).ToLowerInvariant());
            ImGui.TextDisabled($"      {Path.GetFileName(path)}");
            EditorIcons.DrawAt(ImGui.GetItemRectMin() + new SysVec2(4, 0), icon, tint);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(path);
        }
        if (assets.Count > maxListed)
            ImGui.TextDisabled($"      ... and {assets.Count - maxListed} more");

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
        if (tooltip is not null) {
            ImGui.SameLine(0, 4);
            ImGui.TextDisabled("(?)");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(tooltip);
        }
        ImGui.TableSetColumnIndex(1);
        ImGui.SetNextItemWidth(-1);
    }

    void SysVec3Row(string label, Vector3 value, Action<Vector3> apply, float speed) {
        Row(label);
        var sv = new SysVec3(value.X, value.Y, value.Z);
        if (AxisVec3(label, label, ref sv, speed)) {
            apply(new Vector3(sv.X, sv.Y, sv.Z));
            state.MarkViewportDirty();
        }
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
