// Edge-aware bilateral denoise for the soft RT sun-shadow mask (R8, 1 lit / 0 shadowed). The cone-sampled
// soft mask is noisy at low ray counts; this separable bilateral (PSBlurH then PSBlurV) averages neighbour
// mask texels weighted by depth + normal similarity, so it cleans flat penumbra without bleeding across
// geometry edges. A pure smooth on the HARD mask (1 ray, binary 0/1) is intentionally NOT run — the soft
// path owns the denoise; the hard path skips it entirely on the CPU side (bit-identical to the old result).
//
// Bound: ShadowMask t0, depth t1, world-normal t2, linear-clamp sampler s0, DenoiseConstants b0.

Texture2D<float>  ShadowMask : register(t0);   // input soft mask
Texture2D<float>  Depth      : register(t1);   // scene depth (R32F)
Texture2D<float4> Normal     : register(t2);   // world normal packed [0,1]
SamplerState LinearClamp     : register(s0);

cbuffer DenoiseConstants : register(b0) {
    float2 TexelSize;     // 1 / screen size
    float  DepthSigma;    // depth-difference falloff (relative)
    float  NormalSigma;   // normal-dot falloff power
    float2 Direction;     // (1,0) horizontal pass / (0,1) vertical pass (× TexelSize)
    float2 Pad;
};

struct VSOut { float4 Position : SV_Position; float2 Uv : TEXCOORD0; };
VSOut VSMain(uint vid : SV_VertexID) {
    VSOut o;
    float2 uv = float2((vid << 1) & 2, vid & 2);
    o.Uv = uv;
    o.Position = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
    return o;
}

static const float Weights[5] = { 0.227027, 0.1945946, 0.1216216, 0.054054, 0.016216 };

float3 SampleNormal(float2 uv) { return normalize(Normal.SampleLevel(LinearClamp, uv, 0).rgb * 2.0 - 1.0); }

float Blur(float2 uv) {
    float centerDepth = Depth.SampleLevel(LinearClamp, uv, 0).r;
    float3 centerN    = SampleNormal(uv);

    float sum = ShadowMask.SampleLevel(LinearClamp, uv, 0).r * Weights[0];
    float wsum = Weights[0];

    float2 step = Direction * TexelSize;
    [unroll] for (int i = 1; i < 5; ++i) {
        [unroll] for (int s = -1; s <= 1; s += 2) {
            float2 suv = uv + step * (float(i) * float(s));
            float d = Depth.SampleLevel(LinearClamp, suv, 0).r;
            float3 n = SampleNormal(suv);
            // depth weight: relative difference Gaussian (NaN-safe — depth is finite [0,1])
            float dd = (d - centerDepth) / max(DepthSigma * max(centerDepth, 1e-4), 1e-5);
            float wDepth = exp(-dd * dd);
            // normal weight: dot raised to a power (sharper = more edge-preserving)
            float nd = saturate(dot(n, centerN));
            float wNormal = pow(nd, NormalSigma);
            float w = Weights[i] * wDepth * wNormal;
            sum += ShadowMask.SampleLevel(LinearClamp, suv, 0).r * w;
            wsum += w;
        }
    }
    // wsum >= Weights[0] > 0 always → safe divide; clamp the mask to [0,1].
    return saturate(sum / max(wsum, 1e-5));
}

float PSBlurH(VSOut i) : SV_Target { return Blur(i.Uv); }
float PSBlurV(VSOut i) : SV_Target { return Blur(i.Uv); }
