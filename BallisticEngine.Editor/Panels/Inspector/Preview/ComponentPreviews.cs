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

[ComponentPreview(typeof(Animator))]
internal sealed class AnimatorPreview : IComponentPreview {
    public void Draw(in ComponentPreviewContext ctx) =>
        ctx.Panel.DrawAnimatorSection((Animator)ctx.Behaviour);
}

[ComponentPreview(typeof(AnimatorController))]
internal sealed class AnimatorControllerPreview : IComponentPreview {
    public void Draw(in ComponentPreviewContext ctx) =>
        ctx.Panel.DrawAnimatorControllerSection((AnimatorController)ctx.Behaviour);
}

[ComponentPreview(typeof(LightAnimator))]
internal sealed class LightAnimatorPreview : IComponentPreview {
    public void Draw(in ComponentPreviewContext ctx) =>
        ctx.Panel.DrawLightAnimatorSection((LightAnimator)ctx.Behaviour);
}

[ComponentPreview(typeof(Spawner))]
internal sealed class SpawnerPreview : IComponentPreview {
    public void Draw(in ComponentPreviewContext ctx) =>
        ctx.Panel.DrawSpawnerSection((Spawner)ctx.Behaviour);
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

[ComponentPreview(typeof(ParticleSystem))]
internal sealed class ParticleSystemPreview : IComponentPreview {
    public void Draw(in ComponentPreviewContext ctx) =>
        ctx.Panel.DrawParticleSystemSection((ParticleSystem)ctx.Behaviour);
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
