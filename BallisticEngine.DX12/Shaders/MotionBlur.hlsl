// McGuire-style per-pixel velocity motion blur for the DX12 deferred renderer ("A Reconstruction Filter for
// Plausible Motion Blur", McGuire et al. 2012). Runs at PostProcess AFTER the TAA/FSR resolve produces the
// final-resolution HDR scene color and BEFORE Composite tonemap, so it smears the resolved scene radiance and
// any subsequent DoF blurs the smeared result.
//
// Pipeline (3 draws, all fullscreen triangles):
//   1. TileMax       : downsample the (scaled+clamped) G-buffer velocity to tiles of K=TileSize px, each tile =
//                      the MAX-MAGNITUDE velocity inside it.
//   2. NeighbourMax  : each tile takes the max-magnitude velocity among its 3x3 tile neighbours, so a fast
//                      object bleeds its blur trail into the tiles next to it (no hard tile boundary).
//   3. Reconstruction: per full-res pixel, march MotionBlurSamples taps along the neighbour-max velocity,
//                      weighting each tap by a depth-aware foreground/background + velocity-aware cone/cylinder
//                      weight, jittering the march start with a per-pixel dither (FROZEN to 0 under
//                      deterministic capture) to break banding. Accumulate colour+weight, normalize.
//
// Velocity source = the G-buffer MOTION RT (RT4, RG16F, value = prevUV - currUV in UV space — the SAME source
// TAA reprojects with). The on-screen PIXEL travel direction is currUV-prevUV = -motion, so velocity = -motion,
// scaled by MotionBlurIntensity (a shutter fraction) and clamped to MotionBlurMaxVelocity (fraction of frame).

cbuffer MotionBlurConstants : register(b0) {
    float2 TexelSize;       // 1 / full-res render size (reconstruction sample step)
    float2 TileTexelSize;   // 1 / tile-grid size (NeighbourMax neighbour step)
    // row 1
    float  Intensity;       // MotionBlurIntensity — shutter fraction applied to the raw velocity
    float  MaxVelocity;     // MotionBlurMaxVelocity — clamp on |velocity| (fraction of frame)
    float  SampleCount;     // MotionBlurSamples — gather taps along the velocity vector
    float  TileSize;        // K — max blur radius in px (= tile size in px)
    // row 2
    float  Dither;          // 1 = animated per-pixel dither, 0 = frozen (deterministic capture)
    float3 _pad0;
};

Texture2D SceneColor : register(t0);   // resolved HDR scene colour (reconstruction reads colour taps)
Texture2D MotionTex  : register(t1);   // RG16F G-buffer motion (prevUV - currUV)
Texture2D DepthTex   : register(t2);   // R32F scene depth (foreground/background ordering)
Texture2D VelTile    : register(t3);   // velocity tiles (TileMax output -> NeighbourMax input -> recon input)
SamplerState PointClamp : register(s0);

struct VSOut { float4 Position : SV_Position; float2 Uv : TEXCOORD0; };
VSOut VSMain(uint vid : SV_VertexID) {
    VSOut o;
    float2 uv = float2((vid << 1) & 2, vid & 2);
    o.Position = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
    o.Uv = uv;
    return o;
}

// NaN/Inf scrub as a true component SELECT (lerp/mix(v,0,flag) keeps NaN: NaN*0==NaN — the engine's hard rule).
float2 Sanitize2(float2 v) {
    return float2(isnan(v.x) || isinf(v.x) ? 0.0 : v.x,
                  isnan(v.y) || isinf(v.y) ? 0.0 : v.y);
}
float3 Sanitize3(float3 v) {
    return float3(isnan(v.x) || isinf(v.x) ? 0.0 : v.x,
                  isnan(v.y) || isinf(v.y) ? 0.0 : v.y,
                  isnan(v.z) || isinf(v.z) ? 0.0 : v.z);
}

// Raw G-buffer motion -> the on-screen PIXEL velocity in UV space: currUV-prevUV = -motion, scaled by the
// shutter fraction and clamped to the max-velocity budget (fraction of frame). Returns UV-space velocity.
float2 PixelVelocity(float2 uv) {
    float2 motion = Sanitize2(MotionTex.SampleLevel(PointClamp, uv, 0).rg);
    float2 vel = -motion * Intensity;
    float len = length(vel);
    if (len > MaxVelocity) vel *= MaxVelocity / max(len, 1e-8);
    return vel;
}

float Hash12(float2 p) { return frac(sin(dot(p, float2(12.9898, 78.233))) * 43758.5453); }

// ===== Pass 1: TileMax — max-magnitude velocity over a TileSize x TileSize px block ==========================
// Drawn into the tile-grid RT: i.Uv is the TILE-grid UV. The tile index = floor(i.Uv / TileTexelSize); each
// full-res texel inside it is (tileIdx * k + (x,y) + 0.5) full-res texels = that * TexelSize in full-res UV.
float4 PSTileMax(VSOut i) : SV_Target {
    int k = (int)max(TileSize, 1.0);
    float2 tileIdx = floor(i.Uv / TileTexelSize);
    float2 maxVel = 0.0.xx;
    float  maxLen = -1.0;
    [loop] for (int y = 0; y < k; y++) {
        [loop] for (int x = 0; x < k; x++) {
            float2 fullUv = (tileIdx * (float)k + float2(x, y) + 0.5) * TexelSize;
            float2 v = PixelVelocity(fullUv);
            float  l = dot(v, v);
            if (l > maxLen) { maxLen = l; maxVel = v; }
        }
    }
    return float4(maxVel, 0, 0);
}

// ===== Pass 2: NeighbourMax — max-magnitude velocity among the 3x3 tile neighbours ===========================
float4 PSNeighbourMax(VSOut i) : SV_Target {
    float2 maxVel = 0.0.xx;
    float  maxLen = -1.0;
    [unroll] for (int y = -1; y <= 1; y++)
    [unroll] for (int x = -1; x <= 1; x++) {
        float2 v = VelTile.SampleLevel(PointClamp, i.Uv + float2(x, y) * TileTexelSize, 0).rg;
        float  l = dot(v, v);
        if (l > maxLen) { maxLen = l; maxVel = v; }
    }
    return float4(maxVel, 0, 0);
}

// ===== Pass 3: Reconstruction gather =========================================================================
// Cone weight (McGuire eq. ~): how much a tap at distance `dist` along a smear of length `len` contributes —
// 1 at the centre, falling to 0 at the smear tip.
float Cone(float dist, float len) { return saturate(1.0 - dist / max(len, 1e-5)); }
// Cylinder weight: near-flat over the smear, used for the SAMPLE's own velocity (its own trail is ~uniform).
float Cylinder(float dist, float len) { return 1.0 - smoothstep(0.95 * len, 1.05 * len, dist); }
// Soft depth comparison (z is linear-ish device depth; smaller = nearer). +1 if A is in front of B.
float SoftDepthCompare(float za, float zb) { return saturate(1.0 - (za - zb) / max(0.001, min(za, zb))); }

float4 PSReconstruct(VSOut i) : SV_Target {
    float3 centerColor = Sanitize3(SceneColor.SampleLevel(PointClamp, i.Uv, 0).rgb);

    // The dominant velocity in this pixel's neighbourhood (NeighbourMax) drives the gather DIRECTION.
    float2 tileVel = VelTile.SampleLevel(PointClamp, i.Uv, 0).rg;
    float  tileLen = length(tileVel);
    // No appreciable motion anywhere near this pixel -> pass the resolved colour through unchanged (1 texel
    // budget below which a smear is sub-pixel). A SELECT, never a lerp-with-flag.
    if (tileLen < TexelSize.x) return float4(centerColor, 1.0);

    float2 thisVel = PixelVelocity(i.Uv);     // this pixel's OWN velocity (for the foreground/cylinder term)
    float  thisLen = length(thisVel);
    float  centerZ = DepthTex.SampleLevel(PointClamp, i.Uv, 0).r;

    int   samples = (int)max(SampleCount, 1.0);
    // Jittered start offset in [-0.5,0.5] tap, FROZEN to 0 under deterministic capture (Dither=0) so a paused/
    // bal-render frame is byte-identical.
    float jitter = (Hash12(i.Uv * (1.0 / max(TexelSize.x, 1e-6)) + Dither) - 0.5) * Dither;

    // Weighted accumulation, seeded with the centre tap (weight chosen so a static pixel == its own colour).
    float3 sum = centerColor * (1.0 / (float)samples);
    float  totalWeight = 1.0 / (float)samples;

    [loop] for (int s = 0; s < samples; s++) {
        // t in [-1,1] across the smear, centred, with the per-pixel jitter to break banding.
        float t = ((float)s + 0.5 + jitter) / (float)samples * 2.0 - 1.0;
        float2 sampleUv = i.Uv + tileVel * t;
        float3 sColor = Sanitize3(SceneColor.SampleLevel(PointClamp, sampleUv, 0).rgb);
        float  sZ = DepthTex.SampleLevel(PointClamp, sampleUv, 0).r;
        float2 sVel = PixelVelocity(sampleUv);
        float  sLen = length(sVel);

        float dist = abs(t) * tileLen;        // UV-space distance of this tap from the pixel along the smear

        // Foreground (sample is nearer than centre) vs background (sample is behind centre) ordering.
        float fg = SoftDepthCompare(centerZ, sZ);   // sample in FRONT of centre  -> its trail covers us
        float bg = SoftDepthCompare(sZ, centerZ);   // sample BEHIND centre       -> we see through to it

        // Foreground: the sample's own motion (cone over its trail) drags colour onto us.
        // Background: the centre's motion (cylinder over OUR trail) reveals the background through the smear.
        float weight = fg * Cone(dist, sLen)
                     + bg * Cone(dist, thisLen)
                     + Cylinder(dist, max(sLen, thisLen)) * 2.0;

        sum += sColor * weight;
        totalWeight += weight;
    }

    float3 outColor = sum / max(totalWeight, 1e-5);
    return float4(Sanitize3(outColor), 1.0);
}
