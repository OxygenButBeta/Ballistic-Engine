
namespace BallisticEngine;

public interface IAudioBackend : System.IDisposable {
    bool IsAvailable { get; }

    int CreateBuffer(in AudioData data);

    void DestroyBuffer(int bufferHandle);

    IAudioVoice Play(int bufferHandle, in AudioVoiceParams parameters);

    void Update(in AudioListenerState listener);

    float MasterVolume { get; set; }
}
