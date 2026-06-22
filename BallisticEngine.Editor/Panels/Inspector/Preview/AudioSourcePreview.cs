using BallisticEngine.AssetPipeline.Loaders;
using BallisticEngine.UI;
using SysVec2 = System.Numerics.Vector2;
using SysVec4 = System.Numerics.Vector4;
using static BallisticEngine.Editor.Inspector.Preview.ComponentPreviewGuiAccess;

namespace BallisticEngine.Editor.Inspector.Preview;

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
