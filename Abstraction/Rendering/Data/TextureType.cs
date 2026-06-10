namespace BallisticEngine;

// Values double as the standard material's sampler slot indices.
// The GL layer binds them as TextureUnit.Texture0 + (int)type.
public enum TextureType {
    Diffuse = 0,
    Normal = 1,
    Metallic = 2,
    Roughness = 3,
    AO = 4,
    SkyBox = 11,
}
