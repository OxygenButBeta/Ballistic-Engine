// Lumen FAZ 3d — LIT-CACHE DEBUG BLIT. A fullscreen pass that blits the LIT surface-cache atlas (FinalLighting or
// DirectLighting) straight to the HDR scene color so the lit cache is VISIBLE and lighting correctness is provable.
// Screen UV maps directly to atlas UV (the whole packed atlas fills the frame). Opaque replace. The atlas is HDR
// (R11G11B10_Float) so we write it raw into the HDR scene color and let the post tonemap handle it (with a small
// gain dial). Gated by BALLISTIC_DX12_LUMEN_LIGHT_DEBUG; the atlas is chosen by BALLISTIC_DX12_LUMEN_LIGHT_VIEW.
//
// Driver note: no loop-carried int / branch / lerp color paths — plain float arithmetic, NaN-scrubbed via select.

cbuffer LitDebugConstants : register(b0) {
    uint  Mode;        // 0 final, 1 direct
    float Scale;       // visualization gain
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
    float3 s = AtlasTex.Sample(PointClamp, i.Uv).rgb;
    // NaN/Inf scrub via component select (NEVER lerp(v,0,flag)). The lit cache is HDR (raw radiance) — write it
    // straight (tonemap later); a tiny ambient floor keeps the packed atlas layout readable on a dark frame.
    s = float3(isnan(s.x) || isinf(s.x) ? 0.0 : s.x,
               isnan(s.y) || isinf(s.y) ? 0.0 : s.y,
               isnan(s.z) || isinf(s.z) ? 0.0 : s.z);
    float3 col = max(s, 0.0.xxx) * Scale + float3(0.004, 0.004, 0.006);
    return float4(col, 1.0);
}
