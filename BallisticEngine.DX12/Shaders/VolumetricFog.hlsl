// Volumetric height fog + sun shafts for the DX12 backend. Ported from the GL Volumetric_Frag.glsl:
// exponential height fog raymarched toward the camera, in-scattering the shadowed sun (HG phase, gated
// by the cascade depth) + isotropic sky ambient, with an analytic tail past the shadowed march. Output
// is (scatter.rgb, transmittance.a); the renderer blends it over the scene color with
// dest = dest*srcAlpha(=transmittance) + src(=scatter) — fog both adds glow AND extinguishes the scene.
//
// Single full-screen pass (no half-res / temporal yet — a quality follow-up). Reads scene depth + the
// shadow cascade array as SRVs; constants in a dedicated CBV.

cbuffer FogConstants : register(b0) {
    float4x4 InvViewProj;        // unjittered camera (view*proj)^-1, transposed on upload
    float4x4 Cascade0, Cascade1, Cascade2, Cascade3;
    float4   CascadeBias;
    float3   CameraPos;          float CascadeCountF;
    float3   SunDirection;       float Density;        // SunDirection = TOWARD the light
    float3   SunColor;           float HeightFalloff;  // sun radiance (pre-exposed*attenuated stand-in)
    float3   SkyAmbient;         float BaseHeight;
    float3   Tint;               float Anisotropy;
    float    Scattering; float AmbientScatter; float SunGlow; float SunGlowSharpness;
    float    StepCount; float MaxDistance; float ShadowMapTexel; float Exposure;
};

Texture2D      DepthTex      : register(t0);
Texture2DArray ShadowCascades: register(t1);
SamplerState   LinearClamp   : register(s0);

static const float PI = 3.14159265359;
static const float ALBEDO = 0.92;
static const float SKY_TAIL = 20000.0;

struct VSOut { float4 Position : SV_Position; float2 Uv : TEXCOORD0; };
VSOut VSMain(uint vid : SV_VertexID) {
    VSOut o;
    float2 uv = float2((vid << 1) & 2, vid & 2);
    o.Position = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
    o.Uv = uv;
    return o;
}

float3 WorldPos(float2 uv, float depth) {
    // DX NDC: xy [-1,1] (y up → flip uv.y), z = depth [0,1].
    float4 ndc = float4(uv.x * 2.0 - 1.0, (1.0 - uv.y) * 2.0 - 1.0, depth, 1.0);
    float4 w = mul(ndc, InvViewProj);
    return w.xyz / w.w;
}

float HG(float mu, float g) {
    float g2 = g * g;
    return (1.0 - g2) / (4.0 * PI * pow(max(1.0 + g2 - 2.0 * g * mu, 1e-4), 1.5));
}
float SigmaT(float y) {
    float f = HeightFalloff <= 0.0 ? 1.0 : exp(min(-HeightFalloff * (y - BaseHeight), 0.0));
    return Density * f;
}
float OpticalDepth(float3 o, float3 d, float t0, float t1) {
    float len = max(t1 - t0, 0.0);
    if (len <= 0.0) return 0.0;
    if (HeightFalloff <= 1e-5) return Density * len;
    float s0 = Density * exp(min(-HeightFalloff * (o.y + d.y * t0 - BaseHeight), 0.0));
    float kdy = HeightFalloff * d.y;
    if (abs(kdy) < 1e-5) return s0 * len;
    return min(s0 * (1.0 - exp(-kdy * len)) / kdy, 40.0);
}
float CascadeApply(int c, float3 wp, out float3 proj) {
    float4x4 m = c == 0 ? Cascade0 : (c == 1 ? Cascade1 : (c == 2 ? Cascade2 : Cascade3));
    float4 clip = mul(float4(wp, 1.0), m);
    proj = clip.xyz; proj.xy = proj.xy * float2(0.5, -0.5) + 0.5;
    return max(abs(clip.x), abs(clip.y));
}
float SunVisibility(float3 wp) {
    int count = (int)CascadeCountF;
    for (int c = 0; c < count; c++) {
        float3 proj; float edge = CascadeApply(c, wp, proj);
        if (edge > 1.0 || proj.z > 1.0 || proj.z < 0.0) continue;
        float d = ShadowCascades.SampleLevel(LinearClamp, float3(proj.xy, (float)c), 0).r;
        return (proj.z - CascadeBias[c]) <= d ? 1.0 : 0.0;
    }
    return 1.0;   // outside all cascades: lit
}
float IGN(float2 pix) { return frac(52.9829189 * frac(dot(pix, float2(0.06711056, 0.00583715)))); }

float4 PSMain(VSOut i) : SV_Target {
    float depth = DepthTex.SampleLevel(LinearClamp, i.Uv, 0).r;
    float3 rayStart = CameraPos;
    bool isSky = depth >= 1.0;
    float3 endPos = WorldPos(i.Uv, min(depth, 0.999999));
    float3 toEnd = endPos - rayStart;
    float surfaceDist = length(toEnd);
    float3 rayDir = toEnd / max(surfaceDist, 1e-4);
    if (isSky) surfaceDist = SKY_TAIL;
    if (surfaceDist < 1e-3) return float4(0, 0, 0, 1);

    float marchDist = min(surfaceDist, MaxDistance);
    int steps = clamp((int)StepCount, 8, 256);
    float stepLen = marchDist / steps;
    float jitter = IGN(i.Position.xy);

    float3 sunDir = normalize(SunDirection);
    float mu = dot(rayDir, sunDir);
    float g = clamp(Anisotropy, 0.0, 0.95);
    float phaseSun = lerp(HG(mu, -0.2), HG(mu, g), 0.82);
    float3 sunSource = SunColor * (phaseSun * Scattering);
    float3 ambSource = SkyAmbient * AmbientScatter;

    float3 scatter = 0; float transmittance = 1.0;
    [loop] for (int s = 0; s < steps; s++) {
        float t = (s + jitter) * stepLen;
        float3 p = rayStart + rayDir * t;
        float sigma = SigmaT(p.y);
        if (sigma > 1e-6) {
            float stepT = exp(-sigma * stepLen);
            float vis = SunVisibility(p);
            float3 src = ALBEDO * (sunSource * vis + ambSource);
            scatter += src * (transmittance * (1.0 - stepT));
            transmittance *= stepT;
            if (transmittance < 0.002) break;
        }
    }
    if (transmittance > 0.002 && surfaceDist > marchDist) {
        float tau = OpticalDepth(rayStart, rayDir, marchDist, surfaceDist);
        float tailT = exp(-tau);
        float3 src = ALBEDO * (sunSource + ambSource);
        scatter += src * (transmittance * (1.0 - tailT));
        transmittance *= tailT;
    }
    float glow = pow(max(mu, 0.0), max(SunGlowSharpness, 1.0)) * SunGlow;
    scatter += SunColor * (glow * (1.0 - transmittance));

    scatter *= Tint;
    if (any(isnan(scatter)) || any(isinf(scatter))) scatter = 0;

    // The scene color is sRGB-encoded LDR (the opaque pass tonemapped already). Tonemap+encode the
    // HDR scatter the SAME way before blending, so fog composites in the same space.
    float3 mapped = saturate(scatter * Exposure);          // already ACES'd? no — keep simple: exposure + sRGB
    float3 srgb = pow(saturate(scatter * Exposure), 1.0 / 2.2);
    return float4(srgb, saturate(transmittance));
}
