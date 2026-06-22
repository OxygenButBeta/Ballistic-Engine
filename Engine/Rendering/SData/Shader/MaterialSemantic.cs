namespace BallisticEngine;

public enum MaterialSemantic {
    None = 0,

    DiffuseMap,
    NormalMap,
    MetallicMap,
    RoughnessMap,
    AOMap,
    EmissiveMap,

    BaseColorFactor,
    MetallicFactor,
    RoughnessFactor,
    SpecularReflectance,
    EmissiveColor,
    EmissiveIntensity,
    NormalStrength,
    NormalFlipY,

    PackedOrm,
    Cutout,
    Transparent,
    Opacity,

    Clearcoat,
    ClearcoatRoughness,

    IsEmissive,
}
