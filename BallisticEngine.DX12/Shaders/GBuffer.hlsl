// G-buffer geometry pass for the DX12 clustered-deferred renderer. Same vertex transform + material
// sampling as the old forward StandardOpaque, but the pixel shader writes a fat G-buffer (3 MRT) instead
// of shading — the deferred lighting pass reads it. Depth is written normally (DSV).
//
// G-buffer layout:
//   RT0 (R8G8B8A8_UNorm_SRGB)        : albedo.rgb (+ a = emissive flag/strength packed later)
//   RT1 (R16G16B16A16_Float)         : world-space normal.xyz (+ a = unused)
//   RT2 (R8G8B8A8_UNorm)             : metallic, roughness, ao, (a = matFlags: cutout/emissive bits)
// Emissive is written into RT0.a-driven path? No — emissive added in the lighting pass needs its color;
// for now emissive is folded into albedo-as-lit later. Keep RT2.a for flags.

cbuffer DrawConstants : register(b0) {
    float4x4 Mvp;            // model * view * proj (transposed)
    float4x4 Model;          // model (transposed) — world normal/pos
    float3   LightDir;       float Exposure;     // (lighting fields unused here; shared struct w/ forward)
    float3   LightColor;     float Metallic;
    float3   Ambient;        float Roughness;
    float3   CameraPos;      float SpecularReflectance;
    float4   BaseColorFactor;
    float3   EmissiveFactor; float HasEmissive;
    float    NormalStrength; float NormalFlipY; float HasMetallicMap; float HasRoughnessMap;
    float    PackedOrm;      float Cutout;       float UseIBL; float PrefilterMaxMip;
};

// Per-pass motion constants (b1): UNJITTERED current + previous frame view*proj (transposed). The pixel
// shader reprojects the surface's world position through both to get a jitter-free screen-space motion
// vector (prevUV - currUV) for TAA + FSR. Same for every draw this frame.
cbuffer MotionConstants : register(b1) {
    float4x4 ViewProjCur;    // current frame, UNJITTERED (transposed)
    float4x4 ViewProjPrev;   // previous frame, UNJITTERED (transposed)
};

Texture2D DiffuseMap   : register(t0);
Texture2D NormalMap    : register(t1);
Texture2D MetallicMap  : register(t2);
Texture2D RoughnessMap : register(t3);
Texture2D AOMap        : register(t4);
Texture2D EmissiveMap  : register(t5);
SamplerState LinearWrap : register(s0);

struct VSInput {
    float3 Pos : POSITION; float3 Normal : NORMAL; float2 Uv : TEXCOORD0; float4 Tangent : TANGENT;
};
struct VSOutput {
    float4 Position : SV_Position;
    float3 NormalW  : NORMAL;
    float4 TangentW : TANGENT;
    float2 Uv       : TEXCOORD0;
    float3 PosW     : TEXCOORD1;
};
struct GBufferOut {
    float4 Albedo   : SV_Target0;   // rgb albedo, a = specularReflectance (for F0 in lighting)
    float4 Normal   : SV_Target1;   // rgb world normal, a = emissive strength flag
    float4 Material : SV_Target2;   // r metallic, g roughness, b ao, a = cutout flag
    float4 Emissive : SV_Target3;   // rgb emissive radiance (added directly in lighting)
    float2 Motion   : SV_Target4;   // screen-space motion (prevUV - currUV), UNJITTERED
};

// Jitter-free screen-space motion from the surface world position (perspective-correct via PosW). On a
// static frame ViewProjCur == ViewProjPrev so the two clips are bit-identical -> motion exactly 0.
float2 ScreenMotion(float3 posW) {
    float4 clipCur  = mul(float4(posW, 1.0), ViewProjCur);
    float4 clipPrev = mul(float4(posW, 1.0), ViewProjPrev);
    float2 uvCur  = (clipCur.xy  / clipCur.w)  * float2(0.5, -0.5) + 0.5;
    float2 uvPrev = (clipPrev.xy / clipPrev.w) * float2(0.5, -0.5) + 0.5;
    return uvPrev - uvCur;
}

VSOutput VSMain(VSInput v) {
    VSOutput o;
    o.Position = mul(float4(v.Pos, 1.0), Mvp);
    o.PosW = mul(float4(v.Pos, 1.0), Model).xyz;
    o.NormalW = normalize(mul(float4(v.Normal, 0.0), Model).xyz);
    o.TangentW = float4(normalize(mul(float4(v.Tangent.xyz, 0.0), Model).xyz), v.Tangent.w);
    o.Uv = v.Uv;
    return o;
}

float3 NormalFromMap(float2 uv, float3 Ngeom, float3 T, float bitangentSign) {
    float2 nxy = NormalMap.Sample(LinearWrap, uv).rg;
    if (NormalFlipY > 0.5) nxy.y = 1.0 - nxy.y;
    float2 xy = (nxy * 2.0 - 1.0) * max(NormalStrength, 0.0);
    float z = sqrt(max(1.0 - dot(xy, xy), 0.0));
    float3 tn = normalize(float3(xy, z));
    float3 N = normalize(Ngeom);
    float3 Tn = normalize(T - N * dot(N, T));
    float3 B = cross(N, Tn) * bitangentSign;
    return normalize(Tn * tn.x + B * tn.y + N * tn.z);
}

GBufferOut PSMain(VSOutput i) {
    float4 albedoSample = DiffuseMap.Sample(LinearWrap, i.Uv);
    if (Cutout > 0.5 && albedoSample.a < 0.5) discard;
    float3 albedo = albedoSample.rgb * BaseColorFactor.rgb;

    float3 mr = MetallicMap.Sample(LinearWrap, i.Uv).rgb;
    float metallicSample = HasMetallicMap > 0.5 ? (PackedOrm > 0.5 ? mr.b : mr.r) : 1.0;
    float metallic = saturate(metallicSample * Metallic);
    float roughSample = HasRoughnessMap > 0.5 ? RoughnessMap.Sample(LinearWrap, i.Uv).r
                                              : (PackedOrm > 0.5 ? mr.g : 1.0);
    float roughness = clamp(roughSample * Roughness, 0.045, 1.0);
    float ao = AOMap.Sample(LinearWrap, i.Uv).r;

    float3 N = NormalFromMap(i.Uv, i.NormalW, i.TangentW.xyz, i.TangentW.w);
    float3 emissive = (HasEmissive > 0.5) ? EmissiveMap.Sample(LinearWrap, i.Uv).rgb * EmissiveFactor : 0.0.xxx;

    GBufferOut o;
    o.Albedo   = float4(albedo, SpecularReflectance);
    o.Normal   = float4(N * 0.5 + 0.5, 1.0);             // store [0,1]-packed world normal
    o.Material = float4(metallic, roughness, ao, Cutout > 0.5 ? 1.0 : 0.0);
    o.Emissive = float4(emissive, 1.0);
    o.Motion   = ScreenMotion(i.PosW);
    return o;
}
