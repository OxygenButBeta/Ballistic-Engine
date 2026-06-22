namespace BallisticEngine;

public readonly struct AudioData {
    public readonly short[] Samples;
    public readonly int Channels;
    public readonly int SampleRate;

    public AudioData(short[] samples, int channels, int sampleRate) {
        Samples = samples ?? System.Array.Empty<short>();
        Channels = channels < 1 ? 1 : channels;
        SampleRate = sampleRate < 1 ? 44100 : sampleRate;
    }

    public int FrameCount => Samples is null || Channels == 0 ? 0 : Samples.Length / Channels;
    public float DurationSeconds => SampleRate == 0 ? 0f : (float)FrameCount / SampleRate;
    public bool IsValid => Samples is { Length: > 0 };
}
