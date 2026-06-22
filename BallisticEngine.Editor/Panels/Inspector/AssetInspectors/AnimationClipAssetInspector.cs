using BallisticEngine.AssetPipeline;
using BallisticEngine.AssetPipeline.Loaders;
using BallisticEngine.Serialization;
using SysVec2 = System.Numerics.Vector2;
using SysVec3 = System.Numerics.Vector3;
using SysVec4 = System.Numerics.Vector4;
using static BallisticEngine.Editor.Inspector.AssetInspectors.AssetInspectorGuiAccess;

namespace BallisticEngine.Editor.Inspector.AssetInspectors;

[AssetInspector(".banim")]
internal sealed class AnimationClipAssetInspector : IAssetInspector {
    public void Draw(in AssetInspectorContext ctx) => DrawAnimationClipAsset(ctx.Path);

    static void DrawAnimationClipAsset(string path) {
        AnimationClip clip = AssetDatabase.Load<AnimationClip>(path);
        if (clip is null) {
            gui.TextDisabled("Could not load animation clip.");
            return;
        }

        EditorDecoration.DrawSectionHeader("Animation");
        gui.TextDisabled($"Duration: {clip.DurationSeconds:F2}s");
        gui.TextDisabled($"Channels (animated bones): {clip.Data.Channels.Length}");
        gui.TextDisabled($"Ticks/sec: {clip.TicksPerSecond:F0}");
        gui.Spacing();
        gui.TextWrapped("Assign this clip to an Animator on a skinned mesh, then use the Animator's " +
            "scrub slider to preview the pose.");
    }
}
