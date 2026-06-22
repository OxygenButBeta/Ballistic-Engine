
namespace BallisticEngine;

public static class Audio {
    public static IAudioBackend Backend { get; set; }

    static AudioListenerState listener = AudioListenerState.Default;

    public static float MasterVolume {
        get => Backend?.MasterVolume ?? 1f;
        set { if (Backend is not null) Backend.MasterVolume = value; }
    }

    public static bool IsAvailable => Backend is { IsAvailable: true };

    public static IAudioVoice Play(AudioClip clip, float volume = 1f, float pitch = 1f, bool loop = false) {
        if (clip is null || Backend is null)
            return null;
        int buffer = clip.GetOrCreateBuffer();
        if (buffer == 0)
            return null;
        var p = AudioVoiceParams.Default;
        p.Volume = volume;
        p.Pitch = pitch;
        p.Looping = loop;
        return Backend.Play(buffer, in p);
    }

    public static IAudioVoice PlayAtPoint(AudioClip clip, Vector3 position, float volume = 1f,
        float minDistance = 1f, float maxDistance = 500f, float pitch = 1f) {
        if (clip is null || Backend is null)
            return null;
        int buffer = clip.GetOrCreateBuffer();
        if (buffer == 0)
            return null;
        var p = AudioVoiceParams.Default;
        p.Spatial = true;
        p.Volume = volume;
        p.Pitch = pitch;
        p.Position = position;
        p.MinDistance = minDistance;
        p.MaxDistance = maxDistance;
        return Backend.Play(buffer, in p);
    }

    public static void SetListener(in AudioListenerState state) => listener = state;

    public static void Update() => Backend?.Update(in listener);

    public static void Shutdown() {
        Backend?.Dispose();
        Backend = null;
    }
}
