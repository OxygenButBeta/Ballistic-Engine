
namespace BallisticEngine;

public abstract class Texture3D : Texture {
    public Vector3 skyAmbient = Vector3.Zero;

    // Upper-hemisphere-weighted average: the SKY's hue without the ground hemisphere mixed
    // in. Airlight uses this (fog veils toward white-blue sky, not toward ground-brown);
    // zero means "not computed" and consumers fall back to skyAmbient.
    public Vector3 skyAirlight = Vector3.Zero;

    // Faces in order: +X, -X, +Y, -Y, +Z, -Z
    protected internal abstract void UploadFaces(TextureData[] faces);
}
