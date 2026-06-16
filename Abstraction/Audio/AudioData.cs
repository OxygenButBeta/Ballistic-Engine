namespace BallisticEngine;

// CPU-side decoded audio (the audio analogue of MeshData/TextureData in Abstraction/Rendering/Data).
// Lives in Abstraction so the Engine layer's AudioClip and the AssetPipeline importer share one type
// without either reaching into the OpenAL backend. Samples are interleaved 16-bit signed PCM — the
// lingua franca both .wav and .ogg decode to and the format OpenAL uploads natively.
public readonly struct AudioData {
    public readonly short[] Samples;   // interleaved PCM; length = FrameCount * Channels
    public readonly int Channels;      // 1 = mono (spatializable), 2 = stereo
    public readonly int SampleRate;    // Hz, e.g. 44100

    public AudioData(short[] samples, int channels, int sampleRate) {
        Samples = samples ?? System.Array.Empty<short>();
        Channels = channels < 1 ? 1 : channels;
        SampleRate = sampleRate < 1 ? 44100 : sampleRate;
    }

    // Null-safe: default(AudioData) (a failed decode returning `default`) leaves Samples null,
    // and these are read on that path — never dereference Samples without the null guard.
    public int FrameCount => Samples is null || Channels == 0 ? 0 : Samples.Length / Channels;
    public float DurationSeconds => SampleRate == 0 ? 0f : (float)FrameCount / SampleRate;
    public bool IsValid => Samples is { Length: > 0 };
}
