using OpenTK.Mathematics;

namespace BallisticEngine;

// Plays an AudioClip from this entity (Unity's AudioSource). In 3D mode the sound is positioned at
// the entity's transform and attenuates with listener distance; in 2D mode it plays flat (UI/music).
//
// Playback is PLAY-MODE ONLY — like physics, a sound should not fire while you're editing the scene.
// PlayOnAwake starts it at OnBegin; otherwise call Play() from a script. A looping source follows the
// transform every Tick so a moving emitter pans correctly.
[Component("Audio Source", "Audio")]
public class AudioSource : Behaviour {
    [Tooltip("The sound to play. Drag a .wav/.ogg audio asset here.")]
    public AudioClip Clip { get; set; }

    [Tooltip("Start playing automatically when the scene begins (Unity's Play On Awake).")]
    public bool PlayOnAwake { get; set; } = true;

    [Tooltip("Restart from the beginning when the clip ends.")]
    public bool Loop { get; set; }

    [Header("Mix")]
    [Range(0f, 1f)]
    public float Volume { get; set; } = 1f;

    [Tooltip("Playback speed / pitch multiplier. 1 = normal.")]
    [Range(0.1f, 3f)]
    public float Pitch { get; set; } = 1f;

    [Header("Spatial")]
    [Tooltip("3D: position in the world and attenuate by listener distance. Off: flat 2D (UI/music).")]
    public bool Spatial { get; set; } = true;

    [Tooltip("Full volume within this distance from the listener.")]
    [Range(0.01f, 1000f)]
    public float MinDistance { get; set; } = 1f;

    [Tooltip("Inaudible beyond this distance from the listener.")]
    [Range(1f, 5000f)]
    public float MaxDistance { get; set; } = 500f;

    // The currently-playing voice (null when stopped). Runtime-only — never serialized.
    [NotSerialized]
    public bool IsPlaying => voice is { IsPlaying: true };

    IAudioVoice voice;

    protected internal override void OnBegin() {
        if (PlayOnAwake)
            Play();
    }

    protected internal override void OnDisabled() => Stop();
    protected internal override void OnDetach() => Stop();

    // Starts (or restarts) playback. No-op in edit mode / without a clip or backend.
    public void Play() {
        if (!SceneManager.IsPlaying || Clip is null)
            return;

        Stop();   // restart semantics — one voice per source

        var p = AudioVoiceParams.Default;
        p.Spatial = Spatial;
        p.Looping = Loop;
        p.Volume = Volume;
        p.Pitch = Pitch;
        p.MinDistance = MinDistance;
        p.MaxDistance = MaxDistance;
        if (Spatial) {
            p.Position = transform.WorldPosition;
            p.Velocity = Vector3.Zero;
        }

        int buffer = Clip.GetOrCreateBuffer();
        if (buffer == 0)
            return;
        voice = Audio.Backend?.Play(buffer, in p);
    }

    public void Stop() {
        voice?.Stop();
        voice = null;
    }

    public void Pause() => voice?.Pause();
    public void Resume() => voice?.Resume();

    protected internal override void Tick(in float delta) {
        if (voice is null || !voice.IsPlaying)
            return;

        // Live-track mix changes so an inspector tweak / script change is heard immediately.
        voice.Volume = Volume;
        voice.Pitch = Pitch;
        voice.Looping = Loop;

        // A moving 3D emitter must follow its transform; derive velocity for Doppler from the
        // frame delta (no need to store a Rigidbody reference).
        if (Spatial) {
            Vector3 now = transform.WorldPosition;
            voice.Velocity = delta > 0f ? (now - voice.Position) / delta : Vector3.Zero;
            voice.Position = now;
        }
    }

    // Always-on marker so an audio emitter is findable in the scene view.
    public override void OnDrawGizmos(IGizmos gizmos) {
        gizmos.Color = new Vector3(0.4f, 0.8f, 1f);
        gizmos.DrawIcon(transform.WorldPosition, GizmoIcon.Light);
    }

    // Selected: the 3D falloff range — inner sphere (full volume within MinDistance) and outer sphere
    // (silent beyond MaxDistance), Unity-style, so you can see where the sound is audible. 2D sources
    // ignore distance, so only the marker shows.
    public override void OnDrawGizmosSelected(IGizmos gizmos) {
        if (!Spatial)
            return;
        Vector3 p = transform.WorldPosition;
        gizmos.Color = new Vector3(0.4f, 0.8f, 1f);
        gizmos.DrawWireSphere(p, MinDistance);
        gizmos.Color = new Vector3(0.2f, 0.4f, 0.7f);
        gizmos.DrawWireSphere(p, MaxDistance);
    }
}
