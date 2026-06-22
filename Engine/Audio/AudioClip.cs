namespace BallisticEngine;

public sealed class AudioClip : BObject {
    public AudioData Data { get; }

    int bufferHandle;

    public AudioClip(in AudioData data, string name) {
        Data = data;
        Name = name;
    }

    public float DurationSeconds => Data.DurationSeconds;
    public int Channels => Data.Channels;
    public int SampleRate => Data.SampleRate;

    internal int GetOrCreateBuffer() {
        if (bufferHandle != 0 || Audio.Backend is null)
            return bufferHandle;
        AudioData data = Data;
        bufferHandle = Audio.Backend.CreateBuffer(in data);
        return bufferHandle;
    }

    internal void ReleaseBuffer() {
        if (bufferHandle != 0) {
            Audio.Backend?.DestroyBuffer(bufferHandle);
            bufferHandle = 0;
        }
    }
}
