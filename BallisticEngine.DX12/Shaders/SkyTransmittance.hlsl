// Transmittance LUT for the DX12 procedural sky (Hillaire 2020 "A Scalable and Production Ready Sky and
// Atmosphere"). A 256x64 RGBA16F table of the atmosphere's transmittance exp(-opticalDepth) from a point at
// altitude h, looking along a direction whose cosine with the planet-up is mu, out to the top of the
// atmosphere. The sky kernel reads this instead of re-marching the Rayleigh/Mie/ozone optical depth toward
// the sun for every cloud/cirrus/ground sample — the same numbers, one fetch. The renderer ALSO samples it
// CPU-side to redden/dim the directional sun at low elevations (real golden-hour, which DX12 lacked).
//
// Constants MUST match ProceduralSky.hlsl (BetaR/BetaM/BetaO, Hr/Hm, Rp/Ra) and the atmosphere-param cbuffer.

cbuffer TransmittanceConstants : register(b0) {
    float AirDensity;   // Rayleigh multiplier  (ProceduralSky AirDensity)
    float Haze;         // Mie multiplier       (ProceduralSky Haze)
    float OzoneDensity; // ozone multiplier     (ProceduralSky OzoneDensity)
    float _padT;
};

static const float PI = 3.14159265359;
static const float Rp = 6360e3;        // planet radius (m)
static const float Ra = 6460e3;        // atmosphere top (m)
static const float3 BetaR = float3(5.802e-6, 13.558e-6, 33.1e-6);
static const float  BetaM = 3.996e-6;
static const float3 BetaO = float3(0.650e-6, 1.881e-6, 0.085e-6);
static const float Hr = 8500.0;
static const float Hm = 1200.0;
static const int   STEPS = 40;         // optical-depth integration steps (LUT bake is cheap, use more)

struct VSOut { float4 Position : SV_Position; float2 Uv : TEXCOORD0; };
VSOut VSMain(uint vid : SV_VertexID) {
    VSOut o;
    float2 uv = float2((vid << 1) & 2, vid & 2);
    o.Position = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
    o.Uv = uv;
    return o;
}

float3 Densities(float r) {
    float h = max(r - Rp, 0.0);
    float ozone = max(0.0, 1.0 - abs(h - 25000.0) / 15000.0);
    return float3(exp(-h / Hr), exp(-h / Hm), ozone);
}
float3 Extinction(float3 depths) {
    return BetaR * AirDensity * depths.x + BetaM * 1.11 * Haze * depths.y + BetaO * OzoneDensity * depths.z;
}

// Distance from radius r, cos(zenith)=mu, to the top of the atmosphere (Ra). Always exits (origin inside).
float DistToTop(float r, float mu) {
    float disc = r * r * (mu * mu - 1.0) + Ra * Ra;
    return max(-r * mu + sqrt(max(disc, 0.0)), 0.0);
}

// Bruneton mapping: UV.x -> mu (cos view-zenith), UV.y -> altitude. Horizon-dense in mu for accuracy near
// grazing angles (where transmittance changes fastest). Inverse of the standard r/mu <-> uv mapping.
void UvToRMu(float2 uv, out float r, out float mu) {
    float H = sqrt(Ra * Ra - Rp * Rp);            // horizon distance at the ground
    float rho = H * uv.y;                          // distance to the horizon for this altitude band
    r = sqrt(rho * rho + Rp * Rp);
    float dMin = Ra - r, dMax = rho + H;
    float d = dMin + uv.x * (dMax - dMin);
    mu = (d == 0.0) ? 1.0 : clamp((H * H - rho * rho - d * d) / (2.0 * r * d), -1.0, 1.0);
}

float4 PSMain(VSOut i) : SV_Target {
    float r, mu;
    UvToRMu(i.Uv, r, mu);

    float t1 = DistToTop(r, mu);
    float3 depths = 0.0;
    float seg = t1 / float(STEPS);
    [loop] for (int s = 0; s < STEPS; s++) {
        float t = (float(s) + 0.5) * seg;
        // radius at step t along a ray from radius r with cos(zenith)=mu (law of cosines)
        float ri = sqrt(max(t * t + 2.0 * r * mu * t + r * r, Rp * Rp));
        depths += Densities(ri) * seg;
    }
    float3 transmittance = exp(-Extinction(depths));
    return float4(transmittance, 1.0);
}
