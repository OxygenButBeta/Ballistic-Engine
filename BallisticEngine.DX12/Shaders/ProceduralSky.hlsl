// Procedural physically-based sky for the DX12 backend - Rayleigh + Mie + ozone single-scattering
// atmosphere + a raymarched volumetric cumulus layer + a thin cirrus sheet + a night starfield + the sun
// disk + a virtual ground, sampled DIRECTLY by view direction (no cubemap bake for the background; the GL
// path bakes to a cube, DX12 marches per-pixel in the skybox pass). Ported from GL Sky_Procedural.glsl.
// SkyRadiance() is the shared kernel: PSMain draws it as the far-plane background and PSEnvBake renders it
// into the IBL env cube, so clouds/cirrus/stars show up in reflections and IBL ambient for free (GL parity).
// Output radiance is engine physical scale (SunRadiance carries lux->radiance); the composite does ACES.
//
// Drawn like the cubemap skybox: a 36-vert SV_VertexID cube at the far plane (LEqual, no depth write),
// filling only pixels geometry did not cover. Constants MUST match ProcSkyConstants in DX12HDRenderer.cs
// AND Dx12IblBaker.cs (extend all three identically).

cbuffer ProcSkyConstants : register(b0) {
    float4x4 ViewProjNoTranslate; // (rotation-only view) * proj, transposed on upload
    float3   SunDirection;  float SunAngularRadius;   // toward the sun (normalized); disk radius (rad)
    float3   SunRadiance;   float SunDiskIntensity;    // sun color*illuminance (engine units); disk scale
    float3   GroundAlbedo;  float AirDensity;          // virtual ground reflectance; Rayleigh mult
    float    Haze;          float HazeAnisotropy; float OzoneDensity; float MultiScatter;
    float    Exposure;      float BakeFace;  float2 _pad0;   // BakeFace: cube face index for the env bake
    // --- Volumetric clouds + cirrus + stars (GL Sky_Procedural.glsl parity) ---
    float    CloudsEnabled; float CloudCoverage;  float CloudDensity;   float CloudAltitude;
    float    CloudThickness;float CloudScale;     float CloudDetail;    float CloudAmbient;
    float3   CloudWindOffset; float CloudWindAngle;       // wind dir*speed*time (m); cirrus streak angle (rad)
    float    CirrusCoverage;float StarIntensity;  float2 _pad1;
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

// ===== Noise =================================================================
// The cumulus base shape uses Perlin-dilated-by-Worley (the Nubis construction): inverted Worley supplies
// the cauliflower clumping real cumulus have, the Perlin breaks its cellular regularity.
float Hash3(float3 p) {
    p = frac(p * 0.1031 + float3(0.17, 0.39, 0.61));
    p += dot(p, p.yzx + 19.19);
    return frac((p.x + p.y) * p.z);
}
float3 Hash33(float3 p) {
    p = frac(p * float3(0.1031, 0.1030, 0.0973));
    p += dot(p, p.yxz + 33.33);
    return frac((p.xxy + p.yxx) * p.zyx);
}
float ValueNoise(float3 p) {
    float3 i = floor(p);
    float3 f = frac(p);
    f = f * f * (3.0 - 2.0 * f);
    float n000 = Hash3(i);
    float n100 = Hash3(i + float3(1, 0, 0));
    float n010 = Hash3(i + float3(0, 1, 0));
    float n110 = Hash3(i + float3(1, 1, 0));
    float n001 = Hash3(i + float3(0, 0, 1));
    float n101 = Hash3(i + float3(1, 0, 1));
    float n011 = Hash3(i + float3(0, 1, 1));
    float n111 = Hash3(i + float3(1, 1, 1));
    return lerp(lerp(lerp(n000, n100, f.x), lerp(n010, n110, f.x), f.y),
                lerp(lerp(n001, n101, f.x), lerp(n011, n111, f.x), f.y), f.z);
}
float Fbm(float3 p, int octaves) {
    float amp = 0.55, sum = 0.0, norm = 0.0;
    [loop] for (int i = 0; i < octaves; i++) {
        sum += amp * ValueNoise(p);
        norm += amp;
        amp *= 0.5;
        p = p * 2.17 + float3(31.7, 11.3, 7.9);
    }
    return sum / norm;
}
// Perlin gradient noise, ~[-1, 1].
float GradNoise(float3 p) {
    float3 i = floor(p);
    float3 f = frac(p);
    float3 u = f * f * (3.0 - 2.0 * f);
    float n000 = dot(Hash33(i) * 2.0 - 1.0, f);
    float n100 = dot(Hash33(i + float3(1, 0, 0)) * 2.0 - 1.0, f - float3(1, 0, 0));
    float n010 = dot(Hash33(i + float3(0, 1, 0)) * 2.0 - 1.0, f - float3(0, 1, 0));
    float n110 = dot(Hash33(i + float3(1, 1, 0)) * 2.0 - 1.0, f - float3(1, 1, 0));
    float n001 = dot(Hash33(i + float3(0, 0, 1)) * 2.0 - 1.0, f - float3(0, 0, 1));
    float n101 = dot(Hash33(i + float3(1, 0, 1)) * 2.0 - 1.0, f - float3(1, 0, 1));
    float n011 = dot(Hash33(i + float3(0, 1, 1)) * 2.0 - 1.0, f - float3(0, 1, 1));
    float n111 = dot(Hash33(i + float3(1, 1, 1)) * 2.0 - 1.0, f - float3(1, 1, 1));
    return lerp(lerp(lerp(n000, n100, u.x), lerp(n010, n110, u.x), u.y),
                lerp(lerp(n001, n101, u.x), lerp(n011, n111, u.x), u.y), u.z);
}
float PerlinFbm(float3 p, int octaves) {
    float amp = 0.55, sum = 0.0, norm = 0.0;
    [loop] for (int i = 0; i < octaves; i++) {
        sum += amp * GradNoise(p);
        norm += amp;
        amp *= 0.5;
        p = p * 2.13 + float3(19.1, 33.4, 47.2);
    }
    return sum / norm * 0.5 + 0.5;
}
// Inverted Worley: 1 at cell feature points falling to 0 between them - each cell reads as one billow.
float WorleyInv(float3 p) {
    float3 i = floor(p);
    float3 f = frac(p);
    float dmin = 1e9;
    for (int x = -1; x <= 1; x++)
    for (int y = -1; y <= 1; y++)
    for (int z = -1; z <= 1; z++) {
        float3 c = float3(x, y, z);
        float3 q = Hash33(i + c) + c - f;
        dmin = min(dmin, dot(q, q));
    }
    return 1.0 - sqrt(min(dmin, 1.0));
}
float Remap(float v, float lo, float hi, float newLo, float newHi) {
    return newLo + (v - lo) / max(hi - lo, 1e-5) * (newHi - newLo);
}

// ===== Volumetric clouds =====================================================
static const float CLOUD_BASE_FREQ = 1.0 / 9000.0; // base shape features ~9 km at CloudScale 1
static const float CLOUD_EXTINCTION = 0.0035;      // m^-1 inside a dense core at CloudDensity 1
static const float CLOUD_ALBEDO = 0.97;            // single-scatter albedo of water droplets
static const float CLOUD_STOPS[6] = { 0.04, 0.1, 0.2, 0.35, 0.6, 1.0 };

float CloudHeight01(float3 p) {
    return (length(p) - Rp - CloudAltitude) / max(CloudThickness, 1.0);
}

// Cloud extinction coefficient (m^-1) at p. cheap=true drops noise octaves and the edge-erosion detail.
float CloudSigma(float3 p, float h01, bool cheap) {
    if (h01 <= 0.0 || h01 >= 1.0) return 0.0;

    float3 sp = (p + CloudWindOffset) * (CLOUD_BASE_FREQ / CloudScale);

    // very low-frequency bank/clearing variation so coverage is not uniform across the sky
    float banks = Remap(ValueNoise(sp * 0.35), 0.3, 0.7, 0.55, 1.45);
    float coverage = clamp(CloudCoverage * banks, 0.0, 1.0);

    // base shape: Perlin DILATED into inverted-Worley billows (Nubis), plus a vertical profile
    float perlin = PerlinFbm(sp, cheap ? 3 : 5);
    float billow = cheap
        ? WorleyInv(sp * 1.7)
        : 0.625 * WorleyInv(sp * 1.7) + 0.25 * WorleyInv(sp * 3.4) + 0.125 * WorleyInv(sp * 6.8);
    float pw = billow + perlin * (1.0 - billow);
    float shape = clamp(Remap(pw, 0.5, 0.92, 0.0, 1.0), 0.0, 1.0);
    float profile = smoothstep(0.0, 0.06, h01) * (1.0 - smoothstep(0.4, 1.0, h01));
    float dens = clamp(Remap(shape * profile, 1.0 - coverage, 1.0, 0.0, 1.0), 0.0, 1.0);
    if (dens <= 0.0) return 0.0;

    if (!cheap) {
        // high-frequency Worley erosion; wisps near the base, cauliflower near the top
        float detail = 0.65 * WorleyInv(sp * 7.3) + 0.35 * WorleyInv(sp * 15.1);
        detail = lerp(1.0 - detail, detail, clamp(h01 * 3.5, 0.0, 1.0));
        dens = clamp(Remap(dens, CloudDetail * 0.45 * detail * (1.0 - dens), 1.0, 0.0, 1.0), 0.0, 1.0);
        if (dens <= 0.0) return 0.0;
    }

    // smoothstep curve: zero slope at both ends feathers the silhouette over several samples
    dens = dens * dens * (3.0 - 2.0 * dens);
    return dens * CLOUD_EXTINCTION * CloudDensity;
}

// Optical depth toward the sun through the cloud, sampled over widening shells.
float CloudLightDepth(float3 p) {
    float depth = 0.0;
    float prev = 0.0;
    [unroll] for (int j = 0; j < 6; j++) {
        float t = CloudThickness * CLOUD_STOPS[j];
        float3 q = p + SunDirection * ((t + prev) * 0.5);
        depth += CloudSigma(q, CloudHeight01(q), true) * (t - prev);
        prev = t;
    }
    return depth;
}

float HG(float mu, float g) {
    float g2 = g * g;
    return (1.0 - g2) / (4.0 * PI * pow(1.0 + g2 - 2.0 * g * mu, 1.5));
}

// Marches the cloud shell: rgb = in-scattered radiance, a = transmittance. midT = scatter-weighted mean
// depth so the air integral can be split in front of / behind the layer.
float4 MarchClouds(float3 o, float3 d, float mu, out float midT) {
    midT = 0.0;
    if (CloudsEnabled < 0.5 || CloudCoverage <= 0.001 || CloudDensity <= 0.0)
        return float4(0.0, 0.0, 0.0, 1.0);

    // low rays would need a huge march through the shell; the distance melt below dissolves them
    float horizon = smoothstep(0.0, 0.035, d.y);
    if (horizon <= 0.0) return float4(0.0, 0.0, 0.0, 1.0);

    float t0 = ExitSphere(o, d, Rp + CloudAltitude);
    float t1 = min(ExitSphere(o, d, Rp + CloudAltitude + CloudThickness), t0 + CloudThickness * 20.0);
    midT = t0;

    // distant clouds melt into the haze band instead of cutting off
    float fade = horizon * exp(-max(t0 - 12000.0, 0.0) * 5.0e-5);
    if (fade <= 0.002) return float4(0.0, 0.0, 0.0, 1.0);

    // sun color at the layer (one atmosphere evaluation: golden/red clouds at dusk for free)
    float3 sunTint = exp(-Extinction(SunDepths(o + d * t0))) * SunRadiance;

    // skylight inside the cloud: Rayleigh-hued but heavily whitened, dims through twilight.
    // The coefficient is the share of the sun's radiance that returns as ambient skylight fill — physically a
    // few percent, not 0.22. At 0.22 (and CloudAmbient 1) every cloud glowed with ~15% of full-sun white
    // radiance, so a half-covered sky read as a flat milky veil under AgX (the desaturated highlight band).
    // 0.05 keeps the shadowed undersides lifted without bleaching the whole hemisphere.
    float3 ambientHue = BetaR * AirDensity + (float3)(BetaM * Haze * 0.5);
    ambientHue /= max(max(ambientHue.r, max(ambientHue.g, ambientHue.b)), 1e-9);
    ambientHue = lerp(ambientHue, (float3)1.0, 0.5);
    float3 ambient = SunRadiance * ambientHue * 0.05 * CloudAmbient
                   * smoothstep(-0.05, 0.35, SunDirection.y);

    // triple-lobe phase: forward + soft backscatter + a tight silver-lining spike + a wide multi-scatter lobe
    float phasePrime = 0.55 * HG(mu, 0.65) + 0.3 * HG(mu, -0.15) + 0.15 * HG(mu, 0.92);
    float phaseWide = HG(mu, 0.16);

    // grazing rays cross a longer slab: more samples instead of bigger steps
    int steps = (int)lerp(48.0, 96.0, clamp((t1 - t0) / (CloudThickness * 20.0), 0.0, 1.0));
    float seg = (t1 - t0) / float(steps);
    float jitter = Hash3(d * 1024.0); // per-direction jitter kills terraced contour bands
    float transmittance = 1.0;
    float3 scattered = 0.0;
    float weightSum = 0.0, depthSum = 0.0;
    [loop] for (int i = 0; i < steps; i++) {
        float t = t0 + (float(i) + jitter) * seg;
        float3 p = o + d * t;
        float h01 = clamp(CloudHeight01(p), 0.0, 1.0);
        float sigma = CloudSigma(p, h01, false);
        if (sigma <= 1e-7) continue;

        float lightDepth = CloudLightDepth(p);
        // Beer-Lambert x powder on the primary lobe, plus a wide multi-scatter lobe (keeps thick interiors lit)
        float energy = exp(-lightDepth) * lerp(1.0, 1.0 - exp(-2.0 * lightDepth), 0.55) * phasePrime
                     + exp(-lightDepth * 0.2) * 0.85 * phaseWide;
        // skylight also occluded by the cloud above: thin spots glow, thick cores darken
        float3 inscatter = sunTint * energy
                         + ambient * lerp(0.35, 1.0, h01) * lerp(0.25, 1.0, exp(-lightDepth * 0.25));

        // energy-conserving per-step integration (Frostbite-style)
        float stepT = exp(-sigma * seg);
        float weight = transmittance * (1.0 - stepT);
        scattered += inscatter * CLOUD_ALBEDO * weight;
        depthSum += t * weight;
        weightSum += weight;
        transmittance *= stepT;
        if (transmittance < 0.004) break;
    }

    if (weightSum <= 0.0) return float4(0.0, 0.0, 0.0, 1.0);
    midT = depthSum / weightSum;

    scattered = min(scattered, (float3)60000.0) * fade; // fp16 safety, then horizon melt
    return float4(scattered, lerp(1.0, transmittance, fade));
}

// ===== Cirrus ================================================================
// Thin ice sheet near 7.5 km: wind-aligned, domain-warped streaks evaluated once where the ray pierces it.
static const float CIRRUS_ALTITUDE = 7500.0;

float4 CirrusLayer(float3 o, float3 d, float mu) {
    if (CirrusCoverage <= 0.001) return float4(0.0, 0.0, 0.0, 1.0);
    float fade = smoothstep(0.015, 0.09, d.y);
    if (fade <= 0.0) return float4(0.0, 0.0, 0.0, 1.0);

    float t = ExitSphere(o, d, Rp + CIRRUS_ALTITUDE);
    float3 p = o + d * t;

    // streaks stretch along the wind (jet stream, ~2.5x faster); a low-frequency warp hooks them into wisps
    float2 windDir = float2(sin(CloudWindAngle), cos(CloudWindAngle));
    float2 q = p.xz + CloudWindOffset.xz * 2.5;
    float2 uv = float2(dot(q, windDir) * 0.22, dot(q, float2(-windDir.y, windDir.x)))
              * (CLOUD_BASE_FREQ * 0.5 / CloudScale);
    float warp = Fbm(float3(uv * 3.1, 17.0), 3);
    uv.y += (warp - 0.5) * 0.7;
    float streak = Fbm(float3(uv, 4.2), 4);

    float dens = clamp(Remap(streak, lerp(0.70, 0.30, CirrusCoverage), 0.92, 0.0, 1.0), 0.0, 1.0);
    dens = pow(dens, 1.3); // soften: most of the sheet stays translucent
    float trans = exp(-dens * 2.0);

    // ice scatters strongly forward (ring around the sun) + a near-isotropic body keeping streaks white
    float3 sunTint = exp(-Extinction(SunDepths(p))) * SunRadiance;
    float phase = 0.5 * HG(mu, 0.78) + 0.35 * HG(mu, 0.15) + 0.04;
    float3 col = sunTint * phase * (1.0 - trans) * fade;
    return float4(min(col, (float3)60000.0), lerp(1.0, trans, fade));
}

// ===== Night sky =============================================================
// Hash-cell starfield + a faint airglow floor, fading in once the sun is a few degrees below the horizon.
float3 NightSky(float3 d) {
    float night = smoothstep(0.04, 0.16, -SunDirection.y);
    if (night <= 0.0) return (float3)0.0;

    float3 glow = float3(0.18, 0.35, 0.7) * 2.0; // airglow: night sky is never void-black

    float3 p = d * 80.0; // ~0.7 degree cells over the direction sphere
    float3 cell = floor(p);
    float sel = Hash3(cell + 0.5);
    if (sel > 0.18 || StarIntensity <= 0.0) return glow * night; // most cells stay empty

    float3 center = cell + 0.5 + (Hash33(cell) - 0.5) * 0.55; // disc kept inside its cell
    float d2 = dot(p - center, p - center);
    float bright = exp2(-7.0 * frac(sel * 41.7)); // few bright stars, many faint ones
    float star = exp(-d2 * 18.0) * bright;
    float3 tint = lerp(float3(1.0, 0.82, 0.6), float3(0.72, 0.82, 1.0), frac(sel * 173.3)); // warm K to blue A
    return (glow + tint * (star * StarIntensity * 30.0)) * night;
}

// Atmosphere radiance toward `dir`: scatter + clouds + cirrus + ground + sun disk + stars. Mirrors GL main().
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

    // Clouds march first so the air integral below can be split around their mean depth.
    float cloudMidT = 0.0;
    float4 clouds = ground ? float4(0.0, 0.0, 0.0, 1.0) : MarchClouds(origin, dir, mu, cloudMidT);
    float4 cirrus = ground ? float4(0.0, 0.0, 0.0, 1.0) : CirrusLayer(origin, dir, mu);
    bool hasClouds = clouds.a < 0.9995;

    float3 viewDepths = 0, sumR = 0, sumM = 0;
    float3 frontDepths = 0, frontR = 0, frontM = 0;
    float seg = tMax / float(VIEW_STEPS);
    [loop] for (int i = 0; i < VIEW_STEPS; i++) {
        float3 p = origin + dir * ((float(i) + 0.5) * seg);
        float3 d = Densities(p) * seg;
        viewDepths += d;
        if (HitGround(p, SunDirection) < 0.0) {
            float3 atten = exp(-Extinction(viewDepths + SunDepths(p)));
            sumR += atten * d.x;
            sumM += atten * d.y;
        }
        // Snapshot the in-scatter + air depth accumulated in front of the cloud layer.
        if (hasClouds && (float(i) + 0.5) * seg < cloudMidT) {
            frontDepths = viewDepths; frontR = sumR; frontM = sumM;
        }
    }

    // Single scattering: directional, phase-weighted. Multiple scattering is added SEPARATELY below — folding
    // it into a flat gain on this term (the old `* max(MultiScatter,1)`) scaled the achromatic Mie haze along
    // with the molecular blue and pushed the whole hemisphere into the tonemapper's desaturated highlight band,
    // washing the sky milky-white. Real multiple scattering is near-isotropic and dominated by the molecular
    // (Rayleigh) layer — so it lifts the blue sky brightness without bleaching it.
    float3 singleR = sumR * BetaR * AirDensity * phaseR;
    float3 singleM = sumM * BetaM * Haze * phaseM;
    float3 sky = (singleR + singleM) * SunRadiance;

    // Multiple scattering (Hillaire-style cheap approximation): an isotropic, Rayleigh-weighted glow whose
    // strength is the (MultiScatter-1) excess. Uses the isotropic phase 1/4pi (multiply-scattered light has
    // lost its directionality) and only a small share of the Mie integral, so haze brightens the sky without
    // greying it. MultiScatter 1 = single-scatter only (this whole term is 0).
    float msGain = max(MultiScatter - 1.0, 0.0);
    float3 msR = sumR * BetaR * AirDensity;
    float3 msM = sumM * BetaM * Haze * 0.25;          // haze contributes far less diffuse glow than air
    sky += (msR + msM) * (1.0 / (4.0 * PI)) * msGain * SunRadiance;
    float3 viewTrans = exp(-Extinction(viewDepths));

    if (ground) {
        float3 p = origin + dir * tGround;
        float3 up = normalize(p);
        float ndl = max(dot(up, SunDirection), 0.0);
        float3 sunAtGround = exp(-Extinction(SunDepths(p)));
        sky += GroundAlbedo / PI * SunRadiance * sunAtGround * ndl * viewTrans;
    } else {
        // Cirrus sits above ~half the Rayleigh column, so only that share of the background dims through it.
        float3 skyBack = max(sky * lerp(1.0, cirrus.a, 0.45) + cirrus.rgb, (float3)0.0);

        if (hasClouds) {
            // Split the air at the cumulus mean depth: front scatter untouched, the cloud dimmed by that
            // air (aerial perspective), everything behind shown through the cloud transmittance.
            float3 skyFront = (frontR * BetaR * AirDensity * phaseR + frontM * BetaM * Haze * phaseM)
                            * SunRadiance * max(MultiScatter, 1.0);
            sky = skyFront + exp(-Extinction(frontDepths)) * clouds.rgb
                + clouds.a * max(skyBack - skyFront, (float3)0.0);
        } else {
            sky = skyBack;
        }

        if (mu > cos(SunAngularRadius)) {
            // Visible sun disk: physical radiance reddened by air, limb-darkened, occluded by both layers.
            float solidAngle = 2.0 * PI * (1.0 - cos(SunAngularRadius));
            float r = clamp(acos(clamp(mu, -1.0, 1.0)) / SunAngularRadius, 0.0, 1.0);
            float limb = 1.0 - 0.6 * (1.0 - sqrt(max(1.0 - r * r, 0.0)));
            float3 disk = SunRadiance / max(solidAngle, 1e-6) * (SunDiskIntensity * limb);
            sky += min(disk * viewTrans, (float3)60000.0) * clouds.a * cirrus.a;
        }

        // Starfield + airglow: occluded by both cloud layers, extinguished by the air like any star light.
        sky += NightSky(dir) * viewTrans * clouds.a * cirrus.a;
    }
    return max(sky * Exposure, (float3)0.0);
}

VSOutput VSMain(uint vid : SV_VertexID) {
    VSOutput o;
    float4 pos = mul(float4(CubeVerts[vid], 1.0), ViewProjNoTranslate);
    o.Position = pos.xyww;          // depth 1.0 -> far plane (LEqual fills uncovered pixels)
    o.Dir = CubeVerts[vid];
    return o;
}

float4 PSMain(VSOutput i) : SV_Target {
    // RAW HDR sky radiance into the R16F scene target - the composite does exposure + ACES + sRGB.
    return float4(SkyRadiance(normalize(i.Dir)), 1.0);
}

// ---- BACKGROUND via the baked env cube (the FAST path the renderer uses) ----
// SkyRadiance() is a full atmosphere + cloud raymarch (thousands of ALU/pixel) - far too costly to run for
// every screen pixel the sky covers. The IBL baker already renders that exact kernel into a 256^2 env cube
// each time the sky params change; here we just SAMPLE it by view direction, collapsing the per-pixel cost
// to a single cube fetch (the GL path bakes a cube and samples it the same way). DrawProcSky falls back to
// PSMain only when no env cube has been baked yet.
TextureCube SkyEnv : register(t0);
SamplerState SkyEnvSampler : register(s0);

float4 PSBackground(VSOutput i) : SV_Target {
    return float4(SkyEnv.SampleLevel(SkyEnvSampler, normalize(i.Dir), 0.0).rgb, 1.0);
}

// ---- Env-cube BAKE: render RAW HDR sky radiance into one cube face (FSQ) for IBL convolution. ----
// Same SkyRadiance kernel (clouds/cirrus/stars included), so the IBL + reflections see the full sky.
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
