// IBL precompute passes for the DX12 backend (HLSL ports of the GL IBL_Irradiance / IBL_Prefilter /
// IBL_BrdfLut shaders). Each runs as a fullscreen triangle into one cube face (or the 2D LUT). The env
// cube itself is baked elsewhere (the procedural sky or an asset cubemap copied into an RGBA16F cube).
//
// Convention: GL cube FaceDir(face, uv) reproduced exactly so the irradiance/prefilter cubes line up
// with how the opaque shader samples them. Entry points: VSFullscreen + PSIrradiance/PSPrefilter/PSBrdf.

cbuffer IblConstants : register(b0) {
    int   Face;
    float Roughness;
    float SourceResolution;
    float _pad;
};

TextureCube EnvMap : register(t0);
SamplerState LinearSamp : register(s0);

static const float PI = 3.14159265359;

float3 FaceDir(int face, float2 uv) {
    float2 st = uv * 2.0 - 1.0;
    if (face == 0) return float3( 1.0, -st.y, -st.x);
    if (face == 1) return float3(-1.0, -st.y,  st.x);
    if (face == 2) return float3( st.x,  1.0,  st.y);
    if (face == 3) return float3( st.x, -1.0, -st.y);
    if (face == 4) return float3( st.x, -st.y,  1.0);
    return float3(-st.x, -st.y, -1.0);
}

struct VSOut { float4 Position : SV_Position; float2 Uv : TEXCOORD0; };

// Fullscreen triangle (covers the viewport with 3 verts); UV 0..1 with v flipped to match GL's
// top-left FaceDir convention (GL face UVs run s right, t DOWN).
VSOut VSFullscreen(uint vid : SV_VertexID) {
    VSOut o;
    float2 uv = float2((vid << 1) & 2, vid & 2);
    o.Position = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
    o.Uv = float2(uv.x, uv.y);   // 0..1, t down (matches GL FaceDir t)
    return o;
}

// ---- Diffuse irradiance (cosine convolution) ----
float4 PSIrradiance(VSOut i) : SV_Target {
    float3 N = normalize(FaceDir(Face, i.Uv));
    float3 up = abs(N.y) < 0.999 ? float3(0,1,0) : float3(1,0,0);
    float3 right = normalize(cross(up, N));
    up = normalize(cross(N, right));

    float3 irradiance = 0;
    float sampleDelta = 0.025;
    float count = 0;
    for (float phi = 0.0; phi < 2.0 * PI; phi += sampleDelta) {
        for (float theta = 0.0; theta < 0.5 * PI; theta += sampleDelta) {
            float3 t = float3(sin(theta) * cos(phi), sin(theta) * sin(phi), cos(theta));
            float3 dir = t.x * right + t.y * up + t.z * N;
            float3 rad = min(EnvMap.SampleLevel(LinearSamp, dir, 0).rgb, 500.0.xxx);
            irradiance += rad * cos(theta) * sin(theta);
            count += 1.0;
        }
    }
    irradiance = PI * irradiance / count;
    return float4(irradiance, 1.0);
}

// ---- GGX-prefiltered specular ----
float RadicalInverseVdC(uint bits) {
    bits = (bits << 16u) | (bits >> 16u);
    bits = ((bits & 0x55555555u) << 1u) | ((bits & 0xAAAAAAAAu) >> 1u);
    bits = ((bits & 0x33333333u) << 2u) | ((bits & 0xCCCCCCCCu) >> 2u);
    bits = ((bits & 0x0F0F0F0Fu) << 4u) | ((bits & 0xF0F0F0F0u) >> 4u);
    bits = ((bits & 0x00FF00FFu) << 8u) | ((bits & 0xFF00FF00u) >> 8u);
    return float(bits) * 2.3283064365386963e-10;
}
float2 Hammersley(uint i, uint n) { return float2((float)i / (float)n, RadicalInverseVdC(i)); }

float3 ImportanceSampleGGX(float2 Xi, float3 N, float rough) {
    float a = rough * rough;
    float phi = 2.0 * PI * Xi.x;
    float cosT = sqrt((1.0 - Xi.y) / (1.0 + (a * a - 1.0) * Xi.y));
    float sinT = sqrt(1.0 - cosT * cosT);
    float3 H = float3(cos(phi) * sinT, sin(phi) * sinT, cosT);
    float3 up = abs(N.z) < 0.999 ? float3(0,0,1) : float3(1,0,0);
    float3 tan = normalize(cross(up, N));
    float3 bit = cross(N, tan);
    return normalize(tan * H.x + bit * H.y + N * H.z);
}
float DistributionGGX(float NdotH, float rough) {
    float a = rough * rough; float a2 = a * a;
    float d = NdotH * NdotH * (a2 - 1.0) + 1.0;
    return a2 / (PI * d * d + 1e-7);
}

float4 PSPrefilter(VSOut i) : SV_Target {
    float3 N = normalize(FaceDir(Face, i.Uv));
    float3 R = N, V = N;
    const uint SAMPLES = 512u;
    float3 prefiltered = 0;
    float totalWeight = 0;
    for (uint s = 0u; s < SAMPLES; s++) {
        float2 Xi = Hammersley(s, SAMPLES);
        float3 H = ImportanceSampleGGX(Xi, N, Roughness);
        float3 L = normalize(2.0 * dot(V, H) * H - V);
        float NdotL = max(dot(N, L), 0.0);
        if (NdotL <= 0.0) continue;
        float NdotH = max(dot(N, H), 0.0);
        float HdotV = max(dot(H, V), 0.0);
        float D = DistributionGGX(NdotH, Roughness);
        float pdf = D * NdotH / (4.0 * HdotV) + 1e-4;
        float saTexel = 4.0 * PI / (6.0 * SourceResolution * SourceResolution);
        float saSample = 1.0 / ((float)SAMPLES * pdf + 1e-4);
        float mip = Roughness == 0.0 ? 0.0 : 0.5 * log2(saSample / saTexel);
        float3 rad = min(EnvMap.SampleLevel(LinearSamp, L, mip).rgb, 16384.0.xxx);
        prefiltered += rad * NdotL;
        totalWeight += NdotL;
    }
    return float4(prefiltered / max(totalWeight, 1e-4), 1.0);
}

// ---- Split-sum BRDF LUT (RG) ----
float GeometrySchlickGGX_IBL(float NdotV, float rough) {
    float a = rough * rough; float k = a / 2.0;
    return NdotV / (NdotV * (1.0 - k) + k);
}
float GeometrySmith_IBL(float NdotV, float NdotL, float rough) {
    return GeometrySchlickGGX_IBL(NdotV, rough) * GeometrySchlickGGX_IBL(NdotL, rough);
}
float2 PSBrdf(VSOut i) : SV_Target {
    float NdotV = max(i.Uv.x, 1e-3);
    float roughness = i.Uv.y;
    float3 V = float3(sqrt(1.0 - NdotV * NdotV), 0.0, NdotV);
    float3 N = float3(0, 0, 1);
    float scale = 0, bias = 0;
    const uint SAMPLES = 1024u;
    for (uint s = 0u; s < SAMPLES; s++) {
        float2 Xi = Hammersley(s, SAMPLES);
        float3 H = ImportanceSampleGGX(Xi, N, roughness);
        float3 L = normalize(2.0 * dot(V, H) * H - V);
        float NdotL = max(L.z, 0.0);
        float NdotH = max(H.z, 0.0);
        float VdotH = max(dot(V, H), 0.0);
        if (NdotL <= 0.0) continue;
        float G = GeometrySmith_IBL(NdotV, NdotL, roughness);
        float GVis = (G * VdotH) / (NdotH * NdotV);
        float Fc = pow(1.0 - VdotH, 5.0);
        scale += (1.0 - Fc) * GVis;
        bias += Fc * GVis;
    }
    return float2(scale, bias) / (float)SAMPLES;
}
