using OpenTK.Graphics.OpenGL;

namespace BallisticEngine;

public enum TextureType
{
    Diffuse = TextureUnit.Texture0,
    Normal = TextureUnit.Texture1,
    Specular = TextureUnit.Texture2,
    Metallic = TextureUnit.Texture3,
    SkyboxFace0 = TextureUnit.Texture4,
    SkyboxFace1 = TextureUnit.Texture5,
    SkyboxFace2 = TextureUnit.Texture6,
    SkyboxFace3 = TextureUnit.Texture7,
    SkyboxFace4 = TextureUnit.Texture8,
    SkyboxFace5 = TextureUnit.Texture9,
}