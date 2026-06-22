using BallisticEngine.AssetPipeline.Loaders;
using BallisticEngine.UI;
using SysVec2 = System.Numerics.Vector2;
using SysVec4 = System.Numerics.Vector4;
using static BallisticEngine.Editor.Inspector.Preview.ComponentPreviewGuiAccess;

namespace BallisticEngine.Editor.Inspector.Preview;

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
