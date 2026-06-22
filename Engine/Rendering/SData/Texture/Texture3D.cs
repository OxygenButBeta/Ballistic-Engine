
namespace BallisticEngine;

public abstract class Texture3D : Texture {
    public Vector3 skyAmbient = Vector3.Zero;

    public Vector3 skyAirlight = Vector3.Zero;

    protected internal abstract void UploadFaces(TextureData[] faces);
}
