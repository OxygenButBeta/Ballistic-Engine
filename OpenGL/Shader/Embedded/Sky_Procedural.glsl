#version 330 core

in vec2 TexCoords;
out vec4 FragColor;

// Physically-based procedural sky: single-scattering Rayleigh + Mie + ozone atmosphere
// plus a raymarched volumetric cumulus layer, a thin cirrus sheet and a night starfield,
// rendered one cube face per pass (FaceDir reconstructs the direction, same convention as
// the IBL bakes). Output radiance is in the engine's physical light scale - SunRadiance
// already carries the sun's lux -> radiance conversion - so the result drops into the same
// pipeline slots as a measured HDRI (sky draw, IBL bake, EV exposure).

uniform int Face;
uniform vec3 SunDirection;      // toward the sun, normalized
uniform vec3 SunRadiance;       // sun color * illuminance, engine radiance units
uniform float SunAngularRadius; // radians
uniform float SunDiskIntensity; // visible-disk-only artistic scale
uniform float AirDensity;       // Rayleigh multiplier (1 = Earth)
uniform float Haze;             // Mie multiplier (1 = clear day)
uniform float HazeAnisotropy;   // Mie phase g
uniform float OzoneDensity;     // ozone absorption multiplier
uniform vec3 GroundAlbedo;      // virtual planet surface below the horizon
uniform float MultiScatter;     // multiple-scattering energy approximation (1 = single only)
uniform float Exposure;         // sky luminance multiplier, baked into the texels

uniform int   CloudsEnabled;    // volumetric cumulus layer toggle
uniform float CloudCoverage;    // 0 = clear, 1 = overcast
uniform float CloudDensity;     // extinction multiplier
uniform float CloudAltitude;    // layer base above sea level (m)
uniform float CloudThickness;   // layer vertical extent (m)
uniform float CloudScale;       // horizontal feature size multiplier
uniform float CloudDetail;      // edge erosion strength
uniform float CloudAmbient;     // skylight-inside-clouds scale
uniform vec3  CloudWindOffset;  // wind direction * speed * time, meters
uniform float CloudWindAngle;   // wind compass direction, radians (cirrus streak alignment)
uniform float CirrusCoverage;   // thin high ice sheet: 0 = clear
uniform float StarIntensity;    // night starfield radiance scale (0 = off)

const float PI = 3.14159265359;
const float Rp = 6360e3;        // planet radius (m)
const float Ra = 6460e3;        // atmosphere top (m)
const vec3  BetaR = vec3(5.802e-6, 13.558e-6, 33.1e-6); // Rayleigh scattering at sea level
const float BetaM = 3.996e-6;                           // Mie scattering at sea level
const vec3  BetaO = vec3(0.650e-6, 1.881e-6, 0.085e-6); // ozone absorption
const float Hr = 8500.0;        // Rayleigh scale height (m)
const float Hm = 1200.0;        // Mie scale height (m)
const int   VIEW_STEPS = 32;
const int   LIGHT_STEPS = 8;

vec3 FaceDir(int face, vec2 uv) {
    vec2 st = uv * 2.0 - 1.0;
    if (face == 0) return vec3( 1.0, -st.y, -st.x);
    if (face == 1) return vec3(-1.0, -st.y,  st.x);
    if (face == 2) return vec3( st.x,  1.0,  st.y);
    if (face == 3) return vec3( st.x, -1.0, -st.y);
    if (face == 4) return vec3( st.x, -st.y,  1.0);
    return vec3(-st.x, -st.y, -1.0);
}

// Distance to where the ray exits a planet-centered sphere of radius R (from inside it,
// or up through it when starting below).
float ExitSphere(vec3 o, vec3 d, float R) {
    float b = dot(o, d);
    float c = dot(o, o) - R * R;
    return -b + sqrt(max(b * b - c, 0.0));
}

// Distance to the atmosphere top from inside it (always hits).
float ExitAtmosphere(vec3 o, vec3 d) {
    return ExitSphere(o, d, Ra);
}

// Distance to the planet surface, or -1 when the ray misses it.
float HitGround(vec3 o, vec3 d) {
    float b = dot(o, d);
    float c = dot(o, o) - Rp * Rp;
    float h = b * b - c;
    if (h < 0.0)
        return -1.0;
    float t = -b - sqrt(h);
    return t > 0.0 ? t : -1.0;
}

// (rayleigh, mie, ozone) density at a point; ozone peaks in a 25km-high tent layer.
vec3 Densities(vec3 p) {
    float h = max(length(p) - Rp, 0.0);
    float ozone = max(0.0, 1.0 - abs(h - 25000.0) / 15000.0);
    return vec3(exp(-h / Hr), exp(-h / Hm), ozone);
}

// Total extinction for integrated (rayleigh, mie, ozone) path densities.
vec3 Extinction(vec3 depths) {
    return BetaR * AirDensity * depths.x
         + BetaM * 1.11 * Haze * depths.y   // Mie extinction = scattering / 0.9
         + BetaO * OzoneDensity * depths.z;
}

// Integrated densities from p toward the sun, out of the atmosphere.
vec3 SunDepths(vec3 p) {
    float seg = ExitAtmosphere(p, SunDirection) / float(LIGHT_STEPS);
    vec3 depths = vec3(0.0);
    for (int j = 0; j < LIGHT_STEPS; j++)
        depths += Densities(p + SunDirection * ((float(j) + 0.5) * seg)) * seg;
    return depths;
}

// ===== Noise ================================================================
// The cumulus base shape uses Perlin-dilated-by-Worley (the Nubis / "Horizon Zero Dawn"
// construction): inverted Worley supplies the cauliflower clumping real cumulus have,
// the Perlin breaks its cellular regularity. Plain value-noise fbm (the old base) reads
// as smoke blobs, never as billows. All ALU - this shader only runs at bake time.

float Hash3(vec3 p) {
    p = fract(p * 0.1031 + vec3(0.17, 0.39, 0.61));
    p += dot(p, p.yzx + 19.19);
    return fract((p.x + p.y) * p.z);
}

vec3 Hash33(vec3 p) {
    p = fract(p * vec3(0.1031, 0.1030, 0.0973));
    p += dot(p, p.yxz + 33.33);
    return fract((p.xxy + p.yxx) * p.zyx);
}

float ValueNoise(vec3 p) {
    vec3 i = floor(p);
    vec3 f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    float n000 = Hash3(i);
    float n100 = Hash3(i + vec3(1.0, 0.0, 0.0));
    float n010 = Hash3(i + vec3(0.0, 1.0, 0.0));
    float n110 = Hash3(i + vec3(1.0, 1.0, 0.0));
    float n001 = Hash3(i + vec3(0.0, 0.0, 1.0));
    float n101 = Hash3(i + vec3(1.0, 0.0, 1.0));
    float n011 = Hash3(i + vec3(0.0, 1.0, 1.0));
    float n111 = Hash3(i + vec3(1.0, 1.0, 1.0));
    return mix(mix(mix(n000, n100, f.x), mix(n010, n110, f.x), f.y),
               mix(mix(n001, n101, f.x), mix(n011, n111, f.x), f.y), f.z);
}

float Fbm(vec3 p, int octaves) {
    float amp = 0.55;
    float sum = 0.0;
    float norm = 0.0;
    for (int i = 0; i < octaves; i++) {
        sum += amp * ValueNoise(p);
        norm += amp;
        amp *= 0.5;
        p = p * 2.17 + vec3(31.7, 11.3, 7.9);
    }
    return sum / norm;
}

// Perlin gradient noise, ~[-1, 1].
float GradNoise(vec3 p) {
    vec3 i = floor(p);
    vec3 f = fract(p);
    vec3 u = f * f * (3.0 - 2.0 * f);
    float n000 = dot(Hash33(i) * 2.0 - 1.0, f);
    float n100 = dot(Hash33(i + vec3(1, 0, 0)) * 2.0 - 1.0, f - vec3(1, 0, 0));
    float n010 = dot(Hash33(i + vec3(0, 1, 0)) * 2.0 - 1.0, f - vec3(0, 1, 0));
    float n110 = dot(Hash33(i + vec3(1, 1, 0)) * 2.0 - 1.0, f - vec3(1, 1, 0));
    float n001 = dot(Hash33(i + vec3(0, 0, 1)) * 2.0 - 1.0, f - vec3(0, 0, 1));
    float n101 = dot(Hash33(i + vec3(1, 0, 1)) * 2.0 - 1.0, f - vec3(1, 0, 1));
    float n011 = dot(Hash33(i + vec3(0, 1, 1)) * 2.0 - 1.0, f - vec3(0, 1, 1));
    float n111 = dot(Hash33(i + vec3(1, 1, 1)) * 2.0 - 1.0, f - vec3(1, 1, 1));
    return mix(mix(mix(n000, n100, u.x), mix(n010, n110, u.x), u.y),
               mix(mix(n001, n101, u.x), mix(n011, n111, u.x), u.y), u.z);
}

float PerlinFbm(vec3 p, int octaves) {
    float amp = 0.55;
    float sum = 0.0;
    float norm = 0.0;
    for (int i = 0; i < octaves; i++) {
        sum += amp * GradNoise(p);
        norm += amp;
        amp *= 0.5;
        p = p * 2.13 + vec3(19.1, 33.4, 47.2);
    }
    return sum / norm * 0.5 + 0.5;
}

// Inverted Worley: 1 at cell feature points falling to 0 between them - each cell reads
// as one convex billow.
float WorleyInv(vec3 p) {
    vec3 i = floor(p);
    vec3 f = fract(p);
    float dmin = 1e9;
    for (int x = -1; x <= 1; x++)
    for (int y = -1; y <= 1; y++)
    for (int z = -1; z <= 1; z++) {
        vec3 c = vec3(float(x), float(y), float(z));
        vec3 q = Hash33(i + c) + c - f;
        dmin = min(dmin, dot(q, q));
    }
    return 1.0 - sqrt(min(dmin, 1.0));
}

float Remap(float v, float lo, float hi, float newLo, float newHi) {
    return newLo + (v - lo) / max(hi - lo, 1e-5) * (newHi - newLo);
}

// ===== Volumetric clouds =====================================================
// A single raymarched cumulus layer baked into the same cubemap, so reflections, the IBL
// and SSGI all see the clouds for free. The camera sits below the layer, so only upward
// rays march; grazing rays melt into the horizon haze instead of walking hundreds of km
// through the shell.

const float CLOUD_BASE_FREQ = 1.0 / 9000.0; // base shape features ~9 km at CloudScale 1
const float CLOUD_EXTINCTION = 0.0035;      // m^-1 inside a dense core at CloudDensity 1
const float CLOUD_ALBEDO = 0.97;            // single-scatter albedo of water droplets

float CloudHeight01(vec3 p) {
    return (length(p) - Rp - CloudAltitude) / max(CloudThickness, 1.0);
}

// Cloud extinction coefficient (m^-1) at p. cheap=true drops noise octaves and skips the
// edge-erosion detail pass (used by the light march, where overestimating density just
// deepens self-shadowing).
float CloudSigma(vec3 p, float h01, bool cheap) {
    if (h01 <= 0.0 || h01 >= 1.0)
        return 0.0;

    vec3 sp = (p + CloudWindOffset) * (CLOUD_BASE_FREQ / CloudScale);

    // very low-frequency bank/clearing variation so coverage is not uniform across the sky
    float banks = Remap(ValueNoise(sp * 0.35), 0.3, 0.7, 0.55, 1.45);
    float coverage = clamp(CloudCoverage * banks, 0.0, 1.0);

    // base shape: Perlin DILATED into the inverted-Worley billows (remap perlin from [0,1]
    // into [billow, 1], the Nubis construction) - Worley cell centers puff outward into
    // convex cauliflower lobes; then a vertical profile (flat base, billowy middle,
    // feathered top)
    float perlin = PerlinFbm(sp, cheap ? 3 : 5);
    float billow = cheap
        ? WorleyInv(sp * 1.7)
        : 0.625 * WorleyInv(sp * 1.7) + 0.25 * WorleyInv(sp * 3.4) + 0.125 * WorleyInv(sp * 6.8);
    float pw = billow + perlin * (1.0 - billow);
    float shape = clamp(Remap(pw, 0.5, 0.92, 0.0, 1.0), 0.0, 1.0);
    float profile = smoothstep(0.0, 0.06, h01) * (1.0 - smoothstep(0.4, 1.0, h01));
    float base = clamp(Remap(shape * profile, 1.0 - coverage, 1.0, 0.0, 1.0), 0.0, 1.0);
    if (base <= 0.0)
        return 0.0;

    if (!cheap) {
        // high-frequency Worley erosion; inverting the noise near the base reads as wisps,
        // keeping it upright near the top reads as cauliflower billows. (1 - base) focuses
        // the bite on silhouettes so dense cores keep their mass
        float detail = 0.65 * WorleyInv(sp * 7.3) + 0.35 * WorleyInv(sp * 15.1);
        detail = mix(1.0 - detail, detail, clamp(h01 * 3.5, 0.0, 1.0));
        base = clamp(Remap(base, CloudDetail * 0.45 * detail * (1.0 - base), 1.0, 0.0, 1.0), 0.0, 1.0);
        if (base <= 0.0)
            return 0.0;
    }

    // smoothstep curve: zero slope at both ends feathers the silhouette over several
    // samples instead of a hard density cliff (hard cliffs alias at the cubemap texel)
    base = base * base * (3.0 - 2.0 * base);

    return base * CLOUD_EXTINCTION * CloudDensity;
}

// Optical depth toward the sun through the cloud, sampled over widening shells (the last
// stop reaches a full thickness out so towering neighbours still shadow this sample).
float CloudLightDepth(vec3 p) {
    const float STOPS[6] = float[6](0.04, 0.1, 0.2, 0.35, 0.6, 1.0);
    float depth = 0.0;
    float prev = 0.0;
    for (int j = 0; j < 6; j++) {
        float t = CloudThickness * STOPS[j];
        vec3 q = p + SunDirection * ((t + prev) * 0.5);
        depth += CloudSigma(q, CloudHeight01(q), true) * (t - prev);
        prev = t;
    }
    return depth;
}

float HG(float mu, float g) {
    float g2 = g * g;
    return (1.0 - g2) / (4.0 * PI * pow(1.0 + g2 - 2.0 * g * mu, 1.5));
}

// Marches the cloud shell: rgb = in-scattered radiance, a = transmittance. midT returns the
// scatter-weighted mean depth so the air integral can be split in front of / behind the layer.
vec4 MarchClouds(vec3 o, vec3 d, float mu, out float midT) {
    midT = 0.0;
    if (CloudsEnabled == 0 || CloudCoverage <= 0.001 || CloudDensity <= 0.0)
        return vec4(0.0, 0.0, 0.0, 1.0);

    // low rays would need a huge march through the shell; this is only the hard floor -
    // the distance melt below dissolves them into the horizon haze well before it
    float horizon = smoothstep(0.0, 0.035, d.y);
    if (horizon <= 0.0)
        return vec4(0.0, 0.0, 0.0, 1.0);

    float t0 = ExitSphere(o, d, Rp + CloudAltitude);
    float t1 = min(ExitSphere(o, d, Rp + CloudAltitude + CloudThickness),
                   t0 + CloudThickness * 20.0);
    midT = t0;

    // distant clouds melt into the haze band instead of cutting off: real horizons stack
    // compressed, washed-out cloud layers, not a clear gap
    float fade = horizon * exp(-max(t0 - 12000.0, 0.0) * 5.0e-5);
    if (fade <= 0.002)
        return vec4(0.0, 0.0, 0.0, 1.0);

    // sun color at the layer (one atmosphere evaluation: golden/red clouds at dusk for free)
    vec3 sunTint = exp(-Extinction(SunDepths(o + d * t0))) * SunRadiance;

    // skylight inside the cloud: Rayleigh-hued but heavily whitened (multiple scattering
    // inside the droplets desaturates the blue), dims through twilight
    vec3 ambientHue = BetaR * AirDensity + vec3(BetaM * Haze * 0.5);
    ambientHue /= max(max(ambientHue.r, max(ambientHue.g, ambientHue.b)), 1e-9);
    ambientHue = mix(ambientHue, vec3(1.0), 0.5);
    vec3 ambient = SunRadiance * ambientHue * 0.22 * CloudAmbient
                 * smoothstep(-0.05, 0.35, SunDirection.y);

    // triple-lobe phase: forward + soft backscatter + a tight silver-lining spike near the
    // sun; a wide near-isotropic lobe stands in for the higher scattering orders in the
    // energy term below - it carries MOST of a thick cloud's brightness (multiple
    // scattering is why sunlit cumulus read brighter than the blue sky, not single bounce)
    float phasePrime = 0.55 * HG(mu, 0.65) + 0.3 * HG(mu, -0.15) + 0.15 * HG(mu, 0.92);
    float phaseWide = HG(mu, 0.16);

    // grazing rays cross a longer slab: give them more samples instead of bigger steps
    int steps = int(mix(48.0, 96.0, clamp((t1 - t0) / (CloudThickness * 20.0), 0.0, 1.0)));
    float seg = (t1 - t0) / float(steps);
    // per-direction jitter of the sample positions: without it the discrete march
    // distances print as terraced contour bands on every cloud silhouette
    float jitter = Hash3(d * 1024.0);
    float transmittance = 1.0;
    vec3 scattered = vec3(0.0);
    float weightSum = 0.0;
    float depthSum = 0.0;
    for (int i = 0; i < steps; i++) {
        float t = t0 + (float(i) + jitter) * seg;
        vec3 p = o + d * t;
        float h01 = clamp(CloudHeight01(p), 0.0, 1.0);
        float sigma = CloudSigma(p, h01, false);
        if (sigma <= 1e-7)
            continue;

        float lightDepth = CloudLightDepth(p);
        // Beer-Lambert x powder on the primary lobe, plus a wide multi-scatter lobe with a
        // slow falloff: it is what keeps thick interiors textured instead of flat ambient
        float energy = exp(-lightDepth) * mix(1.0, 1.0 - exp(-2.0 * lightDepth), 0.55) * phasePrime
                     + exp(-lightDepth * 0.2) * 0.85 * phaseWide;
        // skylight is also occluded by the cloud above the sample: this mottles the
        // undersides (thin spots glow, thick cores darken) instead of one flat gray
        vec3 inscatter = sunTint * energy
                       + ambient * mix(0.35, 1.0, h01) * mix(0.25, 1.0, exp(-lightDepth * 0.25));

        // energy-conserving per-step integration (Frostbite-style)
        float stepT = exp(-sigma * seg);
        float weight = transmittance * (1.0 - stepT);
        scattered += inscatter * CLOUD_ALBEDO * weight;
        depthSum += t * weight;
        weightSum += weight;
        transmittance *= stepT;
        if (transmittance < 0.004)
            break;
    }

    if (weightSum <= 0.0)
        return vec4(0.0, 0.0, 0.0, 1.0);
    midT = depthSum / weightSum;

    scattered = min(scattered, vec3(60000.0)) * fade; // fp16 safety, then horizon melt
    return vec4(scattered, mix(1.0, transmittance, fade));
}

// ===== Cirrus ================================================================
// Thin ice sheet near 7.5 km: wind-aligned, domain-warped streaks evaluated once where
// the ray pierces the layer (no march - the sheet is optically thin). Sits above the
// cumulus, tints gold/red at dusk through the same atmosphere evaluation.

const float CIRRUS_ALTITUDE = 7500.0;

vec4 CirrusLayer(vec3 o, vec3 d, float mu) {
    if (CirrusCoverage <= 0.001)
        return vec4(0.0, 0.0, 0.0, 1.0);
    float fade = smoothstep(0.015, 0.09, d.y);
    if (fade <= 0.0)
        return vec4(0.0, 0.0, 0.0, 1.0);

    float t = ExitSphere(o, d, Rp + CIRRUS_ALTITUDE);
    vec3 p = o + d * t;

    // streaks stretch along the wind (jet stream blows the same way the cumulus drift,
    // ~2.5x faster up here); a low-frequency warp bends them into the hooked wisps
    vec2 windDir = vec2(sin(CloudWindAngle), cos(CloudWindAngle));
    vec2 q = p.xz + CloudWindOffset.xz * 2.5;
    vec2 uv = vec2(dot(q, windDir) * 0.22, dot(q, vec2(-windDir.y, windDir.x)))
            * (CLOUD_BASE_FREQ * 0.5 / CloudScale);
    float warp = Fbm(vec3(uv * 3.1, 17.0), 3);
    uv.y += (warp - 0.5) * 0.7;
    float streak = Fbm(vec3(uv, 4.2), 4);

    // the fbm only spans ~[0.2, 0.8], so the coverage window lives inside that range
    float dens = clamp(Remap(streak, mix(0.70, 0.30, CirrusCoverage), 0.92, 0.0, 1.0), 0.0, 1.0);
    dens = pow(dens, 1.3); // soften: most of the sheet stays translucent
    float trans = exp(-dens * 2.0);

    // ice crystals scatter strongly forward (the bright ring around the sun) + a near-
    // isotropic multi-scatter body that keeps the streaks reading white against the blue
    // sky away from the sun; energy-conserving via (1 - transmittance)
    vec3 sunTint = exp(-Extinction(SunDepths(p))) * SunRadiance;
    float phase = 0.5 * HG(mu, 0.78) + 0.35 * HG(mu, 0.15) + 0.04;
    vec3 col = sunTint * phase * (1.0 - trans) * fade;
    return vec4(min(col, vec3(60000.0)), mix(1.0, trans, fade));
}

// ===== Night sky =============================================================
// Hash-cell starfield + a faint airglow floor, fading in once the sun is a few degrees
// below the horizon. The engine is in true photometric units (sun ~80000), so the night
// radiances are lifted to the bottom of the auto-exposure range (~EV 5-8): physically a
// starfield sits orders of magnitude below what any camera EV the engine meters can see.
// Night scenes want an automatic-Exposure volume (or a low fixed EV) - exactly like a real
// camera. Stars are baked texels: Resolution >= 512 keeps them round.

vec3 NightSky(vec3 d) {
    float night = smoothstep(0.04, 0.16, -SunDirection.y);
    if (night <= 0.0)
        return vec3(0.0);

    // airglow: the sky is never void-black; a deep blue floor keeps night readable
    vec3 glow = vec3(0.18, 0.35, 0.7) * 2.0;

    vec3 p = d * 80.0; // ~0.7 degree cells over the direction sphere
    vec3 cell = floor(p);
    float sel = Hash3(cell + 0.5);
    if (sel > 0.18 || StarIntensity <= 0.0)
        return glow * night; // most cells stay empty

    // the disc is kept inside its cell so a single-cell lookup never clips a neighbour
    vec3 center = cell + 0.5 + (Hash33(cell) - 0.5) * 0.55;
    float d2 = dot(p - center, p - center);
    float bright = exp2(-7.0 * fract(sel * 41.7)); // few bright stars, many faint ones
    float star = exp(-d2 * 18.0) * bright;
    // blackbody-ish tint spread: warm K-class to blue-white A-class
    vec3 tint = mix(vec3(1.0, 0.82, 0.6), vec3(0.72, 0.82, 1.0), fract(sel * 173.3));
    return (glow + tint * (star * StarIntensity * 30.0)) * night;
}

void main() {
    vec3 dir = normalize(FaceDir(Face, TexCoords));
    vec3 origin = vec3(0.0, Rp + 500.0, 0.0); // camera 500m up: keeps the horizon line crisp

    float tGround = HitGround(origin, dir);
    bool ground = tGround > 0.0;
    float tMax = ground ? tGround : ExitAtmosphere(origin, dir);

    float mu = dot(dir, SunDirection);
    float phaseR = 3.0 / (16.0 * PI) * (1.0 + mu * mu);
    float g = clamp(HazeAnisotropy, -0.99, 0.99);
    float g2 = g * g;
    // Cornette-Shanks Mie phase.
    float phaseM = 3.0 / (8.0 * PI) * ((1.0 - g2) * (1.0 + mu * mu)) /
                   ((2.0 + g2) * pow(1.0 + g2 - 2.0 * g * mu, 1.5));

    // Clouds march first so the air integral below can be split around their mean depth.
    // Ground rays never reach the layers (the camera sits below the cloud base).
    float cloudMidT = 0.0;
    vec4 clouds = ground ? vec4(0.0, 0.0, 0.0, 1.0) : MarchClouds(origin, dir, mu, cloudMidT);
    vec4 cirrus = ground ? vec4(0.0, 0.0, 0.0, 1.0) : CirrusLayer(origin, dir, mu);
    bool hasClouds = clouds.a < 0.9995;

    vec3 viewDepths = vec3(0.0);
    vec3 sumR = vec3(0.0);
    vec3 sumM = vec3(0.0);
    vec3 frontDepths = vec3(0.0);
    vec3 frontR = vec3(0.0);
    vec3 frontM = vec3(0.0);
    float seg = tMax / float(VIEW_STEPS);
    for (int i = 0; i < VIEW_STEPS; i++) {
        vec3 p = origin + dir * ((float(i) + 0.5) * seg);
        vec3 d = Densities(p) * seg;
        viewDepths += d;

        // The planet shadows this air sample from the sun: no direct in-scatter here.
        if (HitGround(p, SunDirection) < 0.0) {
            vec3 attenuation = exp(-Extinction(viewDepths + SunDepths(p)));
            sumR += attenuation * d.x;
            sumM += attenuation * d.y;
        }

        // Snapshot the in-scatter and air depth accumulated in front of the cloud layer.
        if (hasClouds && (float(i) + 0.5) * seg < cloudMidT) {
            frontDepths = viewDepths;
            frontR = sumR;
            frontM = sumM;
        }
    }

    vec3 sky = (sumR * BetaR * AirDensity * phaseR + sumM * BetaM * Haze * phaseM)
             * SunRadiance * max(MultiScatter, 1.0);

    vec3 viewTransmittance = exp(-Extinction(viewDepths));

    if (ground) {
        // Lambertian virtual ground lit by the attenuated sun, seen through the air. Gives
        // the IBL's lower hemisphere a plausible ground bounce.
        vec3 p = origin + dir * tGround;
        vec3 up = normalize(p);
        float ndl = max(dot(up, SunDirection), 0.0);
        vec3 sunAtGround = exp(-Extinction(SunDepths(p)));
        sky += GroundAlbedo / PI * SunRadiance * sunAtGround * ndl * viewTransmittance;
    }
    else {
        // Cirrus sits above everything: roughly half the Rayleigh scatter accumulates
        // above 7.5km, so only that share of the background dims through the sheet.
        vec3 skyBack = max(sky * mix(1.0, cirrus.a, 0.45) + cirrus.rgb, vec3(0.0));

        if (hasClouds) {
            // Split the air at the cumulus' mean depth: in-front scatter stays untouched,
            // the cloud slots in dimmed by that air (aerial perspective), and everything
            // behind it shows through the cloud's transmittance.
            vec3 skyFront = (frontR * BetaR * AirDensity * phaseR + frontM * BetaM * Haze * phaseM)
                          * SunRadiance * max(MultiScatter, 1.0);
            sky = skyFront + exp(-Extinction(frontDepths)) * clouds.rgb
                + clouds.a * max(skyBack - skyFront, vec3(0.0));
        }
        else {
            sky = skyBack;
        }

        if (mu > cos(SunAngularRadius)) {
            // Visible sun disk: physical radiance = illuminance / disk solid angle, reddened
            // by the air path, with limb darkening, occluded by both cloud layers. Clamped
            // inside fp16 (the analytic sun carries the actual direct lighting; these texels
            // drive bloom and reflections).
            float solidAngle = 2.0 * PI * (1.0 - cos(SunAngularRadius));
            float r = clamp(acos(clamp(mu, -1.0, 1.0)) / SunAngularRadius, 0.0, 1.0);
            float limb = 1.0 - 0.6 * (1.0 - sqrt(max(1.0 - r * r, 0.0)));
            vec3 disk = SunRadiance / max(solidAngle, 1e-6) * (SunDiskIntensity * limb);
            sky += min(disk * viewTransmittance, vec3(60000.0)) * clouds.a * cirrus.a;
        }

        // Starfield + airglow: occluded by both cloud layers, extinguished by the air like
        // any celestial light (stars dim and redden toward the horizon for free).
        sky += NightSky(dir) * viewTransmittance * clouds.a * cirrus.a;
    }

    FragColor = vec4(max(sky * Exposure, vec3(0.0)), 1.0);
}
