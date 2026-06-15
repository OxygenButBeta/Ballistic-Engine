
namespace BallisticEngine;

// The "ears" of the scene (Unity's AudioListener) — usually on the camera/player. Pushes its
// transform pose to the Audio facade each frame so 3D voices spatialize correctly. Only ONE should
// be active; the most recently enabled wins (Active), matching Unity's "multiple listeners" warning
// behavior without the hard error.
//
// Like AudioSource, the listener updates in play mode (its pose drives gameplay audio). In edit mode
// the editor camera can drive the listener directly via Audio.SetListener for scene-view preview.
[Component("Audio Listener", "Audio")]
public class AudioListener : Behaviour {
    // The active listener the engine reads each frame. Set in OnEnabled, cleared in OnDisabled —
    // mirrors the SceneBehaviour `static Active` pattern (Skybox/SceneLighting).
    public static AudioListener Active { get; private set; }

    Vector3 lastPosition;
    bool hasLast;

    protected internal override void OnEnabled() {
        if (Active is not null && Active != this)
            Debugging.LogWarning(
                $"Multiple AudioListeners active; '{Entity?.Name}' will take over from '{Active.Entity?.Name}'.");
        Active = this;
        hasLast = false;
    }

    protected internal override void OnDisabled() {
        if (Active == this)
            Active = null;
    }

    protected internal override void OnDetach() {
        if (Active == this)
            Active = null;
    }

    protected internal override void Tick(in float delta) {
        if (Active != this)
            return;

        Vector3 position = transform.WorldPosition;
        Quaternion rotation = transform.WorldRotation;

        var state = new AudioListenerState {
            Position = position,
            Forward = Vector3.Transform(Vector3.UnitZ, rotation),
            Up = Vector3.Transform(Vector3.UnitY, rotation),
            Velocity = hasLast && delta > 0f ? (position - lastPosition) / delta : Vector3.Zero,
        };
        Audio.SetListener(in state);

        lastPosition = position;
        hasLast = true;
    }

    // A camera-style marker — the listener is "the ears", usually on the camera.
    public override void OnDrawGizmos(IGizmos gizmos) {
        gizmos.Color = new Vector3(0.4f, 0.8f, 1f);
        gizmos.DrawIcon(transform.WorldPosition, GizmoIcon.Camera);
    }
}
