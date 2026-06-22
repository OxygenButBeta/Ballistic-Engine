
namespace BallisticEngine;

public struct AudioListenerState {
    public Vector3 Position;
    public Vector3 Forward;
    public Vector3 Up;
    public Vector3 Velocity;

    public static AudioListenerState Default => new() {
        Position = Vector3.Zero,
        Forward = -Vector3.UnitZ,
        Up = Vector3.UnitY,
        Velocity = Vector3.Zero,
    };
}
