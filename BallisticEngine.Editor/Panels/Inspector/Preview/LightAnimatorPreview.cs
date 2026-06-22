using BallisticEngine.AssetPipeline.Loaders;
using BallisticEngine.UI;
using SysVec2 = System.Numerics.Vector2;
using SysVec4 = System.Numerics.Vector4;
using static BallisticEngine.Editor.Inspector.Preview.ComponentPreviewGuiAccess;

namespace BallisticEngine.Editor.Inspector.Preview;

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
