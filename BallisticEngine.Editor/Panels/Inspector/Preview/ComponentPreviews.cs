using BallisticEngine.AssetPipeline.Loaders;
using BallisticEngine.UI;
using SysVec2 = System.Numerics.Vector2;
using SysVec4 = System.Numerics.Vector4;
using static BallisticEngine.Editor.Inspector.Preview.ComponentPreviewGuiAccess;

namespace BallisticEngine.Editor.Inspector.Preview;

internal static class ComponentPreviewGuiAccess {
    internal static IEditorGui gui => EditorGui.Shared;
}

[ComponentPreview(typeof(Renderer))]
internal sealed class RendererPreview : IComponentPreview {
    public void Draw(in ComponentPreviewContext ctx) {
        var renderer = (Renderer)ctx.Behaviour;
        DrawSubMeshMaterials(renderer, ctx.Panel);
    }

    static void DrawSubMeshMaterials(Renderer renderer, InspectorPanel panel) {
        Mesh mesh = renderer.SharedMesh;
        if (mesh?.SubMeshes is not { Length: > 1 } subMeshes)
            return;

        int only = renderer.SubMeshIndex;
        if (only >= 0 && only < subMeshes.Length) {
            EditorDecoration.DrawSectionHeader("Material");
            DrawSlotRow(renderer, panel, subMeshes[only], only);
            return;
        }

        EditorDecoration.DrawSectionHeader($"Materials ({subMeshes.Length})");
        const int ScrollThreshold = 8;
        bool scroll = subMeshes.Length > ScrollThreshold;
        if (scroll) {
            float rowH = gui.FrameHeightWithSpacing + gui.TextLineHeightWithSpacing;
            gui.BeginChild("##submatscroll", new SysVec2(0, Math.Min(10, subMeshes.Length) * rowH),
                border: true);
        }
        for (var i = 0; i < subMeshes.Length; i++)
            DrawSlotRow(renderer, panel, subMeshes[i], i);
        if (scroll)
            gui.EndChild();
    }

    static void DrawSlotRow(Renderer renderer, InspectorPanel panel, SubMeshData sub, int i) {
        gui.PushId(i);
        string label = string.IsNullOrEmpty(sub.Name) ? $"Submesh {i}" : sub.Name;
        gui.TextUnformatted(label);
        if (gui.IsItemHovered() && !string.IsNullOrEmpty(sub.MaterialRef))
            gui.Tooltip($"{label}\nBaked: {sub.MaterialRef}");

        Material baked = string.IsNullOrEmpty(sub.MaterialRef) ? null
            : AssetDatabase.LoadRef<Material>(sub.MaterialRef);
        panel.DrawSubMeshMaterialSlot(renderer, i, baked);
        gui.PopId();
    }
}

[ComponentPreview(typeof(Volume))]
internal sealed class VolumePreview : IComponentPreview {
    static object volumeUndoBefore;
    static object volumeUndoLastClean;

    public void Draw(in ComponentPreviewContext ctx) {
        var entity = ctx.Entity;
        var volume = (Volume)ctx.Behaviour;
        InspectorPanel panel = ctx.Panel;
        gui.Spacing();

        if (volume.Profile is null) {
            if (gui.Button($"{EditorIcons.Add}  New Profile", new SysVec2(-1, 0)))
                CreateProfileAsset(entity, volume);
            gui.TextDisabled("Creates a .volume asset and assigns it.");
            return;
        }

        EditorDecoration.DrawSectionHeader("Overrides");
        object beforeSnap = VolumeProfileEditor.Snapshot(volume.Profile);
        if (VolumeProfileEditor.Draw(volume.Profile)) {
            VolumeProfileEditor.SaveToAsset(volume.Profile);
            panel.MarkViewportDirty();

            VolumeProfile prof = volume.Profile;
            volumeUndoBefore ??= volumeUndoLastClean;
            volumeUndoBefore ??= beforeSnap;

            if (!gui.IsAnyItemActive()) {
                object before = volumeUndoBefore;
                object after = VolumeProfileEditor.Snapshot(prof);
                EditorCommands.EditAsset("Edit Volume Override",
                    applyOld: () => { VolumeProfileEditor.Restore(prof, before); VolumeProfileEditor.SaveToAsset(prof); panel.MarkViewportDirty(); },
                    applyNew: () => { VolumeProfileEditor.Restore(prof, after); VolumeProfileEditor.SaveToAsset(prof); panel.MarkViewportDirty(); },
                    mutate: () => { });
                volumeUndoBefore = null;
            }
        }
        else if (!gui.IsAnyItemActive()) {
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

        AsyncAssetImport.Request("Importing profile...", onFinished: () => {
            EditorCommands.EditEntity(entity, "Assign Profile",
                () => volume.Profile = AssetDatabase.Load<VolumeProfile>(assetPath));
        });
    }
}

[ComponentPreview(typeof(Terrain))]
internal sealed class TerrainPreview : IComponentPreview {
    public void Draw(in ComponentPreviewContext ctx) => DrawTerrainBrushSection((Terrain)ctx.Behaviour);

    static void DrawTerrainBrushSection(Terrain terrain) {
        gui.Spacing();

        if (terrain.Terrain3D is null) {
            gui.TextDisabled("Assign a Terrain asset to sculpt (or create one: Assets > New Terrain).");
            TerrainTool.Armed = false;
            return;
        }

        EditorDecoration.DrawSectionHeader("Sculpt");

        bool armed = TerrainTool.Armed;
        if (gui.Checkbox("Enable Brush", ref armed))
            TerrainTool.Armed = armed;
        if (gui.IsItemHovered())
            gui.Tooltip("Left-drag in the Scene view to sculpt. While on, clicks paint instead of selecting.");

        if (!armed)
            return;

        string[] modes = ["Raise", "Lower", "Smooth", "Flatten", "Set"];
        int mode = (int)TerrainTool.Brush;
        gui.SetNextItemWidth(-1);
        if (gui.Combo("##terrainbrush", ref mode, modes))
            TerrainTool.Brush = (TerrainSculpt.Brush)mode;

        float radius = TerrainTool.Radius;
        if (gui.SliderFloat("Radius", ref radius, 0.5f, 60f, "%.1f"))
            TerrainTool.Radius = radius;

        float strength = TerrainTool.Strength;
        if (gui.SliderFloat("Strength", ref strength, 0.01f, 2f, "%.2f"))
            TerrainTool.Strength = strength;

        if (TerrainTool.Brush is TerrainSculpt.Brush.Flatten or TerrainSculpt.Brush.Set) {
            float target = TerrainTool.TargetHeight;
            if (gui.SliderFloat("Target Height", ref target, 0f, 1f, "%.2f"))
                TerrainTool.TargetHeight = target;
            if (gui.IsItemHovered())
                gui.Tooltip("Normalized height (x HeightScale) the brush levels toward.");
        }

        gui.TextDisabled("Pick Lower to dig; Smooth/Flatten to level.");
    }
}

[ComponentPreview(typeof(AudioSource))]
internal sealed class AudioSourcePreview : IComponentPreview {
    public void Draw(in ComponentPreviewContext ctx) {
        var source = (AudioSource)ctx.Behaviour;
        EditorDecoration.DrawSectionHeader("Preview");

        if (source.Clip is null) {
            gui.TextDisabled("Assign a Clip to preview.");
            return;
        }

        bool playing = InspectorPanel.audioPreviewVoice is { IsPlaying: true };
        if (gui.Button(playing ? $"{EditorIcons.Pause}  Stop" : $"{EditorIcons.Play}  Preview",
                new SysVec2(120, 0))) {
            InspectorPanel.audioPreviewVoice?.Stop();
            InspectorPanel.audioPreviewVoice = playing
                ? null
                : Audio.Play(source.Clip, source.Volume, source.Pitch, loop: false);
            playing = !playing;
        }
        gui.SameLine();
        gui.TextDisabled($"{source.Clip.DurationSeconds:F1}s, {source.Clip.Channels}ch, {source.Clip.SampleRate}Hz");

        EditorWidgets.AudioScrubber(source.Clip, source.Volume, source.Pitch,
            ref InspectorPanel.audioPreviewVoice, ref InspectorPanel.audioPreviewTime, ctx.Panel.MarkViewportDirty);

        if (!Audio.IsAvailable)
            gui.TextDisabled("(no audio device on this machine — preview is silent)");
    }
}

[ComponentPreview(typeof(Animator))]
internal sealed class AnimatorPreview : IComponentPreview {
    static bool animatorPreviewPlaying;
    static float animatorPreviewTime;

    public void Draw(in ComponentPreviewContext ctx) =>
        EditorWidgets.AnimatorScrubber((Animator)ctx.Behaviour, ref animatorPreviewTime, ref animatorPreviewPlaying,
            ctx.Panel.MarkViewportDirty);
}

[ComponentPreview(typeof(AnimatorController))]
internal sealed class AnimatorControllerPreview : IComponentPreview {
    public void Draw(in ComponentPreviewContext ctx) {
        var controller = (AnimatorController)ctx.Behaviour;
        EditorDecoration.DrawSectionHeader("State Machine");

        if (controller.StateCount == 0) {
            gui.TextDisabled("No states. Build the graph in a script's OnBegin:");
            gui.TextDisabled("  AddState(name, clip); state.To(target, param, Compare, ...)");
            return;
        }

        if (!SceneManager.IsPlaying)
            gui.TextDisabled("Enter play mode to drive the graph.");

        string cur = controller.CurrentStateName ?? "(none)";
        gui.Text("Current: ");
        gui.SameLine();
        gui.TextColored(EditorTheme.Info, cur);

        gui.Spacing();
        gui.TextDisabled($"States ({controller.StateCount})");
        foreach (AnimatorController.State s in controller.States) {
            bool isCurrent = s.Name == controller.CurrentStateName;
            string label = $"{(isCurrent ? EditorIcons.Play + " " : "   ")}{s.Name}";
            string clipName = s.Clip is not null ? s.Clip.Name : "(no clip)";
            if (isCurrent)
                gui.TextColored(EditorTheme.Info, $"{label}  ->  {clipName}");
            else
                gui.TextDisabled($"{label}  ->  {clipName}");
            if (SceneManager.IsPlaying && gui.IsItemClicked())
                controller.Play(s.Name);
        }

        var prms = controller.Parameters;
        if (prms.Count > 0) {
            EditorDecoration.DrawSectionHeader("Parameters");
            foreach (var kv in prms) {
                string name = kv.Key;
                switch (kv.Value) {
                    case AnimatorController.ParamKind.Bool: {
                        bool b = controller.GetBool(name);
                        if (gui.Checkbox(name, ref b)) controller.SetBool(name, b);
                        break;
                    }
                    case AnimatorController.ParamKind.Trigger: {
                        if (gui.Button($"{EditorIcons.Play} {name}", new SysVec2(140, 0)))
                            controller.SetTrigger(name);
                        gui.SameLine();
                        gui.TextDisabled(controller.GetTrigger(name) ? "(set)" : "");
                        break;
                    }
                    case AnimatorController.ParamKind.Int: {
                        int iv = controller.GetInt(name);
                        if (gui.DragInt(name, ref iv)) controller.SetInt(name, iv);
                        break;
                    }
                    default: {
                        float fv = controller.GetFloat(name);
                        if (gui.DragFloat(name, ref fv, 0.05f)) controller.SetFloat(name, fv);
                        break;
                    }
                }
            }
        }

        if (SceneManager.IsPlaying)
            ctx.Panel.MarkViewportDirty();
    }
}

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
            gui.TextColored(EditorTheme.Warning, "No PointLight or SpotLight on this entity.");
            gui.TextDisabled("Add one — the animator drives its Intensity + Color.");
            return;
        }

        if (gui.Button(lightAnimPreview ? $"{EditorIcons.Pause}  Stop Preview" : $"{EditorIcons.Play}  Preview",
                new SysVec2(140, 0))) {
            lightAnimPreview = !lightAnimPreview;
            if (lightAnimPreview) lightAnimPreviewClock = 0f;
            else { lightAnim.RestoreBase(); ctx.Panel.MarkViewportDirty(); }
        }
        gui.SameLine();
        gui.TextDisabled(lightAnim.Animation.ToString());

        if (lightAnimPreview && !SceneManager.IsPlaying) {
            lightAnimPreviewClock += (float)Time.DeltaTime;
            lightAnim.Apply(lightAnimPreviewClock);
            ctx.Panel.MarkViewportDirty();
        }
    }
}

[ComponentPreview(typeof(Spawner))]
internal sealed class SpawnerPreview : IComponentPreview {
    public void Draw(in ComponentPreviewContext ctx) {
        var spawner = (Spawner)ctx.Behaviour;
        EditorDecoration.DrawSectionHeader("Spawner");

        if (spawner.Prefab is null) {
            gui.TextColored(EditorTheme.Warning, "Assign a Prefab to spawn.");
            return;
        }

        gui.Text($"Alive: {spawner.AliveCount} / {spawner.MaxAlive}");
        gui.SameLine();
        gui.TextDisabled($"(pooled: {spawner.PooledCount})");

        if (gui.Button($"{EditorIcons.Play}  Spawn One", new SysVec2(120, 0))) {
            spawner.Spawn();
            ctx.Panel.MarkViewportDirty();
        }
        gui.SameLine();
        if (gui.Button($"{EditorIcons.Refresh}  Clear", new SysVec2(120, 0))) {
            spawner.Clear();
            ctx.Panel.MarkViewportDirty();
        }

        if (SceneManager.IsPlaying && spawner.AliveCount > 0)
            ctx.Panel.MarkViewportDirty();
    }
}

[ComponentPreview(typeof(Health))]
internal sealed class HealthPreview : IComponentPreview {
    public void Draw(in ComponentPreviewContext ctx) {
        var health = (Health)ctx.Behaviour;
        EditorDecoration.DrawSectionHeader("Health");

        float frac = health.HealthFraction;
        IEditorDrawList draw = gui.WindowDrawList;
        SysVec2 p = gui.CursorScreenPos;
        float w = MathF.Max(gui.ContentRegionAvail.X, 60f);
        const float h = 18f;
        draw.AddRectFilled(p, p + new SysVec2(w, h), 0xFF202428, 3f);
        var barCol = gui.ColorU32(new SysVec4(1f - frac, frac, 0.12f, 1f));
        if (frac > 0f)
            draw.AddRectFilled(p, p + new SysVec2(w * frac, h), barCol, 3f);
        draw.AddRect(p, p + new SysVec2(w, h), 0xFF000000, 3f);
        string label = health.IsDead ? "DEAD" : $"{health.CurrentHealth:0} / {health.MaxHealth:0}";
        SysVec2 ts = gui.CalcTextSize(label);
        draw.AddText(p + new SysVec2((w - ts.X) * 0.5f, (h - ts.Y) * 0.5f), 0xFFFFFFFF, label);
        gui.Dummy(new SysVec2(w, h));

        if (gui.Button("Damage 10", new SysVec2(90, 0))) { health.TakeDamage(10f); ctx.Panel.MarkViewportDirty(); }
        gui.SameLine();
        if (gui.Button("Heal 10", new SysVec2(90, 0))) { health.Heal(10f); ctx.Panel.MarkViewportDirty(); }
        gui.SameLine();
        if (gui.Button("Kill", new SysVec2(70, 0))) { health.Kill(); ctx.Panel.MarkViewportDirty(); }
        gui.SameLine();
        if (gui.Button("Revive", new SysVec2(70, 0))) { health.Revive(); ctx.Panel.MarkViewportDirty(); }

        if (!SceneManager.IsPlaying)
            gui.TextDisabled("Edit-mode tests don't fire DestroyOnDeath (play only).");
    }
}

[ComponentPreview(typeof(UIDocument))]
internal sealed class UIDocumentPreview : IComponentPreview {
    public void Draw(in ComponentPreviewContext ctx) {
        var doc = (UIDocument)ctx.Behaviour;
        InspectorPanel panel = ctx.Panel;
        EditorDecoration.DrawSectionHeader("Markup & Style");
        DrawPathDropField(panel, "UXML (markup)", doc.Uxml, [".uxml", ".uihtml", ".html"], p => doc.Uxml = p);
        DrawPathDropField(panel, "USS (style)", doc.Uss, [".uss", ".uicss", ".css"], p => doc.Uss = p);
        gui.TextDisabled("Drag a markup/style asset here, or type its Assets/... path.");
    }

    static void DrawPathDropField(InspectorPanel panel, string label, string current, string[] exts, Action<string> apply) {
        gui.PushId(label);
        gui.TextDisabled(label);
        var s = current ?? "";
        gui.SetNextItemWidth(-1);
        if (gui.InputText("##path", ref s, 256)) {
            EditorCommands.Structural($"Edit {label}", () => { apply(s); panel.MarkViewportDirty(); });
        }

        if (InspectorPanel.AcceptGuidDrop(out Guid guid)) {
            string path = AssetDatabase.GuidToAssetPath(guid);
            if (path is not null && exts.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase))) {
                EditorCommands.Structural($"Assign {label}", () => { apply(path); panel.MarkViewportDirty(); });
            }
        }
        gui.PopId();
    }
}

[ComponentPreview(typeof(ParticleSystem))]
internal sealed class ParticleSystemPreview : IComponentPreview {
    public void Draw(in ComponentPreviewContext ctx) {
        var particles = (ParticleSystem)ctx.Behaviour;
        EditorDecoration.DrawSectionHeader("Preview");

        float spacing = gui.ItemSpacing.X;
        float w = (gui.ContentRegionAvail.X - spacing) * 0.5f;
        if (gui.Button($"{EditorIcons.Refresh}  Restart", new SysVec2(w, 0)))
            particles.Clear();
        gui.SameLine();
        if (gui.Button($"{EditorIcons.Play}  Emit 50", new SysVec2(w, 0)))
            particles.Emit(50);
        gui.TextDisabled($"{particles.LiveCount} live");

        if (particles.LiveCount > 0)
            ctx.Panel.MarkViewportDirty();
    }
}

[ComponentPreview(typeof(TrailRenderer))]
internal sealed class TrailRendererPreview : IComponentPreview {
    public void Draw(in ComponentPreviewContext ctx) {
        var trail = (TrailRenderer)ctx.Behaviour;
        EditorDecoration.DrawSectionHeader("Preview");

        if (gui.Button($"{EditorIcons.Refresh}  Clear", new SysVec2(-1, 0)))
            trail.Clear();
        gui.TextDisabled($"{trail.PointCount} points");

        if (trail.PointCount > 0)
            ctx.Panel.MarkViewportDirty();
    }
}
