// Dear ImGui DX12 backend shader (editor UI). Mirrors EditorImGui_Vert/Frag.glsl: an ortho-projected
// textured+vertex-colored quad stream. The ortho matrix is uploaded TRANSPOSED (the codebase convention),
// so mul(M, v) is correct. Output goes straight to the R8G8B8A8_UNORM swapchain backbuffer (no sRGB
// encode), matching the GL backend which draws into a plain non-sRGB default framebuffer.

cbuffer Ortho : register(b0) {
    float4x4 ProjectionMatrix;
};

Texture2D    tex0  : register(t0);
SamplerState samp0 : register(s0);

struct VSInput {
    float2 pos : POSITION;   // screen pixels (ImDrawVert.pos)
    float2 uv  : TEXCOORD;   // ImDrawVert.uv
    float4 col : COLOR;      // ImDrawVert.col (R8G8B8A8_UNORM -> [0,1] float4, straight RGBA)
};

struct PSInput {
    float4 pos : SV_POSITION;
    float4 col : COLOR;
    float2 uv  : TEXCOORD;
};

PSInput VSMain(VSInput i) {
    PSInput o;
    // Codebase convention: matrices are uploaded TRANSPOSED and multiplied vector-first (mul(v, M)).
    o.pos = mul(float4(i.pos, 0.0, 1.0), ProjectionMatrix);
    o.col = i.col;
    o.uv  = i.uv;
    return o;
}

float4 PSMain(PSInput i) : SV_Target {
    return i.col * tex0.Sample(samp0, i.uv);
}
