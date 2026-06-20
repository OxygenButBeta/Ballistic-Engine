namespace BallisticEngine;

// The BRIDGE between a shader's declared properties and the renderer's FIXED per-draw GPU layout.
//
// Today every material is shaded by one embedded HLSL (StandardOpaque.hlsl) that reads a fixed
// constant buffer + a fixed t0..t5 SRV table. The property-bag refactor lets a shader DECLARE its
// properties (name/type/default), but the renderer still has to land those values in that fixed
// layout. `MaterialSemantic` is the join: a declared property tags WHICH engine channel it feeds,
// so the packer can write `_BaseColor` -> DrawConstants.BaseColorFactor and `_Diffuse` -> SRV t0
// without the renderer knowing the property's authored name.
//
// `None` = a property a future custom-HLSL shader declares that has NO mapping onto the Standard
// layout (it would feed a generic CB the custom shader owns). The Standard packer skips `None`.
public enum MaterialSemantic {
    None = 0,

    // Texture slots — the fixed t0..t5 SRV order the Standard shader binds.
    DiffuseMap,
    NormalMap,
    MetallicMap,
    RoughnessMap,
    AOMap,
    EmissiveMap,

    // Scalar / vector PBR factors (glTF semantics) packed into DrawConstants / GpuMaterial.
    BaseColorFactor,
    MetallicFactor,
    RoughnessFactor,
    SpecularReflectance,
    EmissiveColor,
    EmissiveIntensity,
    NormalStrength,
    NormalFlipY,

    // Flags that gate shading branches (cutout discard, ORM unpack, blend state).
    PackedOrm,
    Cutout,
    Transparent,
    Opacity,

    // Clearcoat layer (glTF KHR_materials_clearcoat).
    Clearcoat,
    ClearcoatRoughness,

    // Emissive enable — derived at load time (map OR authored colour), not a directly authored value.
    // Declared so the packer can read it, but the loader (ApplyScalars) remains its sole authority.
    IsEmissive,
}
