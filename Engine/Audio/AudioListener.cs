
namespace BallisticEngine;

[Component("Audio Listener", "Audio")]
public class AudioListener : Behaviour {
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

    public override void OnDrawGizmos(IGizmos gizmos) {
        gizmos.Color = new Vector3(0.4f, 0.8f, 1f);
        gizmos.DrawIcon(transform.WorldPosition, GizmoIcon.Camera);
    }
}
