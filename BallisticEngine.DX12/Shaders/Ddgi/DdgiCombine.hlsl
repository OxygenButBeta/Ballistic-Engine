// DDGI — combine (Pass 3). Fullscreen pass that adds the diffuse indirect into the HDR scene color:
//   color += E * albedo * ao / PI
// E is the indirect irradiance from DdgiSample, albedo the G-buffer base color, ao the GTAO term. The deferred
// pass already suppressed its IBL diffuse ambient (ctx.GiActiveThisFrame) so this is the ONLY diffuse indirect
// → no double count. Additive One/One blend (PSO set in C#). PSDebug shows raw E (OPAQUE replace) for A/B.
//
// Bound: b0 constants | t0 Indirect (E) | t1 albedo (G0) | t2 AO | s0 clamp.

cbuffer DdgiCombineConstants : register(b0) {
    float AoStrength; float Intensity; float Pad0; float Pad1;
};

Texture2D Indirect : register(t0);
Texture2D Albedo   : register(t1);
Texture2D Ao       : register(t2);
SamplerState LinearClamp : register(s0);

static const float PI = 3.14159265359;

struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };

VSOut VSCombine(uint vid : SV_VertexID) {
    VSOut o;
    o.uv = float2((vid << 1) & 2, vid & 2);
    o.pos = float4(o.uv * float2(2, -2) + float2(-1, 1), 0, 1);
    return o;
}

float3 SampleE(float2 uv) { return Indirect.SampleLevel(LinearClamp, uv, 0).rgb; }

float4 PSCombine(VSOut i) : SV_Target {
    float3 E = SampleE(i.uv);
    float3 albedo = Albedo.SampleLevel(LinearClamp, i.uv, 0).rgb;
    float ao = lerp(1.0, Ao.SampleLevel(LinearClamp, i.uv, 0).r, saturate(AoStrength));
    float3 diffuse = E * albedo * ao * (1.0 / PI) * Intensity;
    return float4(diffuse, 1.0);   // One/One additive
}

float4 PSDebugE(VSOut i) : SV_Target {
    return float4(SampleE(i.uv) * Intensity, 1.0);   // OPAQUE replace — raw E
}
