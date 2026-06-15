// Forward OPAQUE PBR shader for the DX12 backend. Cook-Torrance GGX direct sun lighting with the full
// material map set (diffuse/normal/metallic/roughness/AO/emissive) + glTF scalar factors, mirroring the
// GL Standard Frag.glsl math (DistributionGGX / GeometrySmith / FresnelSchlick / ShadeSun) so shading
// matches the GL path. ACES-tonemapped. NOT yet included: IBL ambient, shadows, punctual lights, SSGI/
// post (later milestones) — ambient is a flat fill stand-in for IBL.
//
// CONVENTIONS (locked, DX12Migration.md): System.Numerics matrices are row-major, HLSL float4x4 is
// column-major, the CPU TRANSPOSES on upload, so mul(float4(pos,1), MVP) matches the CPU math. Vertex
// attributes arrive in SEPARATE input slots (engine keeps pos/normal/uv/tangent in separate buffers).

cbuffer DrawConstants : register(b0) {
    float4x4 Mvp;            // model * view * proj  (transposed on upload)
    float4x4 Model;          // model               (transposed) — world-space normals/tangents
    float3   LightDir;       // TO the light, normalized, world space
    float    Exposure;       // linear pre-tonemap scale (sun radiance is HDR/lux-scaled)
    float3   LightColor;     // sun radiance
    float    Metallic;       // material metallicFactor
    float3   Ambient;        // flat ambient fill (IBL stand-in)
    float    Roughness;      // material roughnessFactor
    float3   CameraPos;      // world-space camera position (for the view vector / specular)
    float    SpecularReflectance; // dielectric F0 = 0.08*this (0.5 = 4%)
    float4   BaseColorFactor;     // glTF base-color tint (rgb; a for cutout later)
    float3   EmissiveFactor; float HasEmissive;     // emissive color*intensity; >0.5 = emit
    float    NormalStrength; float NormalFlipY; float HasMetallicMap; float HasRoughnessMap;
    float    PackedOrm;      float Cutout;          float _pad0; float _pad1;
};

Texture2D DiffuseMap   : register(t0);
Texture2D NormalMap    : register(t1);
Texture2D MetallicMap  : register(t2);
Texture2D RoughnessMap : register(t3);
Texture2D AOMap        : register(t4);
Texture2D EmissiveMap  : register(t5);
SamplerState LinearWrap : register(s0);

struct VSInput {
    float3 Pos     : POSITION;   // slot 0
    float3 Normal  : NORMAL;     // slot 1
    float2 Uv      : TEXCOORD0;  // slot 2
    float4 Tangent : TANGENT;    // slot 3 (xyz tangent, w bitangent sign)
};
struct VSOutput {
    float4 Position : SV_Position;
    float3 NormalW  : NORMAL;
    float4 TangentW : TANGENT;
    float2 Uv       : TEXCOORD0;
    float3 PosW     : TEXCOORD1;
};

static const float PI = 3.14159265359;
static const float EPS = 1e-6;

VSOutput VSMain(VSInput v) {
    VSOutput o;
    o.Position = mul(float4(v.Pos, 1.0), Mvp);
    o.PosW = mul(float4(v.Pos, 1.0), Model).xyz;
    o.NormalW = normalize(mul(float4(v.Normal, 0.0), Model).xyz);
    o.TangentW = float4(normalize(mul(float4(v.Tangent.xyz, 0.0), Model).xyz), v.Tangent.w);
    o.Uv = v.Uv;
    return o;
}

float DistributionGGX(float3 N, float3 H, float rough) {
    float a = rough * rough; float a2 = a * a;
    float NdotH = max(dot(N, H), 0.0);
    float denom = NdotH * NdotH * (a2 - 1.0) + 1.0;
    return a2 / (PI * denom * denom + EPS);
}
float GeometrySchlickGGX(float NdotV, float rough) {
    float k = (rough + 1.0); k = (k * k) / 8.0;
    return NdotV / max(NdotV * (1.0 - k) + k, EPS);
}
float GeometrySmith(float3 N, float3 V, float3 L, float rough) {
    return GeometrySchlickGGX(max(dot(N, V), 0.0), rough) *
           GeometrySchlickGGX(max(dot(N, L), 0.0), rough);
}
float3 FresnelSchlick(float cosT, float3 F0) {
    return F0 + (1.0 - F0) * pow(1.0 - cosT, 5.0);
}

// World normal from the tangent-space normal map (BC5-safe: reconstruct Z from XY). Mirrors GL
// GetNormalFromMap (NormalFlipY + strength scaling on the tangent XY).
float3 NormalFromMap(float2 uv, float3 Ngeom, float3 T, float bitangentSign) {
    float2 nxy = NormalMap.Sample(LinearWrap, uv).rg;
    if (NormalFlipY > 0.5) nxy.y = 1.0 - nxy.y;
    float2 xy = (nxy * 2.0 - 1.0) * max(NormalStrength, 0.0);
    float z = sqrt(max(1.0 - dot(xy, xy), 0.0));
    float3 tn = normalize(float3(xy, z));
    float3 N = normalize(Ngeom);
    float3 Tn = normalize(T - N * dot(N, T));      // Gram-Schmidt
    float3 B = cross(N, Tn) * bitangentSign;
    return normalize(Tn * tn.x + B * tn.y + N * tn.z);
}

float3 ACESFilm(float3 x) {
    const float a = 2.51, b = 0.03, c = 2.43, d = 0.59, e = 0.14;
    return saturate((x * (a * x + b)) / (x * (c * x + d) + e));
}

float4 PSMain(VSOutput i) : SV_Target {
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
    float3 V = normalize(CameraPos - i.PosW);

    // Cook-Torrance direct sun (mirrors ShadeSun, without the sun-disk term — point light dir).
    float3 F0 = lerp(0.08 * SpecularReflectance.xxx, albedo, metallic);
    float3 D = normalize(LightDir);
    float NdotL = max(dot(N, D), 0.0);
    float3 diffuse = 0, specular = 0;
    if (NdotL > 0.0) {
        float3 H = normalize(V + D);
        float NDF = DistributionGGX(N, H, roughness);
        float G = GeometrySmith(N, V, D, roughness);
        float3 F = FresnelSchlick(max(dot(H, V), 0.0), F0);
        float NdotV = max(dot(N, V), 0.0);
        float3 spec = (NDF * G * F) / max(4.0 * NdotV * NdotL, EPS);
        float3 kD = (1.0 - F) * (1.0 - metallic);
        diffuse = kD * albedo / PI * LightColor * NdotL;
        specular = spec * LightColor * NdotL;
    }

    // Flat ambient (IBL stand-in next milestone), occluded by AO.
    float3 ambient = Ambient * albedo * ao;
    float3 emissive = (HasEmissive > 0.5)
        ? EmissiveMap.Sample(LinearWrap, i.Uv).rgb * EmissiveFactor : 0.0.xxx;

    float3 litHdr = diffuse + specular + ambient + emissive;
    float3 mapped = ACESFilm(litHdr * Exposure);
    float3 srgb = pow(mapped, 1.0 / 2.2);   // back to sRGB for the UNORM backbuffer/BMP
    return float4(srgb, 1.0);
}
