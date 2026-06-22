
namespace BallisticEngine;

[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public struct ParticleInstance {
    public Vector3 Position;
    public float Size;
    public Vector4 Color;

    public float Rotation;

    public Vector4 UvRect;
}
