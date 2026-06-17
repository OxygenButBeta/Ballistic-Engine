using System.IO;
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

// Renderer: per-submesh material slots. Wraps its section in a BeginGrid/EndTable table (the others draw
// raw) — exactly the inline shape it replaces. Body moved here in RW1.1.
[ComponentPreview(typeof(Renderer))]
internal sealed class RendererPreview : IComponentPreview {
    public void Draw(in ComponentPreviewContext ctx) {
        var renderer = (Renderer)ctx.Behaviour;
        if (InspectorPanel.BeginGrid("##submats")) {
            DrawSubMeshMaterials(renderer);
            ImGui.EndTable();
        }
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
            InspectorPanel.Row("");
            ImGui.TextDisabled($"... and {subMeshes.Length - MaxRows} more");
        }
    }

    static void DrawSubMeshMaterialRow(Renderer renderer, SubMeshData sub, int i, string rowLabel) {
        InspectorPanel.Row(rowLabel);

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
}

[ComponentPreview(typeof(Volume))]
internal sealed class VolumePreview : IComponentPreview {
    public void Draw(in ComponentPreviewContext ctx) =>
        ctx.Panel.DrawVolumeProfileSection(ctx.Entity, (Volume)ctx.Behaviour);
}

[ComponentPreview(typeof(Terrain))]
internal sealed class TerrainPreview : IComponentPreview {
    public void Draw(in ComponentPreviewContext ctx) =>
        InspectorPanel.DrawTerrainBrushSection((Terrain)ctx.Behaviour);
}

[ComponentPreview(typeof(AudioSource))]
internal sealed class AudioSourcePreview : IComponentPreview {
    public void Draw(in ComponentPreviewContext ctx) =>
        ctx.Panel.DrawAudioSourceSection((AudioSource)ctx.Behaviour);
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

[ComponentPreview(typeof(UIDocument))]
internal sealed class UIDocumentPreview : IComponentPreview {
    public void Draw(in ComponentPreviewContext ctx) =>
        ctx.Panel.DrawUIDocumentSection((UIDocument)ctx.Behaviour);
}

// ParticleSystem preview: it already animates live in the editor (AdvanceAll runs every editor
// frame), so this just adds a Restart (clear) + a one-shot Emit test + a live count, and keeps the
// viewport repainting while particles are alive so you see the motion. Body moved here in RW1.2.
[ComponentPreview(typeof(ParticleSystem))]
internal sealed class ParticleSystemPreview : IComponentPreview {
    public void Draw(in ComponentPreviewContext ctx) {
        var particles = (ParticleSystem)ctx.Behaviour;
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
            ctx.Panel.MarkViewportDirty();
    }
}

// TrailRenderer: animates live in the editor; a Clear + a live point count. Body moved here in RW1.1.
[ComponentPreview(typeof(TrailRenderer))]
internal sealed class TrailRendererPreview : IComponentPreview {
    public void Draw(in ComponentPreviewContext ctx) {
        var trail = (TrailRenderer)ctx.Behaviour;
        ImGui.Spacing();
        ImGui.SeparatorText("Preview");

        if (ImGui.Button($"{EditorIcons.Refresh}  Clear", new SysVec2(-1, 0)))
            trail.Clear();
        ImGui.TextDisabled($"{trail.PointCount} points");

        if (trail.PointCount > 0)
            ctx.Panel.MarkViewportDirty();
    }
}
