using StbImageSharp;

namespace BallisticEngine;

public sealed class GLTexture3D : Texture3D
{
    public override int UID { get; protected set; }
    public override void Activate()
    {
        throw new NotImplementedException();
    }

    public override void Deactivate()
    {
        throw new NotImplementedException();
    }

    public override void Dispose()
    {
        throw new NotImplementedException();
    }

    protected override void Import(ImageResult imageResult, TextureType textureType)
    {
        throw new NotImplementedException();
    }
}