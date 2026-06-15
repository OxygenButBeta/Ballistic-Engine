// Depth-only shadow caster for the DX12 sun cascades. Transforms mesh positions by (model × light-space
// view-proj) into one cascade's depth map. No pixel output — the depth buffer IS the result. One draw
// per submesh per cascade (the same opaque geometry the camera draws, re-projected to light space).

cbuffer ShadowConstants : register(b0) {
    float4x4 LightMvp;   // model * cascade(view*proj), transposed on upload (DX ortho, z in [0,1])
};

float4 VSMain(float3 pos : POSITION) : SV_Position {
    return mul(float4(pos, 1.0), LightMvp);
}
// No PSMain: a null pixel shader (depth-only) is fine for opaque casters. Alpha-cutout casters would
// need a PS that samples diffuse + clips — a follow-up (foliage shadows).
