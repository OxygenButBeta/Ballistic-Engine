
namespace BallisticEngine;

public interface IAudioBackend : System.IDisposable {
    bool IsAvailable { get; }

    int CreateBuffer(in AudioData data);

    void DestroyBuffer(int bufferHandle);

    IAudioVoice Play(int bufferHandle, in AudioVoiceParams parameters);

    void Update(in AudioListenerState listener);

    float MasterVolume { get; set; }
}

public interface IAudioVoice {
    bool IsPlaying { get; }
    bool Looping { get; set; }
    float Volume { get; set; }
    float Pitch { get; set; }
    Vector3 Position { get; set; }
    Vector3 Velocity { get; set; }
    float TimeSeconds { get; set; }

    void Stop();
    void Pause();
    void Resume();
}

public struct AudioVoiceParams {
    public bool Spatial;
    public bool Looping;
    public float Volume;
    public float Pitch;
    public Vector3 Position;
    public Vector3 Velocity;
    public float MinDistance;
    public float MaxDistance;

    public static AudioVoiceParams Default => new() {
        Spatial = false,
        Looping = false,
        Volume = 1f,
        Pitch = 1f,
        MinDistance = 1f,
        MaxDistance = 500f,
    };
}

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
