using System.IO;
using System.Linq;
using BallisticEngine.AssetPipeline.Loaders;
using BallisticEngine.UI;
using Hexa.NET.ImGui;
using SysVec2 = System.Numerics.Vector2;
using SysVec4 = System.Numerics.Vector4;

namespace BallisticEngine.Editor.Inspector.Preview;

// The per-component preview sections (editor-rework Rule 1 / Phase B1), one self-registering
// IComponentPreview each. These REPLACE the `if (behaviour is Renderer/Volume/Terrain/...) DrawXxxSection`
// instanceof chain that used to live inline in InspectorPanel.DrawComponent. Discovery is by
// [ComponentPreview] (engine attribute) via TypeCache; order is deterministic by priority then type name
// (DeterministicResolver). The previews are stateless — per-section preview state stays as statics on
// InspectorPanel — so the registry keeps a single shared instance per class.
//
// RW1.1 (chunk 43): the section BODIES for Renderer/Health/TrailRenderer now LIVE HERE (moved out of the
// InspectorPanel god-panel) — phase B only moved the DISPATCH to this registry, leaving the bodies behind
// under an explicit "later chunk" contract; this is that chunk. The relocated bodies are byte-identical to
// the old inline call: they reach the panel's private EditorState through ctx.Panel.MarkViewportDirty() and
// the shared grid/row helpers (InspectorPanel.BeginGrid / .Row, widened to internal static for exactly this).
// The remaining shims (Volume/Terrain/AudioSource/Animator/...) still delegate back into InspectorPanel
// section methods via the context — RW1.2+ migrate those bodies the same way.

// Renderer: per-submesh material slots (Unity's Materials list). Each submesh of a multi-material mesh
// gets a real EDITABLE asset slot (drag-drop + picker) bound to a per-submesh override; long lists go in a
// scrolling box. (Replaced the old read-only label list, capped at 24, in 2026-06-18.)
[ComponentPreview(typeof(Renderer))]
internal sealed class RendererPreview : IComponentPreview {
    public void Draw(in ComponentPreviewContext ctx) {
        var renderer = (Renderer)ctx.Behaviour;
        DrawSubMeshMaterials(renderer, ctx.Panel);
    }

    // Multi-material meshes carry a baked material ref per submesh (the .mat the importer generated). Each
    // submesh gets an EDITABLE slot bound to a per-submesh override (Renderer.sharedMaterials); a null
    // override inherits the baked material, assigning one overrides just that submesh.
    static void DrawSubMeshMaterials(Renderer renderer, InspectorPanel panel) {
        Mesh mesh = renderer.SharedMesh;
        if (mesh?.SubMeshes is not { Length: > 1 } subMeshes)
            return;

        // A single-submesh renderer (one entity per source part) shows just its own slot.
        int only = renderer.SubMeshIndex;
        if (only >= 0 && only < subMeshes.Length) {
            EditorDecoration.DrawSectionHeader("Material");
            DrawSlotRow(renderer, panel, subMeshes[only], only);
            return;
        }

        // Whole-mesh renderers of split imports can have hundreds of submeshes; keep the rows in a scrolling
        // box (every slot reachable, no silent truncation) rather than capping the visible count.
        EditorDecoration.DrawSectionHeader($"Materials ({subMeshes.Length})");
        const int ScrollThreshold = 8;
        bool scroll = subMeshes.Length > ScrollThreshold;
        if (scroll) {
            float rowH = ImGui.GetFrameHeightWithSpacing() + ImGui.GetTextLineHeightWithSpacing();
            ImGui.BeginChild("##submatscroll", new SysVec2(0, Math.Min(10, subMeshes.Length) * rowH),
                ImGuiChildFlags.Borders);
        }
        for (var i = 0; i < subMeshes.Length; i++)
            DrawSlotRow(renderer, panel, subMeshes[i], i);
        if (scroll)
            ImGui.EndChild();
    }

    // One submesh row: the submesh name as a label, then its editable material-override slot below it. A null
    // override inherits the material baked into the mesh; assigning one overrides just that submesh.
    static void DrawSlotRow(Renderer renderer, InspectorPanel panel, SubMeshData sub, int i) {
        ImGui.PushID(i);
        string label = string.IsNullOrEmpty(sub.Name) ? $"Submesh {i}" : sub.Name;
        ImGui.TextUnformatted(label);
        if (ImGui.IsItemHovered() && !string.IsNullOrEmpty(sub.MaterialRef))
            ImGui.SetTooltip($"{label}\nBaked: {sub.MaterialRef}");

        Material baked = string.IsNullOrEmpty(sub.MaterialRef) ? null
            : AssetDatabase.LoadRef<Material>(sub.MaterialRef);
        panel.DrawSubMeshMaterialSlot(renderer, i, baked);
        ImGui.PopID();
    }
}

// Inline profile editing under a Volume component, Unity-style: the profile's overrides are
// edited in place (and saved straight back to the .volume asset), or a fresh profile asset
// can be created and assigned in one click. Body moved here in RW1.3.
[ComponentPreview(typeof(Volume))]
internal sealed class VolumePreview : IComponentPreview {
    // Volume-profile undo bookkeeping: the snapshot from before the current drag began, and the
    // last settled (clean) snapshot to use as its baseline.
    static object volumeUndoBefore;
    static object volumeUndoLastClean;

    public void Draw(in ComponentPreviewContext ctx) {
        var entity = ctx.Entity;
        var volume = (Volume)ctx.Behaviour;
        InspectorPanel panel = ctx.Panel;
        ImGui.Spacing();

        if (volume.Profile is null) {
            if (ImGui.Button($"{EditorIcons.Add}  New Profile", new SysVec2(-1, 0)))
                CreateProfileAsset(entity, volume);
            ImGui.TextDisabled("Creates a .volume asset and assigns it.");
            return;
        }

        EditorDecoration.DrawSectionHeader("Overrides");
        // UNDO for volume-profile edits (bug 2b): the profile is a .volume ASSET, outside scene-undo.
        // Snapshot before drawing; if a parameter changed, push a callback undo step when the edit
        // SETTLES (no item active) so a slider drag is one entry, not hundreds. The before-snapshot is
        // captured at the start of a drag (the frame the change first appears) and held until release.
        object beforeSnap = VolumeProfileEditor.Snapshot(volume.Profile);
        if (VolumeProfileEditor.Draw(volume.Profile)) {
            VolumeProfileEditor.SaveToAsset(volume.Profile);
            panel.MarkViewportDirty();

            VolumeProfile prof = volume.Profile;
            // Remember the state from BEFORE this drag started (first changed frame).
            volumeUndoBefore ??= volumeUndoLastClean;
            volumeUndoBefore ??= beforeSnap;

            // Commit one undo step when the interaction ends (mouse released / instantaneous widget).
            // F2: route the .volume ASSET edit through the EditorCommands.EditAsset choke point (which is
            // EditorUndo.PushCallback under the hood) so every asset edit shares one undo entry point. The
            // edit already happened during VolumeProfileEditor.Draw above, so the mutate step is a no-op --
            // EditAsset only records the before/after revert pair here. Byte-identical to the prior
            // PushCallback (same label, same applyOld/applyNew closures).
            if (!ImGui.IsAnyItemActive()) {
                object before = volumeUndoBefore;
                object after = VolumeProfileEditor.Snapshot(prof);
                EditorCommands.EditAsset("Edit Volume Override",
                    applyOld: () => { VolumeProfileEditor.Restore(prof, before); VolumeProfileEditor.SaveToAsset(prof); panel.MarkViewportDirty(); },
                    applyNew: () => { VolumeProfileEditor.Restore(prof, after); VolumeProfileEditor.SaveToAsset(prof); panel.MarkViewportDirty(); },
                    mutate: () => { });
                volumeUndoBefore = null;
            }
        }
        else if (!ImGui.IsAnyItemActive()) {
            // Idle: this clean snapshot is the "before" for the next edit.
            volumeUndoLastClean = beforeSnap;
            volumeUndoBefore = null;
        }
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
        // Single-entity edit (the Volume's own entity is reachable), so scope it to that entity --
        // PushEntity restores just it in place (Push->PushEntity scoping aside, byte-identical). The
        // snapshot still fires inside the deferred callback right before the mutate.
        AsyncAssetImport.Request("Importing profile...", onFinished: () => {
            EditorCommands.EditEntity(entity, "Assign Profile",
                () => volume.Profile = AssetDatabase.Load<VolumeProfile>(assetPath));
        });
    }
}

// Terrain sculpting palette: a Sculpt toggle that arms the Scene-view brush, the brush mode, and
// radius/strength (and a target height for Flatten/Set). Drives TerrainTool's static state; the
// actual sculpting happens in the viewport. Not part of scene undo — brush settings are editor
// tool state, and each stroke pushes its own undo + saves the .terrain asset. Body moved here in RW1.3.
[ComponentPreview(typeof(Terrain))]
internal sealed class TerrainPreview : IComponentPreview {
    public void Draw(in ComponentPreviewContext ctx) => DrawTerrainBrushSection((Terrain)ctx.Behaviour);

    static void DrawTerrainBrushSection(Terrain terrain) {
        ImGui.Spacing();

        if (terrain.Terrain3D is null) {
            ImGui.TextDisabled("Assign a Terrain asset to sculpt (or create one: Assets > New Terrain).");
            TerrainTool.Armed = false;
            return;
        }

        EditorDecoration.DrawSectionHeader("Sculpt");

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
}

// AudioSource preview: a Preview/Stop button so you can hear a clip without entering play mode.
// Uses the static Audio facade (play-mode-independent), so it works in edit mode; AudioSource.Play
// itself is gated to play mode. Graceful no-op when no audio device is present (headless CI).
// Body moved here in RW1.3 — the audioPreviewVoice/audioPreviewTime statics stay on InspectorPanel
// (shared with the .wav asset-clip preview DrawAudioClipAsset, an RW1.4 body) and are reached here.
[ComponentPreview(typeof(AudioSource))]
internal sealed class AudioSourcePreview : IComponentPreview {
    public void Draw(in ComponentPreviewContext ctx) {
        var source = (AudioSource)ctx.Behaviour;
        EditorDecoration.DrawSectionHeader("Preview");

        if (source.Clip is null) {
            ImGui.TextDisabled("Assign a Clip to preview.");
            return;
        }

        bool playing = InspectorPanel.audioPreviewVoice is { IsPlaying: true };
        if (ImGui.Button(playing ? $"{EditorIcons.Pause}  Stop" : $"{EditorIcons.Play}  Preview",
                new SysVec2(120, 0))) {
            InspectorPanel.audioPreviewVoice?.Stop();
            InspectorPanel.audioPreviewVoice = playing
                ? null
                : Audio.Play(source.Clip, source.Volume, source.Pitch, loop: false);
            playing = !playing;
        }
        ImGui.SameLine();
        ImGui.TextDisabled($"{source.Clip.DurationSeconds:F1}s, {source.Clip.Channels}ch, {source.Clip.SampleRate}Hz");

        EditorWidgets.AudioScrubber(source.Clip, source.Volume, source.Pitch,
            ref InspectorPanel.audioPreviewVoice, ref InspectorPanel.audioPreviewTime, ctx.Panel.MarkViewportDirty);

        if (!Audio.IsAvailable)
            ImGui.TextDisabled("(no audio device on this machine — preview is silent)");
    }
}

// Animator preview: a play/pause toggle + a scrub slider that evaluates the clip in edit mode, so
// you can pose the skinned character without entering play. Drives Animator.EvaluatePreview, which
// runs the same sample->skeleton->skinning pipeline as play-mode Tick. Body moved here in RW1.2.
[ComponentPreview(typeof(Animator))]
internal sealed class AnimatorPreview : IComponentPreview {
    static bool animatorPreviewPlaying;
    static float animatorPreviewTime;

    public void Draw(in ComponentPreviewContext ctx) =>
        EditorWidgets.AnimatorScrubber((Animator)ctx.Behaviour, ref animatorPreviewTime, ref animatorPreviewPlaying,
            ctx.Panel.MarkViewportDirty);
}

// AnimatorController: a live view of the state machine. The graph is script-built (states +
// transitions are wired in OnBegin), so this is a runtime DEBUG/DRIVE surface — it lists the states
// with the current one highlighted, and renders a poker for each declared parameter (checkbox for
// bool, slider for float/int, a button for triggers) so you can drive the graph from the inspector
// in play mode without writing test code (very AI-managed-friendly: set "Speed" and watch it cross
// from idle->walk->run live). Body moved here in RW1.2.
[ComponentPreview(typeof(AnimatorController))]
internal sealed class AnimatorControllerPreview : IComponentPreview {
    public void Draw(in ComponentPreviewContext ctx) {
        var controller = (AnimatorController)ctx.Behaviour;
        EditorDecoration.DrawSectionHeader("State Machine");

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
        ImGui.TextColored(EditorTheme.Info, cur);

        // State list with the active one highlighted.
        ImGui.Spacing();
        ImGui.TextDisabled($"States ({controller.StateCount})");
        foreach (AnimatorController.State s in controller.States) {
            bool isCurrent = s.Name == controller.CurrentStateName;
            string label = $"{(isCurrent ? EditorIcons.Play + " " : "   ")}{s.Name}";
            string clipName = s.Clip is not null ? s.Clip.Name : "(no clip)";
            if (isCurrent)
                ImGui.TextColored(EditorTheme.Info, $"{label}  ->  {clipName}");
            else
                ImGui.TextDisabled($"{label}  ->  {clipName}");
            // A click jumps to the state (play mode) — handy for testing.
            if (SceneManager.IsPlaying && ImGui.IsItemClicked())
                controller.Play(s.Name);
        }

        // Parameter pokers.
        var prms = controller.Parameters;
        if (prms.Count > 0) {
            EditorDecoration.DrawSectionHeader("Parameters");
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
            ctx.Panel.MarkViewportDirty(); // keep repainting so transitions show live
    }
}

// LightAnimator: a live preview toggle that animates the light IN EDIT MODE (so you can dial in a
// flicker/pulse without entering play), plus a warning if there's no light on the entity to drive.
// The IntensityCurve / ColorOverTime members render their curve+gradient widgets automatically via
// the reflection DrawMember, so this only adds the preview control. Body moved here in RW1.2.
[ComponentPreview(typeof(LightAnimator))]
internal sealed class LightAnimatorPreview : IComponentPreview {
    static bool lightAnimPreview;
    static float lightAnimPreviewClock;

    public void Draw(in ComponentPreviewContext ctx) {
        var lightAnim = (LightAnimator)ctx.Behaviour;
        EditorDecoration.DrawSectionHeader("Preview");

        bool hasLight = lightAnim.GetComponent<PointLight>() is not null
                     || lightAnim.GetComponent<SpotLight>() is not null;
        if (!hasLight) {
            ImGui.TextColored(EditorTheme.Warning, "No PointLight or SpotLight on this entity.");
            ImGui.TextDisabled("Add one — the animator drives its Intensity + Color.");
            return;
        }

        if (ImGui.Button(lightAnimPreview ? $"{EditorIcons.Pause}  Stop Preview" : $"{EditorIcons.Play}  Preview",
                new SysVec2(140, 0))) {
            lightAnimPreview = !lightAnimPreview;
            if (lightAnimPreview) lightAnimPreviewClock = 0f;
            else { lightAnim.RestoreBase(); ctx.Panel.MarkViewportDirty(); } // un-dim the light when stopping
        }
        ImGui.SameLine();
        ImGui.TextDisabled(lightAnim.Animation.ToString());

        // Drive the light in edit mode along its own preview clock (play mode runs Tick itself).
        if (lightAnimPreview && !SceneManager.IsPlaying) {
            lightAnimPreviewClock += (float)Time.DeltaTime;
            lightAnim.Apply(lightAnimPreviewClock);
            ctx.Panel.MarkViewportDirty();
        }
    }
}

// Spawner: live alive/pooled counts + a manual Spawn One / Clear. Spawning only runs in play mode
// (Tick), so the manual button is most useful there; in edit mode it instantiates immediately so
// you can preview the prefab placement, and Clear cleans those up. Body moved here in RW1.2.
[ComponentPreview(typeof(Spawner))]
internal sealed class SpawnerPreview : IComponentPreview {
    public void Draw(in ComponentPreviewContext ctx) {
        var spawner = (Spawner)ctx.Behaviour;
        EditorDecoration.DrawSectionHeader("Spawner");

        if (spawner.Prefab is null) {
            ImGui.TextColored(EditorTheme.Warning, "Assign a Prefab to spawn.");
            return;
        }

        ImGui.Text($"Alive: {spawner.AliveCount} / {spawner.MaxAlive}");
        ImGui.SameLine();
        ImGui.TextDisabled($"(pooled: {spawner.PooledCount})");

        if (ImGui.Button($"{EditorIcons.Play}  Spawn One", new SysVec2(120, 0))) {
            spawner.Spawn();
            ctx.Panel.MarkViewportDirty();
        }
        ImGui.SameLine();
        if (ImGui.Button($"{EditorIcons.Refresh}  Clear", new SysVec2(120, 0))) {
            spawner.Clear();
            ctx.Panel.MarkViewportDirty();
        }

        if (SceneManager.IsPlaying && spawner.AliveCount > 0)
            ctx.Panel.MarkViewportDirty(); // keep repainting while instances live/expire
    }
}

// Health: a live HP bar + edit-mode damage/heal/kill/revive test buttons. Body moved here in RW1.1.
[ComponentPreview(typeof(Health))]
internal sealed class HealthPreview : IComponentPreview {
    public void Draw(in ComponentPreviewContext ctx) {
        var health = (Health)ctx.Behaviour;
        EditorDecoration.DrawSectionHeader("Health");

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

        if (ImGui.Button("Damage 10", new SysVec2(90, 0))) { health.TakeDamage(10f); ctx.Panel.MarkViewportDirty(); }
        ImGui.SameLine();
        if (ImGui.Button("Heal 10", new SysVec2(90, 0))) { health.Heal(10f); ctx.Panel.MarkViewportDirty(); }
        ImGui.SameLine();
        if (ImGui.Button("Kill", new SysVec2(70, 0))) { health.Kill(); ctx.Panel.MarkViewportDirty(); }
        ImGui.SameLine();
        if (ImGui.Button("Revive", new SysVec2(70, 0))) { health.Revive(); ctx.Panel.MarkViewportDirty(); }

        if (!SceneManager.IsPlaying)
            ImGui.TextDisabled("Edit-mode tests don't fire DestroyOnDeath (play only).");
    }
}

// UIDocument's Uxml/Uss are string PATHS; give them drag-drop target fields so you can drop a
// .uxml/.uss (or .uihtml/.uss) asset from the browser instead of typing the address (item 15).
// Body moved here in RW1.3 — DrawPathDropField came along as a private static helper; the shared
// AcceptGuidDrop stays on InspectorPanel (used by 8 sites) and is reached as InspectorPanel.AcceptGuidDrop.
[ComponentPreview(typeof(UIDocument))]
internal sealed class UIDocumentPreview : IComponentPreview {
    public void Draw(in ComponentPreviewContext ctx) {
        var doc = (UIDocument)ctx.Behaviour;
        InspectorPanel panel = ctx.Panel;
        EditorDecoration.DrawSectionHeader("Markup & Style");
        DrawPathDropField(panel, "UXML (markup)", doc.Uxml, [".uxml", ".uihtml", ".html"], p => doc.Uxml = p);
        DrawPathDropField(panel, "USS (style)", doc.Uss, [".uss", ".uicss", ".css"], p => doc.Uss = p);
        ImGui.TextDisabled("Drag a markup/style asset here, or type its Assets/... path.");
    }

    // A text field for an asset PATH that also accepts a drag-drop of a matching-extension asset (sets
    // the field to the dropped asset's path). `exts` are the accepted extensions (lowercase, with dot).
    static void DrawPathDropField(InspectorPanel panel, string label, string current, string[] exts, Action<string> apply) {
        ImGui.PushID(label);
        ImGui.TextDisabled(label);
        var s = current ?? "";
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText("##path", ref s, 256)) {
            // `apply` is an opaque closure that may write any target (UIDocument paths etc.) and the
            // entity is not reachable here, so this stays a whole-scene structural snapshot.
            EditorCommands.Structural($"Edit {label}", () => { apply(s); panel.MarkViewportDirty(); });
        }
        // Drop target over the field: accept a single matching asset and write its path.
        if (InspectorPanel.AcceptGuidDrop(out Guid guid)) {
            string path = AssetDatabase.GuidToAssetPath(guid);
            if (path is not null && exts.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase))) {
                EditorCommands.Structural($"Assign {label}", () => { apply(path); panel.MarkViewportDirty(); });
            }
        }
        ImGui.PopID();
    }
}

// ParticleSystem preview: it already animates live in the editor (AdvanceAll runs every editor
// frame), so this just adds a Restart (clear) + a one-shot Emit test + a live count, and keeps the
// viewport repainting while particles are alive so you see the motion. Body moved here in RW1.2.
[ComponentPreview(typeof(ParticleSystem))]
internal sealed class ParticleSystemPreview : IComponentPreview {
    public void Draw(in ComponentPreviewContext ctx) {
        var particles = (ParticleSystem)ctx.Behaviour;
        EditorDecoration.DrawSectionHeader("Preview");

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
            ctx.Panel.MarkViewportDirty();
    }
}

// TrailRenderer: animates live in the editor; a Clear + a live point count. Body moved here in RW1.1.
[ComponentPreview(typeof(TrailRenderer))]
internal sealed class TrailRendererPreview : IComponentPreview {
    public void Draw(in ComponentPreviewContext ctx) {
        var trail = (TrailRenderer)ctx.Behaviour;
        EditorDecoration.DrawSectionHeader("Preview");

        if (ImGui.Button($"{EditorIcons.Refresh}  Clear", new SysVec2(-1, 0)))
            trail.Clear();
        ImGui.TextDisabled($"{trail.PointCount} points");

        if (trail.PointCount > 0)
            ctx.Panel.MarkViewportDirty();
    }
}
