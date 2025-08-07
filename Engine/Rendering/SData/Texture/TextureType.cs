using OpenTK.Graphics.OpenGL;

namespace BallisticEngine;

public enum TextureType
{
    Diffuse = TextureUnit.Texture0,
    Normal = TextureUnit.Texture1,
    Specular = TextureUnit.Texture2,
    Metallic = TextureUnit.Texture3
}