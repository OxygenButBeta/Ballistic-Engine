using OpenTK.Audio.OpenAL;

namespace BallisticEngine.OpenALAudio;

internal sealed class OpenALVoice : IAudioVoice {
    public int Source { get; }
    public int BoundBuffer { get; private set; }
    public bool Reserved { get; private set; }

    bool looping;

    public OpenALVoice(int source) => Source = source;

    public void Configure(int buffer, in AudioVoiceParams p) {
        BoundBuffer = buffer;
        looping = p.Looping;
        Reserved = p.Looping;

        AL.Source(Source, ALSourcei.Buffer, buffer);
        AL.Source(Source, ALSourceb.Looping, p.Looping);
        AL.Source(Source, ALSourcef.Gain, MathHelper.Clamp(p.Volume, 0f, 1f));
        AL.Source(Source, ALSourcef.Pitch, p.Pitch <= 0f ? 0.01f : p.Pitch);

        AL.Source(Source, ALSourceb.SourceRelative, !p.Spatial);
        if (p.Spatial) {
            AL.Source(Source, ALSource3f.Position, p.Position.X, p.Position.Y, p.Position.Z);
            AL.Source(Source, ALSource3f.Velocity, p.Velocity.X, p.Velocity.Y, p.Velocity.Z);
            AL.Source(Source, ALSourcef.ReferenceDistance, p.MinDistance);
            AL.Source(Source, ALSourcef.MaxDistance, p.MaxDistance);
            AL.Source(Source, ALSourcef.RolloffFactor, 1f);
        }
        else {
            AL.Source(Source, ALSource3f.Position, 0f, 0f, 0f);
            AL.Source(Source, ALSource3f.Velocity, 0f, 0f, 0f);
        }
    }

    public void Play() => AL.SourcePlay(Source);

    public bool IsPlaying =>
        (ALSourceState)AL.GetSource(Source, ALGetSourcei.SourceState) == ALSourceState.Playing;

    public bool Looping {
        get => looping;
        set {
            looping = value;
            Reserved = value && IsPlaying;
            AL.Source(Source, ALSourceb.Looping, value);
        }
    }

    public float Volume {
        get => AL.GetSource(Source, ALSourcef.Gain);
        set => AL.Source(Source, ALSourcef.Gain, MathHelper.Clamp(value, 0f, 1f));
    }

    public float Pitch {
        get => AL.GetSource(Source, ALSourcef.Pitch);
        set => AL.Source(Source, ALSourcef.Pitch, value <= 0f ? 0.01f : value);
    }

    public Vector3 Position {
        get { AL.GetSource(Source, ALSource3f.Position, out float x, out float y, out float z); return new Vector3(x, y, z); }
        set => AL.Source(Source, ALSource3f.Position, value.X, value.Y, value.Z);
    }

    public Vector3 Velocity {
        get { AL.GetSource(Source, ALSource3f.Velocity, out float x, out float y, out float z); return new Vector3(x, y, z); }
        set => AL.Source(Source, ALSource3f.Velocity, value.X, value.Y, value.Z);
    }

    public float TimeSeconds {
        get => AL.GetSource(Source, ALSourcef.SecOffset);
        set => AL.Source(Source, ALSourcef.SecOffset, value < 0f ? 0f : value);
    }

    public void Stop() {
        AL.SourceStop(Source);
        Reserved = false;
    }

    public void Pause() => AL.SourcePause(Source);

    public void Resume() => AL.SourcePlay(Source);

    public void RecycleIfFinished() {
        if (Reserved && !looping)
            Reserved = false;
        var state = (ALSourceState)AL.GetSource(Source, ALGetSourcei.SourceState);
        if (state is ALSourceState.Stopped or ALSourceState.Initial) {
            if (!looping)
                Reserved = false;
        }
    }
}

internal sealed class SilentVoice : IAudioVoice {
    public static readonly SilentVoice Instance = new();
    public bool IsPlaying => false;
    public bool Looping { get; set; }
    public float Volume { get; set; }
    public float Pitch { get; set; }
    public Vector3 Position { get; set; }
    public Vector3 Velocity { get; set; }
    public float TimeSeconds { get; set; }
    public void Stop() { }
    public void Pause() { }
    public void Resume() { }
}
