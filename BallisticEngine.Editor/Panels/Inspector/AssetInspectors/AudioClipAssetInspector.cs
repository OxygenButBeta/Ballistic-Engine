using BallisticEngine.AssetPipeline;
using BallisticEngine.AssetPipeline.Loaders;
using BallisticEngine.Serialization;
using SysVec2 = System.Numerics.Vector2;
using SysVec3 = System.Numerics.Vector3;
using SysVec4 = System.Numerics.Vector4;
using static BallisticEngine.Editor.Inspector.AssetInspectors.AssetInspectorGuiAccess;

namespace BallisticEngine.Editor.Inspector.AssetInspectors;

[AssetInspector(".wav")]
[AssetInspector(".wave")]
[AssetInspector(".ogg")]
internal sealed class AudioClipAssetInspector : IAssetInspector {
    public void Draw(in AssetInspectorContext ctx) => DrawAudioClipAsset(ctx.Path);

    static void DrawAudioClipAsset(string path) {
        AudioClip clip = AssetDatabase.Load<AudioClip>(path);
        if (clip is null) {
            gui.TextDisabled("Could not load audio clip.");
            return;
        }

        EditorDecoration.DrawSectionHeader("Preview");
        bool playing = InspectorPanel.audioPreviewVoice is { IsPlaying: true };
        if (gui.Button(playing ? $"{EditorIcons.Pause}  Stop" : $"{EditorIcons.Play}  Play",
                new SysVec2(120, 0))) {
            InspectorPanel.audioPreviewVoice?.Stop();
            InspectorPanel.audioPreviewVoice = playing ? null : Audio.Play(clip);
        }
        gui.SameLine();
        gui.TextDisabled($"{clip.DurationSeconds:F1}s  -  {clip.Channels}ch  -  {clip.SampleRate} Hz");
        if (!Audio.IsAvailable)
            gui.TextDisabled("(no audio device on this machine - preview is silent)");
    }
}
