// PHASE-3 PROOF FEATURE (chunk 20): the full-screen blit the SceneColorTintFeature drives through the
// backend-agnostic IFeaturePassRecorder. Tints the HDR scene color by `Tint`, faded by `Strength`
// (Strength 0 = passthrough = pixel-neutral when the feature is removed/off). Applied in HDR-LINEAR
// space BEFORE composite/tonemap — a multiplicative color grade, exactly the canonical place a tint
// sits (PostProcess, just before Composite). One entry: PSMain over a fullscreen triangle.
//
// The recorder samples the live SceneColor as t0 (a scratch copy — a DX12 RT cannot be sampled and
// rendered to at once) and writes the tinted result back into SceneColor. So this shader is a pure
// READ→write: it never reads its own output.

cbuffer TintConstants : register(b0) {
    float3 Tint;       // multiplied into the scene color (white = no change)
    float Strength;    // 0 = passthrough, 1 = full tint
};

Texture2D Source : register(t0);     // the scene-color scratch copy
SamplerState PointClamp : register(s0);

struct VSOut { float4 Position : SV_Position; float2 Uv : TEXCOORD0; };

VSOut VSMain(uint vid : SV_VertexID) {
    VSOut o;
    float2 uv = float2((vid << 1) & 2, vid & 2);
    o.Position = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
    o.Uv = uv;
    return o;
}

float4 PSMain(VSOut i) : SV_Target {
    float3 c = Source.SampleLevel(PointClamp, i.Uv, 0).rgb;
    float3 tinted = c * Tint;
    // lerp(c, c*Tint, Strength): Strength 0 → c (byte-identical to no feature), 1 → full tint.
    return float4(lerp(c, tinted, saturate(Strength)), 1.0);
}
