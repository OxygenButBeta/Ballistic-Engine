using BallisticEngine.AssetPipeline.Loaders;
using BallisticEngine.UI;
using SysVec2 = System.Numerics.Vector2;
using SysVec4 = System.Numerics.Vector4;
using static BallisticEngine.Editor.Inspector.Preview.ComponentPreviewGuiAccess;

namespace BallisticEngine.Editor.Inspector.Preview;

[ComponentPreview(typeof(Animator))]
internal sealed class AnimatorPreview : IComponentPreview {
    static bool animatorPreviewPlaying;
    static float animatorPreviewTime;

    public void Draw(in ComponentPreviewContext ctx) =>
        EditorWidgets.AnimatorScrubber((Animator)ctx.Behaviour, ref animatorPreviewTime, ref animatorPreviewPlaying,
            ctx.Panel.MarkViewportDirty);
}
