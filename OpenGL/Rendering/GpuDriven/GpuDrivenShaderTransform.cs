using System.Text;

namespace BallisticEngine.OpenGL.GpuDriven;

// Transforms a material's OWN vertex+fragment GLSL into a GPU-driven variant by INJECTION only —
// the shading math is never touched, so the lit result is bit-identical to the legacy uniform path.
//
// What changes:
//   * Vertex: the per-draw model matrix comes from PerDrawData[gl_DrawID] (written by the cull
//     compute) instead of the `model` uniform / instance attributes. gl_Position arithmetic is
//     otherwise identical (verified: raw-blit OpenTK mat4 read as `m*v` == uniform path).
//   * Fragment: the six material sampler2D uniforms and the per-material control uniforms become
//     #defines that read the bindless GpuMaterial indexed by the per-draw materialId. Every
//     `texture(Diffuse, uv)` etc. in the unchanged body resolves to a bindless sampler.
//
// The materialId is passed vertex->fragment via a flat out/in (gl_DrawID is only valid in the
// vertex stage). The PerDrawData/GpuMaterial blocks are declared identically in both stages.
public static class GpuDrivenShaderTransform {
    // Common SSBO + extension block injected into both stages right after the #version line.
    // Public so the GPU-driven prepass fragment can reuse the EXACT same block — GLSL requires
    // interface blocks of the same name to have identical member names/types across stages, so a
    // hand-written copy with different names (dH vs diffuseH) fails to link.
    public const string SharedDecls = @"
#extension GL_ARB_bindless_texture : require
#extension GL_ARB_shader_draw_parameters : require

struct GpuPerDraw { mat4 model; uint materialId; uint _p0; uint _p1; uint _p2; };
struct GpuMaterial {
    uvec2 diffuseH; uvec2 normalH; uvec2 metallicH; uvec2 roughnessH; uvec2 aoH; uvec2 emissiveH;
    vec4 baseColorFactor; vec4 emissiveFactor;
    float metallicMul; float roughnessMul; float normalStrengthM; float opacity;
    uint flags; float specularReflectanceM; uint _mp1; uint _mp2;
};
layout(std430, binding = 5) readonly buffer GpuPerDrawBuf  { GpuPerDraw gpuDraws[]; };
layout(std430, binding = 6) readonly buffer GpuMaterialBuf { GpuMaterial gpuMats[]; };
";

    // The GPU-driven prepass fragment: depth-only with bindless cutout discard. Built from the
    // SAME SharedDecls block as the vertex stage so the GpuMaterialBuf interface matches and links.
    public static string PrepassFragment() {
        return "#version 460 core\n" + SharedDecls + @"
in vec2 texCoord;
flat in uint vMaterialId;
void main() {
    GpuMaterial m = gpuMats[vMaterialId];
    if ((m.flags & 64u) != 0u && texture(sampler2D(m.diffuseH), texCoord).a < 0.5)
        discard;
}
";
    }

    public static string TransformVertex(string src) {
        string s = InsertAfterVersion(src, SharedDecls + @"
flat out uint vMaterialId;
");
        // Replace the model-matrix selection. The original computes:
        //   mat4 modelMatrix = isInstanced ? mat4(instance_...) : model;
        // We override modelMatrix from PerDrawData[gl_DrawID] and forward the materialId. Keep the
        // `model`/`isInstanced` uniforms declared (harmless, unused) so we don't have to excise them.
        const string marker = "mat4 modelMatrix = isInstanced";
        int idx = s.IndexOf(marker, StringComparison.Ordinal);
        if (idx >= 0) {
            int semi = s.IndexOf(';', idx);
            if (semi >= 0) {
                string replacement =
                    "GpuPerDraw _gd = gpuDraws[gl_DrawIDARB];\n" +
                    "    vMaterialId = _gd.materialId;\n" +
                    "    mat4 modelMatrix = _gd.model";
                s = s[..idx] + replacement + s[(semi)..];
            }
        }
        return s;
    }

    public static string TransformFragment(string src) {
        // Bindless sampler accessors + material-control #defines reading the per-draw material.
        // _M is the material for THIS fragment. Placed after the SSBO decls so gpuMats is visible.
        string header = SharedDecls + @"
flat in uint vMaterialId;
#define _M gpuMats[vMaterialId]
// Bindless sampler redefinitions — every texture(Diffuse,uv) in the body samples the bound map.
#define Diffuse   sampler2D(_M.diffuseH)
#define Normal    sampler2D(_M.normalH)
#define Metallic  sampler2D(_M.metallicH)
#define Roughness sampler2D(_M.roughnessH)
#define AO        sampler2D(_M.aoH)
#define Emissive  sampler2D(_M.emissiveH)
// Per-material control redefinitions (replace the plain uniforms).
#define BaseColorFactor    (_M.baseColorFactor)
#define MetallicMultiplier (_M.metallicMul)
#define RoughnessMultiplier (_M.roughnessMul)
#define SpecularReflectance (_M.specularReflectanceM)
#define NormalStrength     (_M.normalStrengthM)
#define EmissiveFactor     (_M.emissiveFactor.rgb)
#define Opacity            (_M.opacity)
#define PackedOrm     ((_M.flags & 1u)   != 0u)
#define HasMetallicMap ((_M.flags & 2u)  != 0u)
#define HasRoughnessMap ((_M.flags & 4u) != 0u)
#define NormalFlipY   ((_M.flags & 8u)   != 0u)
#define HasEmissive   ((_M.flags & 16u)  != 0u)
#define AlphaBlend    ((_M.flags & 32u)  != 0u)
#define AlphaCutout   ((_M.flags & 64u)  != 0u)
";
        string s = InsertAfterVersion(src, header);
        // Remove the original material-control uniform declarations and the six sampler2D
        // uniforms so the #defines above are the sole definition (a uniform + macro of the same
        // name is a redefinition error). NormalStrength etc. are matched as whole `uniform ...;`.
        s = StripMaterialUniforms(s);
        return s;
    }

    // Replaces the source's `#version ...` line with `#version 460 core` and inserts `block`
    // right after it. The SSBOs (std430), gl_DrawIDARB and bindless samplers all need 460; the
    // original 330/410 shading code is forward-compatible, so bumping the version is safe.
    static string InsertAfterVersion(string src, string block) {
        const string ver = "#version 460 core\n";
        int v = src.IndexOf("#version", StringComparison.Ordinal);
        if (v < 0)
            return ver + block + src;
        int eol = src.IndexOf('\n', v);
        if (eol < 0)
            return ver + block;
        // Drop the original #version line entirely, substitute 460, then the injected block.
        return ver + block + src[(eol + 1)..];
    }

    // Removes the per-material `uniform` lines that we replace with #defines. Matches the exact
    // declarations from Frag.glsl; leaving any in place would collide with the macro of the same
    // name. Sampler uniforms (Diffuse..Emissive) are removed too.
    static readonly string[] UniformsToStrip = {
        "uniform sampler2D Diffuse;",
        "uniform sampler2D Normal;",
        "uniform sampler2D Metallic;",
        "uniform sampler2D Roughness;",
        "uniform sampler2D AO;",
        "uniform sampler2D Emissive;",
        "uniform vec4 BaseColorFactor;",
        "uniform float MetallicMultiplier;",
        "uniform float RoughnessMultiplier;",
        "uniform float SpecularReflectance;",
        "uniform bool PackedOrm;",
        "uniform bool HasMetallicMap;",
        "uniform bool HasRoughnessMap;",
        "uniform float NormalStrength;",
        "uniform bool NormalFlipY;",
        "uniform vec3 EmissiveFactor;",
        "uniform bool HasEmissive;",
        "uniform bool AlphaBlend;",
        "uniform float Opacity;",
        "uniform bool AlphaCutout;",
    };

    static string StripMaterialUniforms(string src) {
        var sb = new StringBuilder(src.Length);
        foreach (string line in src.Split('\n')) {
            string trimmed = line.TrimStart();
            bool strip = false;
            foreach (string u in UniformsToStrip) {
                if (trimmed.StartsWith(u, StringComparison.Ordinal)) {
                    strip = true;
                    break;
                }
            }
            if (!strip)
                sb.Append(line).Append('\n');
        }
        return sb.ToString();
    }
}
