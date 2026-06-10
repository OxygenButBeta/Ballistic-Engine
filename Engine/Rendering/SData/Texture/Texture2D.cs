namespace BallisticEngine;

public abstract class Texture2D : Texture {
    protected internal abstract void Upload(in TextureData data, TextureType type);
}
