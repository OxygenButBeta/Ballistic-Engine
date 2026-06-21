// DDGI — combine (Pass 3). Fullscreen pass that adds the FINISHED diffuse indirect into the HDR scene color:
//   color += Indirect          (Indirect already = E * albedo / π, computed in the sample pass)
// The receiver Lambert BRDF (albedo/π) and the gather are BOTH done in DdgiSample (a compute pass that reads the
// G-buffer cleanly as a NonPixel SRV). The combine PS therefore binds ONLY `Indirect` (a single committed/transient
// color) and NEVER the G-buffer — this fixed the dead GI: the old combine PS bound the G-buffer albedo while its
// real layout was RENDER_TARGET (tracker desync), read 0, and E*albedo=0 → DDGI added nothing. The deferred pass
// already suppressed its IBL diffuse ambient (ctx.GiActiveThisFrame) so this is the only diffuse indirect → no
// double count. Additive One/One blend (PSO in C#). PSDebug shows the raw indirect (OPAQUE replace) for A/B.
//
// Bound: b0 constants | t0 Indirect | s0 clamp.

cbuffer DdgiCombineConstants : register(b0) {
    float AoStrength; float Intensity; float Pad0; float Pad1;
};

Texture2D Indirect : register(t0);
SamplerState LinearClamp : register(s0);

struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };

VSOut VSCombine(uint vid : SV_VertexID) {
    VSOut o;
    o.uv = float2((vid << 1) & 2, vid & 2);
    o.pos = float4(o.uv * float2(2, -2) + float2(-1, 1), 0, 1);
    return o;
}

float3 SampleE(float2 uv) { return Indirect.SampleLevel(LinearClamp, uv, 0).rgb; }

float4 PSCombine(VSOut i) : SV_Target {
    return float4(SampleE(i.uv) * Intensity, 1.0);   // One/One additive (Indirect already E*albedo/π)
}

float4 PSDebugE(VSOut i) : SV_Target {
    return float4(SampleE(i.uv) * Intensity, 1.0);   // OPAQUE replace — same finished indirect
}
