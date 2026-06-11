namespace BallisticEngine;

/// <summary>
/// B-Object is the base class for all objects in the engine.
/// </summary>
public abstract class BObject {
    // InstanceId is a unique identifier for each instance of BObject. Settable (internal) so the
    // scene serializer can RESTORE an entity's identity on load/undo: deserialization rebuilds
    // entities as fresh objects, and without re-stamping the saved id every undo would orphan the
    // editor selection (the old object is gone). Reassigned exactly once, right after Instantiate,
    // before the object is ever used as a hash key — so Equals/GetHashCode stay consistent.
    public Guid InstanceId { get; internal set; } = Guid.NewGuid();
    public string Name = "BObject";

    public override bool Equals(object? obj) {
        if (obj is null)
            return false;

        return obj is BObject other && InstanceId.Equals(other.InstanceId);
    }

    protected bool Equals(BObject other) {
        return other is not null && InstanceId.Equals(other.InstanceId);
    }

    public override int GetHashCode() => InstanceId.GetHashCode();

    protected virtual void OnInstanceCreated() {
    }
}