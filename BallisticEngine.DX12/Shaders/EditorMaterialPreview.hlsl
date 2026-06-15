// Editor material-thumbnail shader (DX12 port of EditorMaterialPreview_*.glsl): a UV sphere shaded with
// the material's base colour, albedo + normal maps, a roughness/metallic specular and a rim, gamma-encoded.
// Matrices uploaded transposed + multiplied vector-first (codebase convention).
cbuffer Cb : register(b0) {
    float4x4 Mvp;
    float4 BaseColor;
    float Roughness;
    float Metallic;
    float HasAlbedo;   // 1 = sample AlbedoMap, else base colour only
    float HasNormal;   // 1 = perturb by NormalMap
}

Texture2D AlbedoMap : register(t0);
Texture2D NormalMap : register(t1);
SamplerState Samp : register(s0);

struct VSIn { float3 pos : POSITION; float3 normal : NORMAL; float2 uv : TEXCOORD; };
struct PSIn { float4 pos : SV_POSITION; float3 n : NORMAL; float2 uv : TEXCOORD; };

PSIn VSMain(VSIn i) {
    PSIn o;
    o.pos = mul(float4(i.pos, 1.0), Mvp);
    o.n = i.normal;
    o.uv = i.uv;
    return o;
}

float4 PSMain(PSIn i) : SV_Target {
    float3 N = normalize(i.n);
    if (HasNormal > 0.5) {
        float3 nm = NormalMap.Sample(Samp, i.uv).rgb * 2.0 - 1.0;
        N = normalize(N + nm * 0.6);
    }
    float3 L = normalize(float3(0.45, 0.65, 0.7));
    float3 V = float3(0.0, 0.0, 1.0);
    float3 H = normalize(L + V);
    float3 albedo = BaseColor.rgb;
    if (HasAlbedo > 0.5) albedo *= AlbedoMap.Sample(Samp, i.uv).rgb;
    float ndl = dot(N, L);
    float diff = saturate(ndl * 0.5 + 0.5); diff = diff * diff;
    float shininess = lerp(8.0, 200.0, 1.0 - Roughness);
    float spec = pow(max(dot(N, H), 0.0), shininess) * (1.0 - Roughness);
    float3 specColor = lerp(float3(1.0, 1.0, 1.0), albedo, Metallic);
    float rim = pow(1.0 - max(dot(N, V), 0.0), 3.0) * 0.25;
    float3 lit = albedo * (0.12 + diff * 0.95) + specColor * spec + rim;
    lit = pow(saturate(lit), 1.0 / 2.2);
    return float4(lit, 1.0);
}
