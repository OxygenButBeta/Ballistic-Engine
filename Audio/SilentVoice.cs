using OpenTK.Audio.OpenAL;

namespace BallisticEngine.OpenALAudio;

internal sealed class SilentVoice : IAudioVoice {
    public static readonly SilentVoice Instance = new();
    public bool IsPlaying => false;
    public bool Looping { get; set; }
    public float Volume { get; set; }
    public float Pitch { get; set; }
    public Vector3 Position { get; set; }
    public Vector3 Velocity { get; set; }
    public float TimeSeconds { get; set; }
    public void Stop() { }
    public void Pause() { }
    public void Resume() { }
}
