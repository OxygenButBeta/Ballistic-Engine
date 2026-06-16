using OpenTK.Audio.OpenAL;

namespace BallisticEngine.OpenALAudio;

// One OpenAL source from the backend's voice pool. A voice is recycled (returned to the pool) once
// its one-shot finishes, but a LOOPING voice stays Reserved until something explicitly Stops it —
// otherwise AcquireVoice would hand it out from under the still-playing loop.
internal sealed class OpenALVoice : IAudioVoice {
    public int Source { get; }
    public int BoundBuffer { get; private set; }
    public bool Reserved { get; private set; }   // looping voice held out of the free pool

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

        // SourceRelative true = position is relative to the listener (always centered) → effectively
        // 2D. False = world-space position spatialized by the listener pose → 3D.
        AL.Source(Source, ALSourceb.SourceRelative, !p.Spatial);
        if (p.Spatial) {
            AL.Source(Source, ALSource3f.Position, p.Position.X, p.Position.Y, p.Position.Z);
            AL.Source(Source, ALSource3f.Velocity, p.Velocity.X, p.Velocity.Y, p.Velocity.Z);
            AL.Source(Source, ALSourcef.ReferenceDistance, p.MinDistance);
            AL.Source(Source, ALSourcef.MaxDistance, p.MaxDistance);
            AL.Source(Source, ALSourcef.RolloffFactor, 1f);
        }
        else {
            // Center a 2D voice on the listener so panning/attenuation never apply.
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
        // OpenTK's AL.GetSource(ALSource3f) returns an OpenTK Vector3; read the components into the
        // engine's System.Numerics Vector3 via the out-param overload (no cross-type conversion).
        get { AL.GetSource(Source, ALSource3f.Position, out float x, out float y, out float z); return new Vector3(x, y, z); }
        set => AL.Source(Source, ALSource3f.Position, value.X, value.Y, value.Z);
    }

    public Vector3 Velocity {
        get { AL.GetSource(Source, ALSource3f.Velocity, out float x, out float y, out float z); return new Vector3(x, y, z); }
        set => AL.Source(Source, ALSource3f.Velocity, value.X, value.Y, value.Z);
    }

    // Play-head position in seconds. Writing it seeks (OpenAL clamps to the buffer); seeking a
    // stopped/finished one-shot just sets the offset — Play/Resume picks up from there.
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

    // Called once per frame by the backend: a finished, non-looping voice drops its reservation
    // and unbinds so AcquireVoice can reuse it.
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

// Returned by Play() when audio is unavailable or the pool is exhausted, so callers can always
// hold a non-null IAudioVoice and poke at it harmlessly (no null checks at every call site).
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
