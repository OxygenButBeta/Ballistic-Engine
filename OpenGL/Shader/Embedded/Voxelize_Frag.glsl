#version 460 core
// Voxelization fragment stage: scatter each surface fragment's DIRECT-lit radiance into the 3D
// voxel texture. radiance = albedo * (sun*NdotL*shadow + skyAmbient). That injected radiance is
// the first bounce the cone tracer then gathers as indirect light. Written with imageStore using a
// moving-average-ish max-blend (atomic via a packed RGBA8 would be ideal; we use a simple store
// since each voxel is dominated by its nearest surface and the mip filter smooths the rest).

#extension GL_ARB_bindless_texture : require

in GsOut {
    vec3 worldPos;
    vec3 normal;
    vec2 uv;
    flat uint materialId;
} gs;

layout(binding = 0, rgba8) uniform image3D VoxelRadiance;  // readwrite for in-place bounce RMW
// Bounce pass: read the grid's already-injected radiance (coarse mips, from the previous iteration's
// GenerateMipmap) along the surface normal and ADD it. Each iteration = one more bounce -> deep
// interiors fill with light. BouncePass=0 is the direct pass (overwrite); >0 adds bounce (RMW).
uniform sampler3D VoxelRadianceSampler;
uniform int BouncePass;

struct GpuMaterial { uvec2 dH;uvec2 nH;uvec2 mH;uvec2 rH;uvec2 aH;uvec2 eH;
    vec4 bcf;vec4 ef;float mm;float rm;float ns;float op;uint fl;uint a;uint b;uint c; };
layout(std430, binding = 6) readonly buffer GpuMaterialBuf { GpuMaterial gpuMats[]; };

uniform vec3 VolumeMin;
uniform vec3 VolumeInvSize;
uniform int  VoxelRes;

uniform vec3 SunDir;        // toward the sun (world)
uniform vec3 SunColor;      // pre-exposed radiance
uniform vec3 SkyAmbient;    // flat sky fill (pre-exposed)

// Sun cascade shadow (reuse the renderer's cascades — same uniforms as the lit pass).
uniform sampler2DArrayShadow ShadowCascades;
uniform mat4 CascadeMatrices[4];
uniform vec4 CascadeBias;
uniform int  CascadeCount;

float sunShadow(vec3 wp, vec3 N) {
    // Pick the first cascade whose projection contains the point.
    for (int c = 0; c < CascadeCount && c < 4; ++c) {
        vec4 p = CascadeMatrices[c] * vec4(wp, 1.0);
        vec3 proj = p.xyz / p.w * 0.5 + 0.5;
        if (all(greaterThan(proj.xy, vec2(0.0))) && all(lessThan(proj.xy, vec2(1.0))) && proj.z < 1.0) {
            float bias = CascadeBias[c];
            return texture(ShadowCascades, vec4(proj.xy, float(c), proj.z - bias));
        }
    }
    return 1.0; // outside all cascades: lit
}

void main() {
    vec3 g = (gs.worldPos - VolumeMin) * VolumeInvSize;
    if (any(lessThan(g, vec3(0.0))) || any(greaterThan(g, vec3(1.0))))
        return;
    ivec3 vc = ivec3(g * float(VoxelRes));
    vc = clamp(vc, ivec3(0), ivec3(VoxelRes - 1));

    GpuMaterial m = gpuMats[gs.materialId];
    vec3 albedo = texture(sampler2D(m.dH), gs.uv).rgb * m.bcf.rgb;

    vec3 N = normalize(gs.normal);
    float NdotL = max(dot(N, SunDir), 0.0);
    float sh = NdotL > 0.0 ? sunShadow(gs.worldPos, N) : 1.0;

    if (BouncePass == 0) {
        // DIRECT pass: sun + sky + emissive leaving this surface (overwrite).
        vec3 radiance = albedo * (SunColor * NdotL * sh + SkyAmbient);
        if ((m.fl & 16u) != 0u)   // emissive-as-area-light (flag bit4 = HasEmissive)
            radiance += texture(sampler2D(m.eH), gs.uv).rgb * m.ef.rgb;
        imageStore(VoxelRadiance, vc, vec4(radiance, 1.0));
    } else {
        // BOUNCE pass: gather the already-injected radiance arriving along N (coarse mip = the
        // incoming hemisphere average) and ADD this surface's reflected share. RMW so the direct
        // light written in pass 0 is preserved and each pass compounds another bounce.
        vec3 ahead = (gs.worldPos + N * 2.0 - VolumeMin) * VolumeInvSize;
        vec3 incoming = vec3(0.0);
        if (all(greaterThan(ahead, vec3(0.0))) && all(lessThan(ahead, vec3(1.0))))
            incoming = textureLod(VoxelRadianceSampler, ahead, 2.0).rgb;
        vec3 bounce = albedo * incoming * 0.85; // 0.85 = bounce energy retained per hop
        vec4 cur = imageLoad(VoxelRadiance, vc);
        imageStore(VoxelRadiance, vc, vec4(cur.rgb + bounce, 1.0));
    }
}
