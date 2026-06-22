// Lumen FAZ 3c — CARD CAPTURE. For each surface-cache card we orthographically rasterize the card's mesh from the
// card's front-face viewpoint (the C# side builds the ortho View*Proj into Mvp) and write the mesh's MATERIAL
// ATTRIBUTES into that card's page rect in the physical atlas. NO lighting yet (FAZ 3d does that) — this pass only
// fills albedo / card-space normal / emissive / card-space depth, exactly as UE's surface-cache capture does.
//
// VS = the GBuffer.hlsl VSMain transform (POSITION/NORMAL/TEXCOORD/TANGENT in; mul(pos,Mvp) clip, mul(pos,Model)
// world pos + world normal/tangent). PS samples the SAME 6 material textures + scalar fields as GBuffer, then packs
// 4 MRTs. The card axes ride the CB so the PS can rotate the world normal into card space and compute card-depth.
//
// Driver note (FAZ 3b lesson): keep the color math branch/int/lerp-free + saturate-clamped (this GPU mis-compiled
// loop-carried int + branch/lerp color to all-white). NaN scrubs are ternary SELECTS, never lerp(v,0,flag).

cbuffer LumenCaptureConstants : register(b0) {
    float4x4 Mvp;            // model * cardView * cardProj (transposed on upload) — world→card clip
    float4x4 Model;          // model (transposed) — mesh-local → world for the normal/pos
    float3   CardAxisX;      float pad0;     // card right (unit, world)
    float3   CardAxisY;      float pad1;     // card up    (unit, world)
    float3   CardAxisZ;      float CardExtentZ;   // card outward normal (unit, world) + world depth half-extent
    float3   CardOrigin;     float pad2;     // card center (world)
    // ---- material scalars (mirror GBuffer's DrawConstants subset) ----
    float4   BaseColorFactor;
    float3   EmissiveFactor; float HasEmissive;
    float    Metallic;       float Roughness;     float NormalStrength; float NormalFlipY;
    float    HasMetallicMap; float HasRoughnessMap; float PackedOrm;    float Cutout;
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
struct CaptureOut {
    float4 Albedo   : SV_Target0;   // R8G8B8A8_UNorm  : rgb albedo, a opacity
    float4 Normal   : SV_Target1;   // R8G8_UNorm      : card-space normal.xy*0.5+0.5 (only .rg used)
    float4 Emissive : SV_Target2;   // R11G11B10_Float : emissive radiance
    float  Depth    : SV_Target3;   // R16_Float       : card-space linear depth [0,1]
};

VSOutput VSMain(VSInput v) {
    VSOutput o;
    o.Position = mul(float4(v.Pos, 1.0), Mvp);
    o.PosW     = mul(float4(v.Pos, 1.0), Model).xyz;
    o.NormalW  = normalize(mul(float4(v.Normal, 0.0), Model).xyz);
    o.TangentW = float4(normalize(mul(float4(v.Tangent.xyz, 0.0), Model).xyz), v.Tangent.w);
    o.Uv       = v.Uv;
    return o;
}

// Same tangent-space normal-map application as GBuffer.NormalFromMap (no LOD bias — capture is one fixed mip view).
float3 NormalFromMap(float2 uv, float3 Ngeom, float3 T, float bitangentSign) {
    float2 nxy = NormalMap.Sample(LinearWrap, uv).rg;
    if (NormalFlipY > 0.5) nxy.y = 1.0 - nxy.y;
    float2 xy = (nxy * 2.0 - 1.0) * max(NormalStrength, 0.0);
    float z = sqrt(max(1.0 - dot(xy, xy), 0.0));
    float3 tn = normalize(float3(xy, z));
    float3 N  = normalize(Ngeom);
    float3 Tn = normalize(T - N * dot(N, T));
    float3 B  = cross(N, Tn) * bitangentSign;
    return normalize(Tn * tn.x + B * tn.y + N * tn.z);
}

CaptureOut PSMain(VSOutput i) {
    float4 albedoSample = DiffuseMap.Sample(LinearWrap, i.Uv);
    if (Cutout > 0.5 && albedoSample.a < 0.5) discard;
    float3 albedo  = albedoSample.rgb * BaseColorFactor.rgb;
    float  opacity = albedoSample.a * BaseColorFactor.a;

    float3 emissive = (HasEmissive > 0.5) ? EmissiveMap.Sample(LinearWrap, i.Uv).rgb * EmissiveFactor : float3(0, 0, 0);

    // World normal (with normal map) → CARD space. Only XY are stored (Z reconstructed in the sampler). NaN/denorm
    // scrub via ternary select (never lerp): a bad normal becomes the flat card normal (cardN.xy = 0 → output 0.5).
    float3 Nworld = NormalFromMap(i.Uv, i.NormalW, i.TangentW.xyz, i.TangentW.w);
    float2 cardN  = float2(dot(Nworld, CardAxisX), dot(Nworld, CardAxisY));
    bool   bad    = any(isnan(cardN)) || any(isinf(cardN));
    cardN = bad ? float2(0, 0) : clamp(cardN, -1.0, 1.0);

    // Card-space linear depth in [0,1]: distance of this surface BEHIND the front face along the inward direction
    // (-CardAxisZ), normalized by the full card depth slab (2*CardExtentZ). Front face center = Origin + AxisZ*ExtentZ.
    float3 frontCenter = CardOrigin + CardAxisZ * CardExtentZ;
    float  slab        = max(2.0 * CardExtentZ, 1e-6);
    float  depth       = saturate(dot(i.PosW - frontCenter, -CardAxisZ) / slab);

    CaptureOut o;
    o.Albedo   = float4(saturate(albedo), saturate(opacity));
    o.Normal   = float4(saturate(cardN * 0.5 + 0.5), 0.0, 1.0);
    o.Emissive = float4(max(emissive, 0.0.xxx), 0.0);
    o.Depth    = depth;
    return o;
}
