// Horizon-based ambient occlusion (HBAO) for the DX12 backend, ported from the GL SSAO_Frag.glsl.
// Reconstructs the view-space position + normal from scene depth, marches a few azimuthal slices and
// tracks the max elevation above the tangent plane → graded contact darkening in crevices (flat open
// ground reads zero occlusion). Output is AO in R (1 = unoccluded). A separable depth-aware blur
// (BlurH/BlurV) softens the noise; the result multiplies the HDR scene color before the composite.
//
// In the GL renderer SSAO runs pre-opaque so it scales only ambient; this DX12 forward path applies it
// as a post multiply on the final HDR color (cheap + effective; a z-prepass split is a later refinement).

cbuffer SsaoConstants : register(b0) {
    float4x4 Projection;     // camera projection (DX z[0,1]), transposed on upload
    float4x4 InvProjection;  // its inverse, transposed
    float Radius;            // world-space falloff radius
    float Intensity;         // AO strength
    float2 TexelSize;        // 1 / AO-buffer size
};

Texture2D DepthTex : register(t0);
Texture2D AoTex    : register(t0);   // alias for the blur passes (same register, different bind)
SamplerState PointClamp : register(s0);

static const float PI = 3.14159265359;
static const int SLICES = 4;
static const int STEPS = 6;

struct VSOut { float4 Position : SV_Position; float2 Uv : TEXCOORD0; };
VSOut VSMain(uint vid : SV_VertexID) {
    VSOut o;
    float2 uv = float2((vid << 1) & 2, vid & 2);
    o.Position = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
    o.Uv = uv;
    return o;
}

float Rand(float2 co) { return frac(sin(dot(co, float2(12.9898, 78.233))) * 43758.5453); }

float3 ViewPos(float2 uv) {
    float depth = DepthTex.SampleLevel(PointClamp, uv, 0).r;
    // DX NDC: xy [-1,1] (uv.y flipped), z = depth [0,1].
    float4 ndc = float4(uv.x * 2.0 - 1.0, (1.0 - uv.y) * 2.0 - 1.0, depth, 1.0);
    float4 v = mul(ndc, InvProjection);
    return v.xyz / v.w;
}

float3 ReconstructNormal(float2 uv, float3 P) {
    float3 pL = ViewPos(uv - float2(TexelSize.x, 0));
    float3 pR = ViewPos(uv + float2(TexelSize.x, 0));
    float3 pD = ViewPos(uv - float2(0, TexelSize.y));
    float3 pU = ViewPos(uv + float2(0, TexelSize.y));
    float3 dx = abs(pR.z - P.z) < abs(P.z - pL.z) ? (pR - P) : (P - pL);
    float3 dy = abs(pU.z - P.z) < abs(P.z - pD.z) ? (pU - P) : (P - pD);
    return normalize(cross(dx, dy));
}

float4 PSMain(VSOut i) : SV_Target {
    float depth = DepthTex.SampleLevel(PointClamp, i.Uv, 0).r;
    if (depth >= 1.0) return 1.0.xxxx;   // sky: unoccluded

    float3 P = ViewPos(i.Uv);
    // View Z is negative looking forward (RH). Project the world radius to a pixel march length.
    float radiusPx = Radius / max(-P.z, 1e-3) * (0.5 / TexelSize.y);
    radiusPx = clamp(radiusPx, 2.0, 0.3 / TexelSize.y);
    float3 N = ReconstructNormal(i.Uv, P);

    float noise = Rand(i.Uv * 197.0);
    float occlusion = 0.0;
    const float angleBias = 0.15;
    [unroll] for (int s = 0; s < SLICES; s++) {
        float phi = (s + noise) * PI / SLICES;
        float2 dir = float2(cos(phi), sin(phi));
        float maxElev = 0.0;
        [unroll] for (int t = 1; t <= STEPS; t++) {
            float frac = (t - 0.5 + noise) / STEPS;
            float2 off = dir * frac * radiusPx * TexelSize;
            float3 sv = ViewPos(i.Uv + off) - P;
            float dist = length(sv);
            if (dist < 1e-4 || dist > Radius) continue;
            float elevation = dot(sv / dist, N);
            float falloff = saturate(1.0 - (dist / Radius) * (dist / Radius));
            maxElev = max(maxElev, (elevation - angleBias) * falloff);
        }
        occlusion += saturate(maxElev);
    }
    occlusion /= SLICES;
    return saturate(1.0 - occlusion * Intensity).xxxx;
}

// Separable 5-tap blur over the AO (reads AoTex at t0 — bound separately for these passes).
float4 BlurDir(float2 uv, float2 dir) {
    float sum = AoTex.SampleLevel(PointClamp, uv, 0).r * 0.4;
    sum += AoTex.SampleLevel(PointClamp, uv + dir * TexelSize, 0).r * 0.24;
    sum += AoTex.SampleLevel(PointClamp, uv - dir * TexelSize, 0).r * 0.24;
    sum += AoTex.SampleLevel(PointClamp, uv + dir * 2 * TexelSize, 0).r * 0.06;
    sum += AoTex.SampleLevel(PointClamp, uv - dir * 2 * TexelSize, 0).r * 0.06;
    return sum.xxxx;
}
float4 PSBlurH(VSOut i) : SV_Target { return BlurDir(i.Uv, float2(1, 0)); }
float4 PSBlurV(VSOut i) : SV_Target { return BlurDir(i.Uv, float2(0, 1)); }
