// Procedural physically-based sky for the DX12 backend — Rayleigh + Mie + ozone single-scattering
// atmosphere + sun disk + virtual ground, sampled DIRECTLY by view direction (no cubemap bake; the GL
// path bakes to a cube, DX12 marches per-pixel in the skybox pass). Ported from the GL Sky_Procedural.glsl
// main() clean-sky path (clouds/cirrus/stars are a follow-up — this is the atmosphere gradient + sun).
// Output radiance is engine physical scale (SunRadiance carries lux→radiance); ACES-tonemapped here to
// match the opaque pass's LDR output.
//
// Drawn like the cubemap skybox: a 36-vert SV_VertexID cube at the far plane (LEqual, no depth write),
// filling only pixels geometry didn't cover. Constants MUST match ProceduralSkyConstants in DX12HDRenderer.

cbuffer ProcSkyConstants : register(b0) {
    float4x4 ViewProjNoTranslate; // (rotation-only view) * proj, transposed on upload
    float3   SunDirection;  float SunAngularRadius;   // toward the sun (normalized); disk radius (rad)
    float3   SunRadiance;   float SunDiskIntensity;    // sun color*illuminance (engine units); disk scale
    float3   GroundAlbedo;  float AirDensity;          // virtual ground reflectance; Rayleigh mult
    float    Haze;          float HazeAnisotropy; float OzoneDensity; float MultiScatter;
    float    Exposure;      float BakeFace;  float2 _pad;   // BakeFace: cube face index for the env bake
};

static const float PI = 3.14159265359;
static const float Rp = 6360e3;        // planet radius (m)
static const float Ra = 6460e3;        // atmosphere top (m)
static const float3 BetaR = float3(5.802e-6, 13.558e-6, 33.1e-6);
static const float  BetaM = 3.996e-6;
static const float3 BetaO = float3(0.650e-6, 1.881e-6, 0.085e-6);
static const float Hr = 8500.0;
static const float Hm = 1200.0;
static const int VIEW_STEPS = 32;
static const int LIGHT_STEPS = 8;

struct VSOutput { float4 Position : SV_Position; float3 Dir : TEXCOORD0; };

static const float3 CubeVerts[36] = {
    float3(-1,-1, 1), float3( 1,-1, 1), float3( 1, 1, 1), float3( 1, 1, 1), float3(-1, 1, 1), float3(-1,-1, 1),
    float3(-1,-1,-1), float3(-1, 1,-1), float3( 1, 1,-1), float3( 1, 1,-1), float3( 1,-1,-1), float3(-1,-1,-1),
    float3(-1,-1,-1), float3(-1,-1, 1), float3(-1, 1, 1), float3(-1, 1, 1), float3(-1, 1,-1), float3(-1,-1,-1),
    float3( 1,-1,-1), float3( 1, 1,-1), float3( 1, 1, 1), float3( 1, 1, 1), float3( 1,-1, 1), float3( 1,-1,-1),
    float3(-1, 1,-1), float3(-1, 1, 1), float3( 1, 1, 1), float3( 1, 1, 1), float3( 1, 1,-1), float3(-1, 1,-1),
    float3(-1,-1,-1), float3( 1,-1,-1), float3( 1,-1, 1), float3( 1,-1, 1), float3(-1,-1, 1), float3(-1,-1,-1),
};

float ExitSphere(float3 o, float3 d, float R) {
    float b = dot(o, d); float c = dot(o, o) - R * R;
    return -b + sqrt(max(b * b - c, 0.0));
}
float HitGround(float3 o, float3 d) {
    float b = dot(o, d); float c = dot(o, o) - Rp * Rp;
    float h = b * b - c;
    if (h < 0.0) return -1.0;
    float t = -b - sqrt(h);
    return t > 0.0 ? t : -1.0;
}
float3 Densities(float3 p) {
    float h = max(length(p) - Rp, 0.0);
    float ozone = max(0.0, 1.0 - abs(h - 25000.0) / 15000.0);
    return float3(exp(-h / Hr), exp(-h / Hm), ozone);
}
float3 Extinction(float3 depths) {
    return BetaR * AirDensity * depths.x + BetaM * 1.11 * Haze * depths.y + BetaO * OzoneDensity * depths.z;
}
float3 SunDepths(float3 p) {
    float seg = ExitSphere(p, SunDirection, Ra) / float(LIGHT_STEPS);
    float3 depths = 0;
    for (int j = 0; j < LIGHT_STEPS; j++)
        depths += Densities(p + SunDirection * ((float(j) + 0.5) * seg)) * seg;
    return depths;
}

// Atmosphere radiance toward `dir` (clean sky: scatter + ground + sun disk). Mirrors GL main().
float3 SkyRadiance(float3 dir) {
    float3 origin = float3(0.0, Rp + 500.0, 0.0);
    float tGround = HitGround(origin, dir);
    bool ground = tGround > 0.0;
    float tMax = ground ? tGround : ExitSphere(origin, dir, Ra);

    float mu = dot(dir, SunDirection);
    float phaseR = 3.0 / (16.0 * PI) * (1.0 + mu * mu);
    float g = clamp(HazeAnisotropy, -0.99, 0.99); float g2 = g * g;
    float phaseM = 3.0 / (8.0 * PI) * ((1.0 - g2) * (1.0 + mu * mu)) /
                   ((2.0 + g2) * pow(1.0 + g2 - 2.0 * g * mu, 1.5));

    float3 viewDepths = 0, sumR = 0, sumM = 0;
    float seg = tMax / float(VIEW_STEPS);
    for (int i = 0; i < VIEW_STEPS; i++) {
        float3 p = origin + dir * ((float(i) + 0.5) * seg);
        float3 d = Densities(p) * seg;
        viewDepths += d;
        if (HitGround(p, SunDirection) < 0.0) {
            float3 atten = exp(-Extinction(viewDepths + SunDepths(p)));
            sumR += atten * d.x;
            sumM += atten * d.y;
        }
    }

    float3 sky = (sumR * BetaR * AirDensity * phaseR + sumM * BetaM * Haze * phaseM)
               * SunRadiance * max(MultiScatter, 1.0);
    float3 viewTrans = exp(-Extinction(viewDepths));

    if (ground) {
        float3 p = origin + dir * tGround;
        float3 up = normalize(p);
        float ndl = max(dot(up, SunDirection), 0.0);
        float3 sunAtGround = exp(-Extinction(SunDepths(p)));
        sky += GroundAlbedo / PI * SunRadiance * sunAtGround * ndl * viewTrans;
    }
    else if (mu > cos(SunAngularRadius)) {
        float solidAngle = 2.0 * PI * (1.0 - cos(SunAngularRadius));
        float r = clamp(acos(clamp(mu, -1.0, 1.0)) / SunAngularRadius, 0.0, 1.0);
        float limb = 1.0 - 0.6 * (1.0 - sqrt(max(1.0 - r * r, 0.0)));
        float3 disk = SunRadiance / max(solidAngle, 1e-6) * (SunDiskIntensity * limb);
        sky += min(disk * viewTrans, 60000.0.xxx);
    }
    return max(sky * Exposure, 0.0.xxx);
}

float3 ACESFilm(float3 x) {
    const float a = 2.51, b = 0.03, c = 2.43, d = 0.59, e = 0.14;
    return saturate((x * (a * x + b)) / (x * (c * x + d) + e));
}

VSOutput VSMain(uint vid : SV_VertexID) {
    VSOutput o;
    float4 pos = mul(float4(CubeVerts[vid], 1.0), ViewProjNoTranslate);
    o.Position = pos.xyww;          // depth 1.0 → far plane (LEqual fills uncovered pixels)
    o.Dir = CubeVerts[vid];
    return o;
}

float4 PSMain(VSOutput i) : SV_Target {
    // RAW HDR sky radiance into the R16F scene target — the composite pass does exposure + ACES + sRGB.
    return float4(SkyRadiance(normalize(i.Dir)), 1.0);
}

// ---- Env-cube BAKE: render RAW HDR sky radiance into one cube face (FSQ) for IBL convolution. ----
// Same atmosphere math, no tonemap/exposure-fold beyond SkyRadiance's own Exposure, so the irradiance/
// prefilter passes integrate true radiance. Face index comes from the CBV's BakeFace slot.
float3 EnvFaceDir(int face, float2 uv) {
    float2 st = uv * 2.0 - 1.0;
    if (face == 0) return float3( 1.0, -st.y, -st.x);
    if (face == 1) return float3(-1.0, -st.y,  st.x);
    if (face == 2) return float3( st.x,  1.0,  st.y);
    if (face == 3) return float3( st.x, -1.0, -st.y);
    if (face == 4) return float3( st.x, -st.y,  1.0);
    return float3(-st.x, -st.y, -1.0);
}
struct VSBakeOut { float4 Position : SV_Position; float2 Uv : TEXCOORD0; };
VSBakeOut VSEnvBake(uint vid : SV_VertexID) {
    VSBakeOut o;
    float2 uv = float2((vid << 1) & 2, vid & 2);
    o.Position = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
    o.Uv = uv;
    return o;
}
float4 PSEnvBake(VSBakeOut i) : SV_Target {
    float3 dir = normalize(EnvFaceDir((int)BakeFace, i.Uv));
    return float4(SkyRadiance(dir), 1.0);
}
