using OpenTK.Mathematics;

namespace BallisticEngine;

// The audio backend contract. Exactly one implementation is injected at bootstrap (the Audio/
// OpenAL module); the Engine layer talks ONLY through this so it stays free of OpenAL references,
// mirroring how rendering goes through RenderAsset and physics through IPhysicsWorld.
//
// Lifecycle: the host creates the backend once, the engine uploads decoded clips to GPU/driver
// buffers (CreateBuffer), spawns voices to play them (Play), and updates the listener pose +
// active voices once per frame (Update). Dispose tears the device down on shutdown.
public interface IAudioBackend : System.IDisposable {
    // True once the backend's device/context actually came up. A backend is still injected on
    // machines with no audio runtime (it self-disables and plays silently rather than crashing),
    // so the engine checks THIS — not "is a backend present" — to know whether sound really works.
    bool IsAvailable { get; }

    // Uploads decoded PCM to a driver-side buffer and returns an opaque handle (0 = failed).
    // The CPU AudioData can be dropped after this — the buffer owns the samples.
    int CreateBuffer(in AudioData data);

    // Releases a buffer created by CreateBuffer. Voices still referencing it are stopped first.
    void DestroyBuffer(int bufferHandle);

    // Starts a voice playing the given buffer. Returns a handle to control it (0 = no free voice).
    // A 2D voice ignores position/attenuation (UI, music); a 3D voice spatializes against the
    // listener set in Update. Mono buffers spatialize; stereo buffers always play 2D (OpenAL rule).
    IAudioVoice Play(int bufferHandle, in AudioVoiceParams parameters);

    // Updates the listener (the "ears") and advances backend bookkeeping (recycles finished
    // one-shot voices). Called once per frame by the engine after gameplay Tick.
    void Update(in AudioListenerState listener);

    // Master volume 0..1 applied on top of every voice's own gain (the global mixer fader).
    float MasterVolume { get; set; }
}

// A single playing sound. Handles go inert (IsPlaying = false, setters no-op) once the voice
// finishes or is stopped — callers may hold a stale reference safely (engine never-throw style).
public interface IAudioVoice {
    bool IsPlaying { get; }
    bool Looping { get; set; }
    float Volume { get; set; }      // 0..1, this voice's own gain
    float Pitch { get; set; }       // playback rate multiplier, 1 = normal
    Vector3 Position { get; set; }  // world-space; ignored by 2D voices
    Vector3 Velocity { get; set; }  // for Doppler; ignored by 2D voices
    float TimeSeconds { get; set; } // play-head position in seconds; set to seek (clamped by driver)

    void Stop();
    void Pause();
    void Resume();
}

// Parameters captured when a voice starts. 3D voices attenuate by distance between MinDistance
// (full volume) and MaxDistance (silent), linear-rolloff, matching Unity's AudioSource defaults.
public struct AudioVoiceParams {
    public bool Spatial;          // true = 3D positional, false = 2D (UI/music)
    public bool Looping;
    public float Volume;          // 0..1
    public float Pitch;           // 1 = normal
    public Vector3 Position;      // world-space (3D only)
    public Vector3 Velocity;      // world-space (3D Doppler)
    public float MinDistance;     // full volume within this radius
    public float MaxDistance;     // silent beyond this radius

    public static AudioVoiceParams Default => new() {
        Spatial = false,
        Looping = false,
        Volume = 1f,
        Pitch = 1f,
        MinDistance = 1f,
        MaxDistance = 500f,
    };
}

// The listener pose the backend spatializes against (the camera/player "ears"). Forward/Up
// orient the listener; Velocity drives Doppler on moving listeners.
public struct AudioListenerState {
    public Vector3 Position;
    public Vector3 Forward;
    public Vector3 Up;
    public Vector3 Velocity;

    public static AudioListenerState Default => new() {
        Position = Vector3.Zero,
        Forward = -Vector3.UnitZ,
        Up = Vector3.UnitY,
        Velocity = Vector3.Zero,
    };
}
