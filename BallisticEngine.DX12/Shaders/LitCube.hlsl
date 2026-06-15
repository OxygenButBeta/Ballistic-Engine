// Phase 2 lit-mesh smoke: a real vertex-buffer mesh through an MVP constant buffer with simple
// directional N·L lighting + ambient + a Reinhard tonemap. Proves mesh upload + CBV + depth + the
// minimal lit path (the seed the full PBR HLSL port grows from). Lighting math is intentionally the
// same SHAPE as the GL material (N·L * lightColor + ambient, tonemap last) so parity is meaningful.

cbuffer Constants : register(b0) {
    float4x4 MVP;        // model * view * projection
    float4x4 Model;      // for world-space normals
    float3   LightDir;   // toward the light (world)
    float    _pad0;
    float3   LightColor;
    float    _pad1;
    float3   Ambient;
    float    _pad2;
};

struct VSIn {
    float3 pos    : POSITION;
    float3 normal : NORMAL;
    float3 color  : COLOR;
};

struct VSOut {
    float4 pos    : SV_Position;
    float3 normal : NORMAL;
    float3 color  : COLOR;
};

VSOut VSMain(VSIn i) {
    VSOut o;
    o.pos = mul(float4(i.pos, 1.0), MVP);
    o.normal = normalize(mul(float4(i.normal, 0.0), Model).xyz);
    o.color = i.color;
    return o;
}

float3 Tonemap(float3 c) {
    return c / (c + 1.0.xxx); // Reinhard — placeholder until the real ACES tonemap is ported
}

float4 PSMain(VSOut i) : SV_Target {
    float ndl = saturate(dot(normalize(i.normal), normalize(LightDir)));
    float3 lit = i.color * (Ambient + LightColor * ndl);
    return float4(Tonemap(lit), 1.0);
}
