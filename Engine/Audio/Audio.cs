using OpenTK.Mathematics;

namespace BallisticEngine;

// Static facade over the audio backend (Unity's AudioSource statics + the global mixer). The
// backend (Audio/OpenAL) is injected by EngineBootstrap; the Engine layer talks ONLY through
// IAudioBackend so it stays free of OpenAL, mirroring the Physics facade over IPhysicsWorld.
//
// The engine pumps Audio.Update once per frame (after gameplay Tick) with the active listener's
// pose. Unlike physics, audio runs in BOTH edit and play mode at the backend level, but AudioSource
// playback is gated to play mode (a sound shouldn't fire while you're editing) — see AudioSource.
public static class Audio {
    public static IAudioBackend Backend { get; set; }

    // The active listener's pose, refreshed each frame by AudioListener (or the editor camera).
    // The backend spatializes 3D voices against this.
    static AudioListenerState listener = AudioListenerState.Default;

    // Master volume 0..1 over every voice. Persisted on the backend so it survives a listener swap.
    public static float MasterVolume {
        get => Backend?.MasterVolume ?? 1f;
        set { if (Backend is not null) Backend.MasterVolume = value; }
    }

    // True only when a backend is injected AND it actually came up (OpenAL device + context).
    // A backend is injected at bootstrap even on machines with no OpenAL runtime, where it
    // gracefully self-disables — so checking `Backend is not null` would wrongly report audio as
    // available and hide the "no device / native DLL missing" case from the editor's preview hint.
    public static bool IsAvailable => Backend is { IsAvailable: true };

    // ---- One-shot playback (Unity's AudioSource.PlayClipAtPoint / PlayOneShot) ---------------

    // Plays a clip in 2D (no spatialization) — UI sounds, music, non-positional SFX.
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

    // Plays a clip at a world position, spatialized against the current listener (Unity's
    // PlayClipAtPoint). Fire-and-forget: the returned voice recycles itself when it finishes.
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

    // ---- Listener (engine-internal) ----------------------------------------------------------

    // Set by the active AudioListener each frame (or by the editor camera for scene-view audio).
    public static void SetListener(in AudioListenerState state) => listener = state;

    // Pumped once per frame by the engine loop: pushes the listener pose and recycles finished
    // voices. Safe to call with no backend (no-op).
    public static void Update() => Backend?.Update(in listener);

    // Releases the backend on shutdown (host Main after the window loop returns).
    public static void Shutdown() {
        Backend?.Dispose();
        Backend = null;
    }
}
