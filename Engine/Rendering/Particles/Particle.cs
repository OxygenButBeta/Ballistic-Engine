
namespace BallisticEngine;

internal struct Particle {
    public Vector3 Position;
    public Vector3 Velocity;
    public float Age;
    public float Lifetime;
    public float Rotation;
    public float RotationSpeed;
    public float StartSize;

    public readonly bool IsDead => Age >= Lifetime;
    public readonly float NormalizedAge => Lifetime > 0f ? Age / Lifetime : 1f;
}
