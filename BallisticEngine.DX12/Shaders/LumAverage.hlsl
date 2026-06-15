// Auto-exposure luminance reduction for the DX12 backend. A single fullscreen pass into a 1×1 R16F
// target: sample a coarse grid of the HDR scene, average the LOG luminance (geometric mean — the
// standard exposure metering, robust to a few bright pixels), and write exp(avg) = the scene's average
// luminance. The composite reads this 1×1 texel and derives exposure = Key / avgLum. No compute/UAV/
// readback — just one tiny RTV pass; deterministic, fine for the headless screenshot path.
//
// (A mip-pyramid or compute reduction would be more precise/cheaper at high res; this grid average is
// plenty for metering and keeps the infrastructure minimal — a follow-up can upgrade it.)

Texture2D HdrColor : register(t0);
SamplerState LinearClamp : register(s0);

struct VSOut { float4 Position : SV_Position; float2 Uv : TEXCOORD0; };
VSOut VSMain(uint vid : SV_VertexID) {
    VSOut o;
    float2 uv = float2((vid << 1) & 2, vid & 2);
    o.Position = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
    o.Uv = uv;
    return o;
}

float Luminance(float3 c) { return dot(c, float3(0.2126, 0.7152, 0.0722)); }

float4 PSMain(VSOut i) : SV_Target {
    const int GRID = 32;                       // 32×32 = 1024 samples across the frame
    float logSum = 0.0; int n = 0;
    [loop] for (int y = 0; y < GRID; y++) {
        [loop] for (int x = 0; x < GRID; x++) {
            float2 uv = (float2(x, y) + 0.5) / GRID;
            float3 hdr = HdrColor.SampleLevel(LinearClamp, uv, 0).rgb;
            float lum = max(Luminance(hdr), 1e-4);
            logSum += log(lum);
            n++;
        }
    }
    float avgLum = exp(logSum / n);            // geometric mean luminance
    return float4(avgLum, avgLum, avgLum, 1.0);
}
