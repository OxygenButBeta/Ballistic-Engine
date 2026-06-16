// Aerial perspective for the DX12 procedural sky — the #1 missing realism cue. Distant opaque geometry should
// pick up the atmosphere's blue-grey scattering haze that grows with view distance (the cue that sells scale:
// far buildings/mountains desaturate toward the sky colour). Unreal applies a 3D aerial-perspective LUT to all
// opaque surfaces; this is the fullscreen analytic equivalent — a short single-scattering march from the camera
// to each opaque pixel, blended into the RAW HDR scene BEFORE the composite tonemap so it shares the exposure.
//
// A SEPARATE pass (sky → THIS → transparents). It does NOT touch the deferred lighting shader. Sky pixels
// (depth==far) are skipped — the sky already integrates the full atmosphere column. Constants mirror the sky
// kernel's Rayleigh/Mie so the haze colour matches the sky it fades into. Near the ground the air density is
// ~uniform over the short scene-scale distances, so optical depth ≈ beta * distance (no exp height profile
// needed — the planet-scale march is for the sky, not for metre-scale geometry).

cbuffer ApConstants : register(b0) {
    float4x4 InvViewProj;   // unproject screen+depth → world (transposed on upload)
    float3   CameraPos;     float Strength;       // world camera pos; master strength (0 = off)
    float3   SunDirection;  float Distance;       // toward the sun (normalized); fade distance scale (m)
    float3   SunRadiance;   float HazeAniso;      // sun colour*illuminance (engine units); Mie phase g
    float3   SkyTint;       float AirDensity;     // ambient sky in-scatter colour; Rayleigh multiplier
    float    Haze;          float MaxDistance;    float NearFade; float _padAp;  // Mie mult; clamp dist; near-field fade-in (m)
};

static const float PI = 3.14159265359;
// Per-metre scattering coefficients at ground level (the sky kernel's betas; density ~1 near the ground).
static const float3 BetaR = float3(5.802e-6, 13.558e-6, 33.1e-6);
static const float  BetaM = 3.996e-6;

Texture2D DepthTex : register(t0);
SamplerState PointClamp : register(s0);

struct VSOut { float4 Position : SV_Position; float2 Uv : TEXCOORD0; };
VSOut VSMain(uint vid : SV_VertexID) {
    VSOut o;
    float2 uv = float2((vid << 1) & 2, vid & 2);
    o.Position = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
    o.Uv = uv;
    return o;
}

float3 WorldFromDepth(float2 uv, float depth) {
    float4 ndc = float4(uv.x * 2.0 - 1.0, (1.0 - uv.y) * 2.0 - 1.0, depth, 1.0);
    float4 w = mul(ndc, InvViewProj);
    return w.xyz / w.w;
}

float HG(float mu, float g) {
    float g2 = g * g;
    return (1.0 - g2) / (4.0 * PI * pow(max(1.0 + g2 - 2.0 * g * mu, 1e-4), 1.5));
}

float4 PSMain(VSOut i) : SV_Target {
    float depth = DepthTex.SampleLevel(PointClamp, i.Uv, 0).r;
    if (depth >= 1.0 || Strength <= 0.0)
        discard;   // sky (full column already has atmosphere) / disabled → leave the scene untouched

    float3 world = WorldFromDepth(i.Uv, depth);
    float3 toCam = world - CameraPos;
    float dist = min(length(toCam), MaxDistance);
    if (dist < 1.0) discard;
    float3 viewDir = toCam / max(length(toCam), 1e-4);

    // Optical depth, scene-scale calibrated. The raw Rayleigh betas only matter over kilometres; scenes are
    // tens-to-hundreds of metres, so Distance is the artistic HALF-DISTANCE (metres at which haze is strong)
    // and the per-channel COLOUR comes from the beta ratio (Rayleigh's blue tilt). grey = how much haze total.
    // NEAR-FIELD FADE (V3, fixes D2 — the blue veil over interiors): the in-scatter colour is the lux-scaled
    // sky radiance (SkyTint ≈ sunRadiance*blue, thousands of units), so even a tiny optical depth at interior
    // distances (~10 m) painted a visible blue veil on every opaque pixel. Fade the haze in over [NearFade,
    // 2*NearFade] m so short-range / enclosed geometry gets ~zero aerial perspective while distant vistas keep
    // the full atmosphere cue. NearFade=0 restores the pre-V3 linear-from-zero behaviour (kill-switch).
    float nearFade = (NearFade > 0.0) ? smoothstep(NearFade, 2.0 * NearFade, dist) : 1.0;
    float grey = (dist / max(Distance, 1.0)) * Strength * nearFade; // ~1 at Distance, grows beyond; ~0 near camera
    float3 betaColR = (BetaR * AirDensity) / max(BetaR.r, 1e-9);  // normalized Rayleigh colour (blue-biased)
    float3 betaColM = (float3)(1.11 * Haze);                      // Mie is grey
    float3 tauR = grey * betaColR;
    float3 tauM = grey * 0.25 * betaColM;                         // Mie contributes less haze than Rayleigh here
    float3 transmittance = exp(-(tauR + tauM));

    // In-scattered light: sun (Rayleigh + Mie phase) + an ambient sky term filling shadowed haze. The haze
    // colour is the sky colour (SkyTint) plus a sun-phase highlight, normalized so it doesn't depend on the
    // raw radiance magnitude (the scene is pre-tonemap raw radiance; keep haze in the same neighbourhood).
    float mu = dot(viewDir, SunDirection);
    float phaseR = 3.0 / (16.0 * PI) * (1.0 + mu * mu);
    float g = clamp(HazeAniso, -0.95, 0.95);
    float phaseM = HG(mu, g);
    // Sun-lit haze + ambient sky haze. SkyTint already carries the engine-radiance scale (sunRadiance * blue).
    float3 sunHaze = SkyTint * (phaseR + phaseM * 0.6) * 2.0;
    float3 ambHaze = SkyTint;
    float3 hazeColor = sunHaze + ambHaze;

    float3 inscatter = hazeColor * (1.0 - transmittance);
    inscatter = min(inscatter, (float3)60000.0);   // fp16 safety

    // Composite as the fog pass does: blend = dest*srcAlpha(transmittance) + src(inscatter). A single scalar
    // transmittance (luma-averaged) drives the dst multiply — haze colour comes from the additive inscatter,
    // the transmittance just dims the distant scene. RGB transmittance would need a 2nd MRT (a follow-up).
    float avgT = dot(transmittance, (float3)0.33333);
    return float4(inscatter, avgT);
}
