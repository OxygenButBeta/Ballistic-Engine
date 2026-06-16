namespace BallisticEngine;

// A loaded sound asset (Unity's AudioClip). Holds the decoded CPU PCM and lazily uploads it to the
// audio backend the first time it's played, caching the buffer handle. Like Mesh, this is a BObject
// so the asset database caches one instance per GUID and the scene serializer turns AudioSource.Clip
// into a guid ref automatically.
public sealed class AudioClip : BObject {
    public AudioData Data { get; }

    // Backend buffer handle, created on first Play (0 = not yet uploaded / no backend). The buffer
    // is owned by the backend; DestroyBuffer runs on Unload.
    int bufferHandle;

    public AudioClip(in AudioData data, string name) {
        Data = data;
        Name = name;
    }

    public float DurationSeconds => Data.DurationSeconds;
    public int Channels => Data.Channels;
    public int SampleRate => Data.SampleRate;

    // Returns the backend buffer for this clip, uploading on first use. 0 when audio is unavailable.
    internal int GetOrCreateBuffer() {
        if (bufferHandle != 0 || Audio.Backend is null)
            return bufferHandle;
        AudioData data = Data;   // copy to a local: an auto-property can't be passed by `in`
        bufferHandle = Audio.Backend.CreateBuffer(in data);
        return bufferHandle;
    }

    // Releases the backend buffer (asset unload / backend teardown). Idempotent.
    internal void ReleaseBuffer() {
        if (bufferHandle != 0) {
            Audio.Backend?.DestroyBuffer(bufferHandle);
            bufferHandle = 0;
        }
    }
}
