// Surface-function G-buffer skeleton. This is GBuffer.hlsl with the per-pixel surface computation
// factored out into a user-authorable `Surface(SurfaceInput) -> SurfaceOutput` function. The engine
// OWNS everything else: VSMain (so z-prepass position invariance + motion are bit-identical to the
// Standard path), the texture/sampler/cbuffer ABI (b0/t0-t5/b1/s0 — identical root signature so PSOs
// are interchangeable per-draw), the tangent-space normal resolve, and the 5-MRT G-buffer packing.
//
// A custom material's Surface body is STRING-CONCATENATED after this skeleton at compile time (the
// embedded-resource loader has no #include). The skeleton defines `__SURFACE_BODY__` as the default
// Standard surface; a custom shader is compiled by appending its own `Surface(...)` and NOT defining
// the default (the cache strips the default when a custom body is present). For Stage A this file
// compiles standalone with the Standard body inline and must render byte-identical to GBuffer.hlsl.

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

cbuffer MotionConstants : register(b1) {
    float4x4 ViewProjCur;    // current frame, UNJITTERED (transposed)
    float4x4 ViewProjPrev;   // previous frame, UNJITTERED (transposed)
    float    NormalLodBias;
    float3   _padMotion;
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
    float4 Albedo   : SV_Target0;
    float4 Normal   : SV_Target1;
    float4 Material : SV_Target2;
    float4 Emissive : SV_Target3;
    float2 Motion   : SV_Target4;
};

// ---- The Surface contract (user-facing) ----------------------------------------------------------
// A custom shader implements `SurfaceOutput Surface(SurfaceInput i)`. It may sample the bound maps,
// read DrawConstants fields, and use the helper functions below. It returns surface values in WORLD
// space (Normal already in world space — use SurfaceNormalFromMap for tangent-space normal maps).
struct SurfaceInput {
    float2 Uv;
    float3 NormalW;     // interpolated geometric world normal
    float4 TangentW;    // world tangent (.xyz) + bitangent sign (.w)
    float3 PosW;        // world position
};
struct SurfaceOutput {
    float3 Albedo;
    float3 Normal;      // WORLD-space shading normal
    float  Metallic;
    float  Roughness;
    float  AO;
    float3 Emissive;
    float  Alpha;       // < 0.5 with Cutout discards
};

float2 ScreenMotion(float3 posW) {
    float4 clipCur  = mul(float4(posW, 1.0), ViewProjCur);
    float4 clipPrev = mul(float4(posW, 1.0), ViewProjPrev);
    float2 uvCur  = (clipCur.xy  / clipCur.w)  * float2(0.5, -0.5) + 0.5;
    float2 uvPrev = (clipPrev.xy / clipPrev.w) * float2(0.5, -0.5) + 0.5;
    return uvPrev - uvCur;
}

// Tangent-space normal-map resolve, available to custom Surface bodies (same math as the Standard path).
float3 SurfaceNormalFromMap(float2 uv, float3 Ngeom, float3 T, float bitangentSign) {
    float2 nxy = NormalMap.SampleBias(LinearWrap, uv, NormalLodBias).rg;
    if (NormalFlipY > 0.5) nxy.y = 1.0 - nxy.y;
    float2 xy = (nxy * 2.0 - 1.0) * max(NormalStrength, 0.0);
    float z = sqrt(max(1.0 - dot(xy, xy), 0.0));
    float3 tn = normalize(float3(xy, z));
    float3 N = normalize(Ngeom);
    float3 Tn = normalize(T - N * dot(N, T));
    float3 B = cross(N, Tn) * bitangentSign;
    return normalize(Tn * tn.x + B * tn.y + N * tn.z);
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

// The surface-shader cache replaces the marker line below with a custom Surface() body, placed HERE
// (before PSMain, so its call resolves — HLSL has no forward declaration). No substitution → the
// default Standard body compiles (standalone/embedded build + the BALLISTIC_DX12_SURFACE_SKELETON
// door). A custom build defines CUSTOM_SURFACE so the default below is omitted.
//USER_SURFACE_MARKER

// ---- Default (Standard PBR) Surface body. A custom shader replaces this; the cache omits it then. ----
#ifndef CUSTOM_SURFACE
SurfaceOutput Surface(SurfaceInput i) {
    SurfaceOutput s;
    float4 albedoSample = DiffuseMap.Sample(LinearWrap, i.Uv);
    s.Alpha = albedoSample.a;
    s.Albedo = albedoSample.rgb * BaseColorFactor.rgb;

    float3 mr = MetallicMap.Sample(LinearWrap, i.Uv).rgb;
    float metallicSample = HasMetallicMap > 0.5 ? (PackedOrm > 0.5 ? mr.b : mr.r) : 1.0;
    s.Metallic = saturate(metallicSample * Metallic);
    float roughSample = HasRoughnessMap > 0.5 ? RoughnessMap.Sample(LinearWrap, i.Uv).r
                                              : (PackedOrm > 0.5 ? mr.g : 1.0);
    s.Roughness = clamp(roughSample * Roughness, 0.045, 1.0);
    s.AO = AOMap.Sample(LinearWrap, i.Uv).r;
    s.Normal = SurfaceNormalFromMap(i.Uv, i.NormalW, i.TangentW.xyz, i.TangentW.w);
    s.Emissive = (HasEmissive > 0.5) ? EmissiveMap.Sample(LinearWrap, i.Uv).rgb * EmissiveFactor : 0.0.xxx;
    return s;
}
#endif

GBufferOut PSMain(VSOutput i) {
    SurfaceInput si;
    si.Uv = i.Uv; si.NormalW = i.NormalW; si.TangentW = i.TangentW; si.PosW = i.PosW;
    SurfaceOutput s = Surface(si);

    if (Cutout > 0.5 && s.Alpha < 0.5) discard;

    GBufferOut o;
    o.Albedo   = float4(s.Albedo, SpecularReflectance);
    // s.Normal is already a unit world normal (the Standard body returns SurfaceNormalFromMap, which
    // normalizes). NOT re-normalized here so the bytes match GBuffer.hlsl exactly; a custom Surface is
    // responsible for returning a unit normal.
    o.Normal   = float4(s.Normal * 0.5 + 0.5, 1.0);
    o.Material = float4(s.Metallic, s.Roughness, s.AO, Cutout > 0.5 ? 1.0 : 0.0);
    o.Emissive = float4(s.Emissive, 1.0);
    o.Motion   = ScreenMotion(i.PosW);
    return o;
}
