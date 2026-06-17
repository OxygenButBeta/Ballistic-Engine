using BallisticEngine.UI;
using Hexa.NET.ImGui;

namespace BallisticEngine.Editor.Inspector.Preview;

// The per-component preview sections (editor-rework Rule 1 / Phase B1), one self-registering
// IComponentPreview each. These REPLACE the `if (behaviour is Renderer/Volume/Terrain/...) DrawXxxSection`
// instanceof chain that used to live inline in InspectorPanel.DrawComponent. Every class is a thin shim:
// [ComponentPreview(typeof(T))] registers it for its component type, and Draw delegates straight back into
// the (internal) InspectorPanel section method via the context — so the rendered output is BYTE-IDENTICAL to
// the old inline call. Only the DISPATCH moved (instanceof chain → ComponentPreviewRegistry resolution).
//
// Discovery is by [ComponentPreview] (engine attribute) via TypeCache; order is deterministic by priority
// then type name (DeterministicResolver). The previews are stateless — per-section preview state stays as
// statics on InspectorPanel — so the registry keeps a single shared instance per class.

// Renderer: per-submesh material slots. Special-cased among the previews because it wraps its section in a
// BeginGrid/EndTable table (the others draw raw) — exactly the inline shape it replaces.
[ComponentPreview(typeof(Renderer))]
internal sealed class RendererPreview : IComponentPreview {
    public void Draw(in ComponentPreviewContext ctx) {
        var renderer = (Renderer)ctx.Behaviour;
        if (InspectorPanel.BeginGrid("##submats")) {
            InspectorPanel.DrawSubMeshMaterials(renderer);
            ImGui.EndTable();
        }
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

[ComponentPreview(typeof(Health))]
internal sealed class HealthPreview : IComponentPreview {
    public void Draw(in ComponentPreviewContext ctx) =>
        ctx.Panel.DrawHealthSection((Health)ctx.Behaviour);
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

[ComponentPreview(typeof(TrailRenderer))]
internal sealed class TrailRendererPreview : IComponentPreview {
    public void Draw(in ComponentPreviewContext ctx) =>
        ctx.Panel.DrawTrailRendererSection((TrailRenderer)ctx.Behaviour);
}
