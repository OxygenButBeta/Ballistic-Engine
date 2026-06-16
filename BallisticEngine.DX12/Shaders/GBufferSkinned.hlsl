// GPU-SKINNED variant of the G-buffer geometry pass. Identical to GBuffer.hlsl in every respect EXCEPT
// the vertex stage skins position/normal/tangent by the per-bone matrices BEFORE the model transform.
// The pixel stage is byte-for-byte the same as GBuffer.hlsl::PSMain so a skinned surface shades exactly
// like a static one in the deferred lighting pass (G-buffer parity).
//
// Skinning matrices live in a StructuredBuffer (t6) uploaded per skinned draw by the renderer. Each
// matrix maps a bind-pose vertex into the animated mesh-local pose: skinned = sum(weight_i * bone_i * v).
// Bone INDICES ride in as floats (the mesh uploads them as a Vector4 float buffer, exact < 2^24) and are
// rounded back to ints here — matching the engine's GL skinning convention.

cbuffer DrawConstants : register(b0) {
    float4x4 Mvp;            // model * view * proj (transposed)
    float4x4 Model;          // model (transposed)
    float3   LightDir;       float Exposure;
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
};

// Per-bone skinning matrices for THIS draw (one skinned mesh). The renderer transposes each matrix on
// upload so mul(v, BoneMatrices[i]) here matches the row-vector convention used for Model/Mvp.
StructuredBuffer<float4x4> BoneMatrices : register(t6);

Texture2D DiffuseMap   : register(t0);
Texture2D NormalMap    : register(t1);
Texture2D MetallicMap  : register(t2);
Texture2D RoughnessMap : register(t3);
Texture2D AOMap        : register(t4);
Texture2D EmissiveMap  : register(t5);
SamplerState LinearWrap : register(s0);

struct VSInput {
    float3 Pos         : POSITION;
    float3 Normal      : NORMAL;
    float2 Uv          : TEXCOORD0;
    float4 Tangent     : TANGENT;
    float4 BoneIndices : BLENDINDICES;   // 4 bone indices as floats (rounded below)
    float4 BoneWeights : BLENDWEIGHT;     // 4 weights, sum 1
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

float2 ScreenMotion(float3 posW) {
    float4 clipCur  = mul(float4(posW, 1.0), ViewProjCur);
    float4 clipPrev = mul(float4(posW, 1.0), ViewProjPrev);
    float2 uvCur  = (clipCur.xy  / clipCur.w)  * float2(0.5, -0.5) + 0.5;
    float2 uvPrev = (clipPrev.xy / clipPrev.w) * float2(0.5, -0.5) + 0.5;
    return uvPrev - uvCur;
}

// Blends the 4 influencing bone matrices by weight into one skinning matrix. Weights already sum to 1
// (renormalized at import), so no extra normalize is needed.
float4x4 SkinMatrix(float4 indices, float4 weights) {
    int4 bi = (int4)(indices + 0.5);
    return weights.x * BoneMatrices[bi.x]
         + weights.y * BoneMatrices[bi.y]
         + weights.z * BoneMatrices[bi.z]
         + weights.w * BoneMatrices[bi.w];
}

VSOutput VSMain(VSInput v) {
    VSOutput o;

    // Skin in mesh-local (bind) space first, then apply the entity's model/Mvp exactly like the static
    // path. Normal/tangent use the upper-3x3 of the skin matrix (no translation).
    float4x4 skin = SkinMatrix(v.BoneIndices, v.BoneWeights);
    float3 skinnedPos     = mul(float4(v.Pos, 1.0), skin).xyz;
    float3 skinnedNormal  = mul(float4(v.Normal, 0.0), skin).xyz;
    float3 skinnedTangent = mul(float4(v.Tangent.xyz, 0.0), skin).xyz;

    o.Position = mul(float4(skinnedPos, 1.0), Mvp);
    o.PosW     = mul(float4(skinnedPos, 1.0), Model).xyz;
    o.NormalW  = normalize(mul(float4(skinnedNormal, 0.0), Model).xyz);
    o.TangentW = float4(normalize(mul(float4(skinnedTangent, 0.0), Model).xyz), v.Tangent.w);
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
    o.Normal   = float4(N * 0.5 + 0.5, 1.0);
    o.Material = float4(metallic, roughness, ao, Cutout > 0.5 ? 1.0 : 0.0);
    o.Emissive = float4(emissive, 1.0);
    o.Motion   = ScreenMotion(i.PosW);
    return o;
}
