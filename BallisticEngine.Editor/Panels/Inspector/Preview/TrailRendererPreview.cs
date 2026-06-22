using BallisticEngine.AssetPipeline.Loaders;
using BallisticEngine.UI;
using SysVec2 = System.Numerics.Vector2;
using SysVec4 = System.Numerics.Vector4;
using static BallisticEngine.Editor.Inspector.Preview.ComponentPreviewGuiAccess;

namespace BallisticEngine.Editor.Inspector.Preview;

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
