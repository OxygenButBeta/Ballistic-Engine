// Aerial-perspective froxel volume (Hillaire 2020 "A Scalable and Production Ready Sky and Atmosphere").
// A small camera-anchored 3D LUT: for each froxel (screen x,y; depth slice z = view distance) it stores the
// accumulated SINGLE-SCATTER inscatter (rgb) and the mean TRANSMITTANCE (a) of a short Rayleigh+Mie march
// from the camera out to that froxel's distance, lit by the sun (atmosphere-attenuated) plus an ambient sky
// term. The AP pass then samples this volume by (screenUV, linearDistance) and applies `scene*T + inscatter`,
// so distant geometry fades into EXACTLY the colour of the sky behind it. This is the physical replacement
// for the old analytic veil (a hardcoded lux-scaled blue tint over a fake linear-distance term).
//
// Constants MUST match ProceduralSky.hlsl / SkyTransmittance.hlsl (BetaR/BetaM/BetaO, Hr/Hm, Rp/Ra) so the
// haze colour is identical to the sky kernel. The volume is camera-relative (froxel depth = metres from the
// camera along the view ray), so it is re-baked every frame from the current view — cheap (32x32x32 = 32k
// threads, one short march each).

cbuffer ApLutConstants : register(b0) {
    float4x4 InvViewProj;     // unproject froxel (ndc.xy, depthForSlice) -> world ray (transposed on upload)
    float3   CameraPos;       float MaxDistance;     // froxel-volume far depth (m)
    float3   SunDirection;    float StartDistance;   // toward the sun (normalized); m before haze builds
    float3   SunRadiance;     float DensityScale;    // sun colour*illuminance (engine units); apparent density mult
    float3   SkyTint;         float Anisotropy;      // ambient sky in-scatter colour (engine-radiance scale); Mie HG g
    float    AirDensity;      float Haze; float OzoneDensity; float Intensity;  // sky atmosphere mults; master strength
    float3   Tint;            float _padL0;          // artistic colour grade on the in-scatter (white = physical)
    uint3    VolumeSize;      float _padL;           // froxel resolution (W,H,D)
};

static const float PI = 3.14159265359;
static const float Rp = 6360e3;        // planet radius (m)
static const float Ra = 6460e3;        // atmosphere top (m)
static const float3 BetaR = float3(5.802e-6, 13.558e-6, 33.1e-6);
static const float  BetaM = 3.996e-6;
static const float3 BetaO = float3(0.650e-6, 1.881e-6, 0.085e-6);
static const float Hr = 8500.0;
static const float Hm = 1200.0;
// Camera-relative -> planet-relative: the scene sits ~500 m above the planet floor (same anchor the sky uses
// for its virtual ground), so air density at a world point uses height above that floor.
static const float SceneFloorAltitude = 500.0;
static const int   MARCH_STEPS = 12;   // froxel-internal march samples per slice accumulation

RWTexture3D<float4> ApVolume : register(u0);

float3 Densities(float worldY) {
    // Height above the scene floor; the planet-scale exponentials flatten to ~constant over scene metres, but
    // keeping them means tall content (towers) correctly thins. ozone is ~flat down here so we drop it for the
    // in-scene march (it lives at 25 km) — included via Extinction for parity but its density is ~0 near ground.
    float h = max(worldY + SceneFloorAltitude, 0.0);
    float ozone = max(0.0, 1.0 - abs(h - 25000.0) / 15000.0);
    return float3(exp(-h / Hr), exp(-h / Hm), ozone);
}
float3 Extinction(float3 depths) {
    return BetaR * AirDensity * depths.x + BetaM * 1.11 * Haze * depths.y + BetaO * OzoneDensity * depths.z;
}

float HG(float mu, float g) {
    float g2 = g * g;
    return (1.0 - g2) / (4.0 * PI * pow(max(1.0 + g2 - 2.0 * g * mu, 1e-4), 1.5));
}

// Sun transmittance toward the top of the atmosphere from a scene point (cheap analytic, same integral the
// sky's SunDepths marches — a few steps is plenty over scene-scale; this is the golden-hour tint).
float3 SunTransmittance(float worldY) {
    // Approximate the slant column to the atmosphere top from this altitude along the sun direction. Over scene
    // scale the start height barely changes the long planetary column, so a single representative depth set is
    // fine and keeps the bake cheap. Reuse the densities at the point scaled by a nominal slant length.
    float h = max(worldY + SceneFloorAltitude, 0.0);
    // Path length to the top of the atmosphere along the sun dir (clamp the grazing/below-horizon explosion).
    float cosZenith = clamp(SunDirection.y, -1.0, 1.0);
    float topDist = (Ra - (Rp + h)) / max(cosZenith, 0.02);   // below-horizon sun -> huge column -> ~0 transmit
    topDist = clamp(topDist, 0.0, 200000.0);
    float3 depths = Densities(worldY) * topDist;
    return exp(-Extinction(depths));
}

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    if (id.x >= VolumeSize.x || id.y >= VolumeSize.y || id.z >= VolumeSize.z) return;

    // Froxel center in [0,1]^3. xy -> screen, z -> normalized depth slice (linear in distance, squared so near
    // slices are finer where the haze gradient matters most).
    float2 uv = (float2(id.xy) + 0.5) / float2(VolumeSize.xy);
    float sliceT = (float(id.z) + 0.5) / float(VolumeSize.z);
    float farThisSlice = MaxDistance * sliceT * sliceT;

    // View ray from the camera through this froxel's screen position (unproject at the near plane direction).
    float4 ndc = float4(uv.x * 2.0 - 1.0, (1.0 - uv.y) * 2.0 - 1.0, 0.5, 1.0);
    float4 wp = mul(ndc, InvViewProj);
    float3 worldAtHalf = wp.xyz / wp.w;
    float3 viewDir = normalize(worldAtHalf - CameraPos);

    float mu = dot(viewDir, SunDirection);
    float phaseR = 3.0 / (16.0 * PI) * (1.0 + mu * mu);
    float g = clamp(Anisotropy, -0.95, 0.95);
    float phaseM = HG(mu, g);

    // SCENE-SCALE CALIBRATION. The physical betas (~5..33e-6 /m) are tuned for the KILOMETRE atmosphere column;
    // over a scene-scale vista (tens-to-hundreds of metres) the raw optical depth is ~1e-3 and aerial perspective
    // is invisible. So we keep the physical per-channel COLOUR (the Rayleigh blue tilt + Mie grey) but recalibrate
    // the MAGNITUDE: the extinction is set so transmittance reaches ~1/e around HazeHalfDistance (derived from the
    // volume's MaxDistance). DensityScale scales that — 1 = the default tasteful vista haze, higher = thicker air.
    // This is the same "Distance is the artistic half-distance, colour comes from the beta ratio" calibration the
    // old analytic shader used, now driving a physically-marched froxel volume.
    float greenExt = BetaR.g * AirDensity;                       // reference channel for the colour ratio
    float3 rayleighColour = (BetaR * AirDensity) / max(greenExt, 1e-12);  // per-channel blue tilt, ~1 at green
    float3 mieColour = (float3)(BetaM * 1.11 * Haze / max(greenExt, 1e-12) * 0.35);  // grey-ish Mie share
    // Half-distance: where transmittance ~= 1/e at DensityScale 1. ~40% of the volume depth reads as a strong but
    // not opaque vista haze over a street/landscape; tunable via DensityScale.
    float hazeHalf = max(MaxDistance * 0.4, 1.0);
    float baseSigma = (1.0 / hazeHalf) * DensityScale;          // per-metre green-channel extinction (scene-scale)
    float3 sigmaColour = rayleighColour + mieColour;            // combined extinction colour (green ~= 1+0.35)

    // Single-scatter march from the camera to this slice's far distance. Each step: scene-scale extinction ->
    // in-scatter = (sun*sunTransmittance*phase + ambientSky) weighted by the running transmittance.
    float3 transmittance = (float3)1.0;
    float3 inscatter = (float3)0.0;
    float seg = farThisSlice / float(MARCH_STEPS);
    [loop] for (int s = 0; s < MARCH_STEPS; s++) {
        float t = (float(s) + 0.5) * seg;
        float3 p = CameraPos + viewDir * t;

        // Haze only builds beyond StartDistance: ramp the local scattering in so foreground / interiors stay
        // crisp (the artistic near-cut so a 30 m room isn't hazed).
        float nearGate = smoothstep(StartDistance, StartDistance * 2.0 + 1.0, t);

        float3 stepExt = baseSigma * sigmaColour * seg * nearGate;   // per-segment extinction (RGB)
        float3 stepT = exp(-stepExt);

        // In-scattered radiance under this segment. The scattering coefficient ~= the extinction (single-scatter
        // albedo ~1 for air). Sun term (phase-weighted, atmosphere-attenuated) + near-isotropic sky fill.
        float3 sigmaR = baseSigma * rayleighColour * seg * nearGate;
        float3 sigmaM = baseSigma * mieColour * seg * nearGate;
        float3 sunTr = SunTransmittance(p.y);
        float3 sunScatter = (sigmaR * phaseR + sigmaM * phaseM) * SunRadiance * sunTr;
        float3 ambScatter = (sigmaR + sigmaM * 0.5) * SkyTint;   // sky-tinted skylight fill
        float3 stepInscatter = sunScatter + ambScatter;

        // Energy-conserving integration: integrate the in-scatter under the segment's own transmittance.
        float3 segIntegral = (stepInscatter - stepInscatter * stepT) / max(stepExt, (float3)1e-7);
        inscatter += transmittance * segIntegral;
        transmittance *= stepT;
    }

    inscatter *= Intensity * Tint;
    // Store inscatter (rgb) + a scalar mean transmittance (a) for the fixed-function dest-multiply. The chromatic
    // dimming lives in the additive inscatter (sky-blue tilt); the scalar a just darkens the distant scene — the
    // standard Hillaire AP simplification (a 2nd MRT for full RGB transmittance is a possible follow-up).
    float avgT = dot(transmittance, (float3)0.33333);
    // Lerp the transmittance toward 1 by (1-Intensity) so Intensity also fades the dimming, not just the colour.
    avgT = lerp(1.0, avgT, saturate(Intensity));
    inscatter = min(inscatter, (float3)60000.0);   // fp16 safety
    ApVolume[id] = float4(inscatter, avgT);
}
