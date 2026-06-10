using OpenTK.Mathematics;

namespace BallisticEngine;

public abstract class Texture3D : Texture {
    public Vector3 skyAmbient = Vector3.Zero;

    // Faces in order: +X, -X, +Y, -Y, +Z, -Z
    protected internal abstract void UploadFaces(TextureData[] faces);
}
