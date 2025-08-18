using OpenTK.Graphics.OpenGL;

namespace BallisticEngine;

public enum TextureType
{
    Diffuse = TextureUnit.Texture0,
    Normal = TextureUnit.Texture1,
    Metallic = TextureUnit.Texture2,
    Roughness = TextureUnit.Texture3,
    AO = TextureUnit.Texture4,
    SkyBox = TextureUnit.Texture11,
}