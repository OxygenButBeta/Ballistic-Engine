using OpenTK.Mathematics;

namespace BallisticEngine;

// One live particle. AoS pool (a simple array on the ParticleSystem) — fine for v1 counts; a SoA
// split is a later optimization if profiling demands it. Internal to the Engine layer; the GL pass
// reads a per-instance snapshot the system exposes, not this struct directly.
internal struct Particle {
    public Vector3 Position;
    public Vector3 Velocity;
    public float Age;          // seconds since spawn
    public float Lifetime;     // seconds until death
    public float Rotation;     // billboard roll, radians
    public float RotationSpeed;
    public float StartSize;    // per-particle base size (jitter is baked at spawn)

    public readonly bool IsDead => Age >= Lifetime;
    public readonly float NormalizedAge => Lifetime > 0f ? Age / Lifetime : 1f;
}

// The render-ready snapshot of one particle the GL pass streams into its instance buffer. Color and
// size are pre-evaluated on the CPU (start->end over lifetime), so the shader stays trivial. Explicit
// sequential layout (no padding between these blittable fields) so the GL pass can stream the array
// into a VBO matching the vertex attribute layout: vec3 pos, float size, vec4 color, float rotation,
// vec4 uvRect = 13 floats / 52 bytes.
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public struct ParticleInstance {
    public Vector3 Position;
    public float Size;
    public Vector4 Color;      // RGBA, premultiplied-friendly
    public float Rotation;
    // Sub-rect of the texture this particle samples (offset.xy, scale.xy). Whole texture = (0,0,1,1);
    // a texture-sheet tile is a smaller cell. The shader maps the quad uv into this rect.
    public Vector4 UvRect;
}
