// Thin-lens DEPTH OF FIELD (bokeh) for the DX12 backend. Four fullscreen sub-passes share this file:
//   PSCoc       — full depth → half-res: downsample the sharp HDR scene color + compute a SIGNED circle-of-
//                 confusion (CoC) per pixel from the thin-lens model. Negative = foreground/near, positive =
//                 background/far. CoC is a fraction of frame height, clamped to MaxCoc.
//   PSDilate    — near-field max-CoC dilation (half-res): spread the near CoC OUTWARD (a small disk max) so the
//                 foreground bokeh bleeds over the focused background — the correct direction (no hard silhouette).
//   PSGather    — golden-angle 48-tap sunflower gather (half-res). FAR field is depth/CoC-aware: a tap contributes
//                 only if its own |CoC| reaches the kernel radius (focused background can't bleed into the blur).
//                 NEAR field uses the dilated CoC so foreground spreads. This entry writes the FAR field.
//   PSComposite — full-res: blend the sharp color with the bilinearly-upsampled NEAR and FAR fields by a smooth
//                 CoC factor (far under, near over). Output replaces the scene color.
//
// All linearization uses InvProjection (the GTAO convention). NaN/Inf are scrubbed with component SELECT
// ternaries, NEVER lerp(v,0,flag) (float lerp is arithmetic: NaN*0 == NaN — the proven temporal-leak gotcha).

cbuffer DofConstants : register(b0) {
    float4x4 InvProjection;   // transposed-on-CPU NDC→view (mul(ndc, InvProjection) like Gtao)
    float2 TexelSize;         // 1 / half-res size (gather/dilate tap spacing)
    float2 FullTexelSize;     // 1 / full-res size (CoC downsample + composite spacing)
    float  FocusDistance;     // metres to the focal plane
    float  FocalLength;       // lens focal length (m)
    float  Aperture;          // f-number (smaller = shallower)
    float  MaxCoc;            // CoC clamp as a fraction of frame height
    float  Near;
    float  Far;
    float2 _pad;
};

Texture2D Tex0 : register(t0);   // CoC: sharp scene color | dilate/gather: dofHalf | composite: sharp scene color
Texture2D Tex1 : register(t1);   // gather: dofNear (dilated near CoC) | composite: far field
Texture2D Tex2 : register(t2);   // CoC: scene depth | composite: near field
SamplerState LinearClamp : register(s0);
SamplerState PointClamp  : register(s1);

struct VSOut { float4 Position : SV_Position; float2 Uv : TEXCOORD0; };
VSOut VSMain(uint vid : SV_VertexID) {
    VSOut o;
    float2 uv = float2((vid << 1) & 2, vid & 2);
    o.Position = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
    o.Uv = uv;
    return o;
}

// Scrub NaN/Inf via a component SELECT (never lerp with a flag — NaN*0 == NaN).
float3 ScrubRgb(float3 c) {
    return float3(isfinite(c.x) ? c.x : 0.0,
                  isfinite(c.y) ? c.y : 0.0,
                  isfinite(c.z) ? c.z : 0.0);
}

// Linear VIEW-space depth (positive metres) from a D3D NDC depth via InvProjection (GTAO convention).
float LinearizeDepth(float ndcZ, float2 uv) {
    float2 ndcXY = float2(uv.x * 2.0 - 1.0, (1.0 - uv.y) * 2.0 - 1.0);
    float4 ndc = float4(ndcXY, ndcZ, 1.0);
    float4 v = mul(ndc, InvProjection);
    return -v.z / max(abs(v.w), 1e-8);   // view-space Z is negative (RH); return positive distance
}

// Signed thin-lens CoC (as a fraction of frame height). diameter = |S2 - S1| / S2 * f^2 / (N * (S1 - f)),
// where S1 = focus distance, S2 = scene depth, f = focal length, N = f-number. Sensor height normalizes it to a
// frame-height fraction (the renderer-side MaxCoc clamp is in those units). Sign: far (S2 > S1) = +, near = -.
float SignedCoc(float sceneDepth) {
    float s1 = FocusDistance;
    float s2 = max(sceneDepth, 1e-3);
    float f  = FocalLength;
    // Lens equation magnitude; guard s1 ≈ f (focus at the focal length → degenerate).
    float denom = max(Aperture * (s1 - f), 1e-5);
    float diamM = abs(s2 - s1) / s2 * (f * f) / denom;   // CoC diameter in metres on the sensor
    // Map the sensor-metre CoC to a frame-height fraction. A 24mm-tall full-frame sensor is the reference; the
    // exact constant is folded into MaxCoc by the artist, so we use a fixed sensor height here.
    const float SensorHeight = 0.024;   // 24mm
    float cocFrac = diamM / SensorHeight;
    cocFrac = min(cocFrac, MaxCoc);
    return (s2 >= s1) ? cocFrac : -cocFrac;   // + far, - near
}

// ===== Sub-pass 0: CoC + half-res downsample =================================================================
// Downsample the sharp scene color (4-tap box at the half-res texel's full-res footprint) and compute the CoC
// from the (point-sampled) full-res depth at the texel centre. Output: rgb = color, a = signed CoC.
float4 PSCoc(VSOut i) : SV_Target {
    float2 uv = i.Uv;
    float2 ft = FullTexelSize;
    float3 c = Tex0.SampleLevel(LinearClamp, uv + float2(-0.5, -0.5) * ft, 0).rgb
             + Tex0.SampleLevel(LinearClamp, uv + float2( 0.5, -0.5) * ft, 0).rgb
             + Tex0.SampleLevel(LinearClamp, uv + float2(-0.5,  0.5) * ft, 0).rgb
             + Tex0.SampleLevel(LinearClamp, uv + float2( 0.5,  0.5) * ft, 0).rgb;
    c *= 0.25;
    c = ScrubRgb(c);
    float ndcZ = Tex2.SampleLevel(PointClamp, uv, 0).r;
    float depth = LinearizeDepth(ndcZ, uv);
    float coc = SignedCoc(depth);
    return float4(c, coc);
}

// ===== Sub-pass 1: near-field max-CoC dilation ===============================================================
// Spread the NEAR (negative) CoC outward with a small disk max so the foreground bokeh expands past its
// geometric silhouette. Far (positive) CoC is passed through unchanged. Output: rgb = color, a = dilated CoC.
#define DILATE_TAPS 8
float4 PSDilate(VSOut i) : SV_Target {
    float2 uv = i.Uv;
    float4 center = Tex0.SampleLevel(LinearClamp, uv, 0);
    float nearCoc = max(-center.a, 0.0);   // magnitude of the near (negative) CoC; 0 if this pixel is far/focused
    // Dilate the near CoC by sampling a ring scaled by the local + neighbour near CoC. Radius in half-res texels.
    float radius = MaxCoc / max(TexelSize.y, 1e-6) * 0.5;   // MaxCoc is a frame-height fraction → half-res texels
    [unroll] for (int t = 0; t < DILATE_TAPS; t++) {
        float ang = (t / float(DILATE_TAPS)) * 6.2831853;
        float2 off = float2(cos(ang), sin(ang)) * TexelSize * radius;
        float n = max(-Tex0.SampleLevel(LinearClamp, uv + off, 0).a, 0.0);
        nearCoc = max(nearCoc, n);
    }
    return float4(ScrubRgb(center.rgb), nearCoc);   // a >= 0 = dilated near magnitude
}

// 48-tap golden-angle sunflower disk: tap k at angle k*φ, radius sqrt((k+0.5)/N). φ = 2π(1 - 1/golden).
#define GATHER_TAPS 48
static const float GOLDEN_ANGLE = 2.39996323;   // radians

// ===== Sub-pass 2: gather bokeh (writes the FAR field) =======================================================
// FAR-field gather: a sample contributes to this pixel's blur only if ITS OWN far CoC reaches the kernel radius
// (depth/CoC-aware), so a focused background pixel never smears into a blurred neighbour. Output: rgb = blurred
// color (premultiplied by coverage), a = coverage (for the composite to normalize + know the blur amount).
float4 PSGather(VSOut i) : SV_Target {
    float2 uv = i.Uv;
    float4 c0 = Tex0.SampleLevel(LinearClamp, uv, 0);      // dofHalf at centre (rgb + signed CoC.a)
    float farCoc = max(c0.a, 0.0);                          // far (positive) CoC magnitude as a frame-height frac
    // Kernel radius in half-res texels from the centre's far CoC.
    float radiusPx = farCoc / max(TexelSize.y, 1e-6);
    if (radiusPx < 0.75) {
        // In focus / no far blur — return the sharp colour with zero coverage so the composite keeps it crisp.
        return float4(ScrubRgb(c0.rgb), 0.0);
    }
    float3 sum = 0.0;
    float weight = 0.0;
    [loop] for (int k = 0; k < GATHER_TAPS; k++) {
        float r = sqrt((k + 0.5) / float(GATHER_TAPS));
        float ang = k * GOLDEN_ANGLE;
        float2 off = float2(cos(ang), sin(ang)) * r * radiusPx * TexelSize;
        float4 s = Tex0.SampleLevel(LinearClamp, uv + off, 0);
        float sCoc = max(s.a, 0.0);                         // the sample's own far CoC magnitude
        // Spread test: the sample reaches the centre if its CoC (in texels) >= the tap's distance from centre.
        float sampleRadiusPx = sCoc / max(TexelSize.y, 1e-6);
        float tapDistPx = r * radiusPx;
        float w = saturate(sampleRadiusPx - tapDistPx + 1.0);   // smooth 1px feather at the boundary
        sum += ScrubRgb(s.rgb) * w;
        weight += w;
    }
    float3 col = (weight > 1e-4) ? sum / weight : ScrubRgb(c0.rgb);
    float coverage = saturate(radiusPx / max(1.0, radiusPx));   // 1 when any far blur present
    return float4(col, coverage);
}

// Near-field gather, inlined into the composite (it reads the dilated near CoC from Tex2). A separate full gather
// of the near field would need a 5th target; instead we gather the near field at composite time from dofNear.
float4 GatherNear(float2 uv, float dilatedNearCoc) {
    float radiusPx = dilatedNearCoc / max(TexelSize.y, 1e-6);
    if (radiusPx < 0.75) return float4(0, 0, 0, 0);
    float3 sum = 0.0;
    float weight = 0.0;
    [loop] for (int k = 0; k < GATHER_TAPS; k++) {
        float r = sqrt((k + 0.5) / float(GATHER_TAPS));
        float ang = k * GOLDEN_ANGLE;
        float2 off = float2(cos(ang), sin(ang)) * r * radiusPx * TexelSize;
        float4 s = Tex2.SampleLevel(LinearClamp, uv + off, 0);   // dofNear: rgb=color, a=dilated near CoC
        float sCoc = max(s.a, 0.0);
        float sampleRadiusPx = sCoc / max(TexelSize.y, 1e-6);
        float tapDistPx = r * radiusPx;
        float w = saturate(sampleRadiusPx - tapDistPx + 1.0);
        sum += ScrubRgb(s.rgb) * w;
        weight += w;
    }
    float3 col = (weight > 1e-4) ? sum / weight : 0.0;
    return float4(col, saturate(radiusPx / max(1.0, radiusPx)));
}

// ===== Sub-pass 3: composite =================================================================================
// Blend sharp(t0) with the upsampled FAR(t1) and the freshly-gathered NEAR (from dofNear t2). Far blends under by
// its coverage; near blends OVER everything (foreground bokeh occludes the background). Smooth CoC transitions.
float4 PSComposite(VSOut i) : SV_Target {
    float2 uv = i.Uv;
    float3 sharp = ScrubRgb(Tex0.SampleLevel(LinearClamp, uv, 0).rgb);

    // FAR field (already gathered to half-res). Coverage drives a smooth blend in.
    float4 farS = Tex1.SampleLevel(LinearClamp, uv, 0);
    float3 farCol = ScrubRgb(farS.rgb);
    float farCov = saturate(farS.a);

    // NEAR field: dilated near CoC lives in dofNear.a; gather it here so it spreads past silhouettes.
    float dilatedNear = max(Tex2.SampleLevel(LinearClamp, uv, 0).a, 0.0);
    float4 nearS = GatherNear(uv, dilatedNear);
    float3 nearCol = nearS.rgb;
    float nearCov = saturate(nearS.a);

    // Composite: start sharp, blend the far field in by its coverage, then the near field over everything.
    float3 col = lerp(sharp, farCol, farCov);
    col = lerp(col, nearCol, nearCov);
    return float4(ScrubRgb(col), 1.0);
}
