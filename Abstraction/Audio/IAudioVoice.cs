
namespace BallisticEngine;

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
