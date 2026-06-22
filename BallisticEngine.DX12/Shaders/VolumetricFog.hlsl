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

    // --- God rays (aesthetic shafts, density decoupled from the fog) ---
    float3   ShaftTint;          float ShaftIntensity;  // ShaftIntensity==0 ⇒ shaft layer off (CPU gate)
    float    ShaftDensity; float ShaftDecay; float ShaftSharpness; float ShaftPad;

    // --- Volumetric dust (procedural sun-lit motes) ---
    float3   DustDrift;          float DustIntensity;   // DustIntensity==0 ⇒ dust layer off (CPU gate)
    float    DustSize; float DustSparkle; float Time; float DustPad;
};

Texture2D      DepthTex      : register(t0);
Texture2DArray ShadowCascades: register(t1);
Texture2D      SceneTex      : register(t2);   // combine pass: full-res lit HDR scene (unused by the march)
Texture2D      FogHalfTex    : register(t3);   // combine pass: half-res (scatter.rgb, transmittance.a)
SamplerState   LinearClamp   : register(s0);
SamplerState   PointClamp    : register(s1);   // combine: nearest-depth tap so a tile doesn't straddle a silhouette

// HalfTexel = 1/half-res size (combine only); InvProjection for the depth-aware upsample (LinearDepth).
cbuffer FogCombineConstants : register(b1) {
    float4x4 InvProjection;   // transposed on upload
    float2   HalfTexel;       float2 CombinePad;
};

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

// --- Procedural dust noise (cheap 3D value noise) ---
float3 Hash33(float3 p) {
    p = float3(dot(p, float3(127.1, 311.7, 74.7)),
               dot(p, float3(269.5, 183.3, 246.1)),
               dot(p, float3(113.5, 271.9, 124.6)));
    return frac(sin(p) * 43758.5453);
}
float ValueNoise3(float3 p) {
    float3 i = floor(p), f = frac(p);
    float3 u = f * f * (3.0 - 2.0 * f);
    float n000 = Hash33(i + float3(0, 0, 0)).x;
    float n100 = Hash33(i + float3(1, 0, 0)).x;
    float n010 = Hash33(i + float3(0, 1, 0)).x;
    float n110 = Hash33(i + float3(1, 1, 0)).x;
    float n001 = Hash33(i + float3(0, 0, 1)).x;
    float n101 = Hash33(i + float3(1, 0, 1)).x;
    float n011 = Hash33(i + float3(0, 1, 1)).x;
    float n111 = Hash33(i + float3(1, 1, 1)).x;
    float nx00 = lerp(n000, n100, u.x), nx10 = lerp(n010, n110, u.x);
    float nx01 = lerp(n001, n101, u.x), nx11 = lerp(n011, n111, u.x);
    return lerp(lerp(nx00, nx10, u.y), lerp(nx01, nx11, u.y), u.z);
}
// DISCRETE dust motes via a cellular (Worley) field: each grid cell holds ONE jittered point; the mote is a
// sharp radial falloff around it — distinct sparkling specks in empty air, NOT the soft cloudy blobs that
// value noise produces. Only a random subset of cells actually carry a mote (presence hash), so the volume
// stays sparse. DustSize scales the cell frequency (higher ⇒ smaller, denser motes).
float DustMotes(float3 worldPos) {
    float3 p = (worldPos + DustDrift * Time) * (DustSize * 1.5);
    float3 cell = floor(p), f = p - cell;
    float acc = 0.0;
    // Search the 3x3x3 neighbourhood so a mote near a cell edge still lights this sample.
    [unroll] for (int dz = -1; dz <= 1; dz++)
    [unroll] for (int dy = -1; dy <= 1; dy++)
    [unroll] for (int dx = -1; dx <= 1; dx++) {
        float3 o = float3(dx, dy, dz);
        float3 h = Hash33(cell + o);           // h.xyz = jittered point in the cell; reuse .x as presence
        if (h.x > 0.82) {                      // ~18% of cells carry a mote → sparse
            float3 pt = o + h;                 // point position relative to our cell origin
            float d = length(f - pt);
            acc += saturate(1.0 - d / 0.35);   // sharp radial mote (radius 0.35 of a cell)
        }
    }
    float m = saturate(acc);
    return m * m;                              // tighten the core → punctate sparkle
}
float DustField(float3 worldPos) { return DustMotes(worldPos); }

// The march. Output (scatter.rgb, transmittance.a). Run full-res (legacy blend path) OR half-res (the new
// default; a PSCombine then depth-aware-upsamples + composites). Depth is point-sampled so a half-res sample's
// world-pos reconstruction stays on ONE surface (a bilinear-blended depth across a silhouette smears the march).
float4 PSMarch(VSOut i) : SV_Target {
    float depth = DepthTex.SampleLevel(PointClamp, i.Uv, 0).r;
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

    // God-ray shaft layer: shadow-gated sun in-scatter with its OWN phase (ShaftSharpness) and an
    // accumulation weight (ShaftDensity) decoupled from the fog's SigmaT — so shafts are visible even
    // when the fog density is at a physical (low) value. ShaftIntensity==0 ⇒ CPU left it off, skip.
    bool shaftOn = ShaftIntensity > 1e-6;
    float shaftPhase = HG(mu, clamp(ShaftSharpness, 0.0, 0.97));
    float3 shaftAccum = 0;

    // Dust layer: procedural motes lit by the (shadow-gated) sun, forward-scattered. Pure additive glow —
    // does NOT contribute to extinction (transmittance), so it never thickens the air. DustIntensity==0 ⇒ off.
    bool dustOn = DustIntensity > 1e-6;
    float dustPhase = HG(mu, 0.55);
    float3 dustAccum = 0;

    float3 scatter = 0; float transmittance = 1.0;
    [loop] for (int s = 0; s < steps; s++) {
        float t = (s + jitter) * stepLen;
        float3 p = rayStart + rayDir * t;
        float vis = -1.0;   // lazily evaluated shadow visibility (shared by fog/shaft/dust)
        float sigma = SigmaT(p.y);
        if (sigma > 1e-6) {
            float stepT = exp(-sigma * stepLen);
            vis = SunVisibility(p);
            float3 src = ALBEDO * (sunSource * vis + ambSource);
            scatter += src * (transmittance * (1.0 - stepT));
            transmittance *= stepT;
        }
        if (shaftOn) {
            if (vis < 0.0) vis = SunVisibility(p);
            float decay = exp(-ShaftDecay * t);
            shaftAccum += (vis * shaftPhase * ShaftDensity * decay * stepLen) * transmittance;
        }
        if (dustOn) {
            // Floating dust reads as a NEAR-camera effect — fade it out past ~12 m so it never becomes a
            // sky-wide texture on distant geometry (which looked like fog, not motes).
            float distFade = saturate(1.0 - t / 12.0);
            float mote = DustField(p) * distFade;
            if (mote > 1e-3) {
                if (vis < 0.0) vis = SunVisibility(p);
                // Motes catch the sun (shadow-gated, forward-scattered) AND pick up a little skylight, so
                // they stay faintly visible in shade / sunless scenes instead of vanishing.
                float3 lit = SunColor * (vis * dustPhase) + SkyAmbient * 0.15;
                dustAccum += (mote * stepLen) * transmittance * lit;
            }
        }
        if (transmittance < 0.002) break;
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

    // Aesthetic layers, added AFTER the fog tint so they grade independently. Shaft uses the shadowed sun
    // colour with its own tint; dust already carries its sun+sky lighting from the march (just scale here).
    if (shaftOn)
        scatter += shaftAccum * SunColor * ShaftTint * ShaftIntensity;
    if (dustOn)
        scatter += dustAccum * (DustIntensity * DustSparkle);

    // NaN/Inf scrub: component SELECT, never mix(v,0,flag) (float mix is arithmetic and NaN*0==NaN —
    // proven leak in temporal-feedback shaders; see the project gotchas).
    if (any(isnan(scatter)) || any(isinf(scatter))) scatter = 0;

    // Fog now composites in HDR (the scene target is R16F; the final composite tonemaps). Output RAW HDR
    // scatter + transmittance; the composite = dest*transmittance + scatter, all pre-tonemap. (Exposure unused.)
    return float4(scatter, saturate(transmittance));
}

float LinearDepthFog(float d) {
    float4 v = mul(float4(0.0, 0.0, d, 1.0), InvProjection);
    return v.z / v.w;
}
// Component-SELECT NaN/Inf scrub (never mix(v,0,flag): NaN*0==NaN — the proven AMD leak). The EXR sun's
// in-scatter can produce an Inf the bilinear upsample would otherwise turn into a screen-eating NaN.
float4 SanitizeFog(float4 v) {
    return float4(isnan(v.x) || isinf(v.x) ? 0.0 : v.x,
                  isnan(v.y) || isinf(v.y) ? 0.0 : v.y,
                  isnan(v.z) || isinf(v.z) ? 0.0 : v.z,
                  isnan(v.w) || isinf(v.w) ? 0.0 : v.w);
}

// COMBINE: depth-aware bilinear upsample of the half-res (scatter, transmittance), then the SAME composite the
// old fixed-function blend did — outColor = scene*transmittance + scatter. Mirrors Ssr.hlsl PSCombine; only the
// final op differs (fog composite vs SSR's Fresnel lerp). Output is the new full-res scene color.
float4 PSCombine(VSOut i) : SV_Target {
    float3 scene = SceneTex.SampleLevel(LinearClamp, i.Uv, 0).rgb;

    float2 fogSize = 1.0 / HalfTexel;
    float2 pos = i.Uv * fogSize - 0.5;
    float2 baseUV = (floor(pos) + 0.5) * HalfTexel;
    float2 f = frac(pos);
    float centerZ = LinearDepthFog(DepthTex.SampleLevel(LinearClamp, i.Uv, 0).r);

    float4 acc = 0.0.xxxx; float wSum = 0.0;
    [unroll] for (int k = 0; k < 4; k++) {
        float2 corner = float2(k & 1, k >> 1);
        float2 uv = baseUV + corner * HalfTexel;
        float wBil = (corner.x > 0.5 ? f.x : 1.0 - f.x) * (corner.y > 0.5 ? f.y : 1.0 - f.y);
        float tapZ = LinearDepthFog(DepthTex.SampleLevel(LinearClamp, uv, 0).r);
        float wDepth = 1.0 / (1.0 + abs(tapZ - centerZ) * 2.0);
        float w = wBil * wDepth + 1e-5;
        acc += SanitizeFog(FogHalfTex.SampleLevel(LinearClamp, uv, 0)) * w;
        wSum += w;
    }
    float4 fog = acc / wSum;            // fog.rgb = scatter, fog.a = transmittance
    return float4(scene * fog.a + fog.rgb, 1.0);   // == the old blend dest*transmittance + scatter
}
