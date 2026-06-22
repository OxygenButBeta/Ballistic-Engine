// Lumen FAZ 3c — CARD-CAPTURE DEBUG BLIT. A fullscreen pass that blits one selected surface-cache atlas (albedo /
// card-normal / emissive / card-depth) straight to the HDR scene color so the captured material attributes are
// VISIBLE and capture correctness is provable. Screen UV maps directly to atlas UV (the whole packed atlas fills the
// frame). Opaque replace. Gated by BALLISTIC_DX12_LUMEN_CAPTURE_DEBUG on the CPU; the atlas is chosen by
// BALLISTIC_DX12_LUMEN_CAPTURE_VIEW (albedo|normal|emissive|depth) → the Mode constant below.
//
// Driver note (FAZ 3b lesson): no loop-carried int / branch / lerp color paths — plain saturate'd float arithmetic.

cbuffer CaptureDebugConstants : register(b0) {
    uint  Mode;        // 0 albedo, 1 normal, 2 emissive, 3 depth
    float Scale;       // visualization gain (depth/normal pushed brighter so they survive tonemapping)
    float2 Pad;
};

Texture2D AtlasTex      : register(t0);
SamplerState PointClamp : register(s0);

struct VSOut { float4 Position : SV_Position; float2 Uv : TEXCOORD0; };

VSOut VSDebug(uint vid : SV_VertexID) {
    VSOut o;
    float2 uv = float2((vid << 1) & 2, vid & 2);
    o.Position = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
    o.Uv = uv;
    return o;
}

float4 PSDebug(VSOut i) : SV_Target {
    float4 s = AtlasTex.Sample(PointClamp, i.Uv);

    // Albedo: show as-is (already [0,1] linear-ish). Emissive: scaled (can be > 1, HDR). Normal: only .rg are packed
    // (XY*0.5+0.5), B holds the reconstructed Z midpoint (0.5) so a flat card reads ~teal. Depth: replicate the single
    // R channel to grayscale. All paths saturate-clamped for a clean opaque replace.
    float3 col;
    if (Mode == 1) {
        col = float3(s.r, s.g, 0.5);                 // card-space normal XY (+ flat-Z midpoint)
    } else if (Mode == 2) {
        col = s.rgb;                                  // emissive radiance (already HDR)
    } else if (Mode == 3) {
        col = s.rrr;                                  // card-space linear depth, grayscale
    } else {
        col = s.rgb;                                  // albedo
    }
    // Visualization gain so depth/normal/albedo survive the post tonemap (Scale ~ a few). A tiny ambient floor keeps
    // the packed atlas layout readable; captured pages pop above it. All saturate-clamped for a clean opaque replace.
    col = saturate(col) * Scale + float3(0.008, 0.008, 0.012);
    return float4(col, 1.0);
}
