namespace BallisticEngine;

/// <summary>
/// B-Object is the base class for all objects in the engine.
/// </summary>
public abstract class BObject {
    // InstanceId is a unique identifier for each instance of BObject.
    public readonly Guid InstanceId = Guid.NewGuid();
    public string Name = "BObject";

    public override bool Equals(object? obj) {
        if (obj is null)
            return false;

        return obj is BObject other && InstanceId.Equals(other.InstanceId);
    }

    protected bool Equals(BObject other) => InstanceId.Equals(other.InstanceId);

    public override int GetHashCode() => InstanceId.GetHashCode();

    protected virtual void OnInstanceCreated() {
    }
}