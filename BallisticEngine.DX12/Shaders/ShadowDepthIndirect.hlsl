// GPU-driven depth-only shadow caster (ExecuteIndirect). Same output as ShadowDepth.hlsl — transforms mesh
// positions by the per-draw LightMvp into one cascade's depth layer — but LightMvp comes from
// PerDraws[DrawIndex] (DrawIndex = an ExecuteIndirect root constant) so one ExecuteIndirect draws all of a
// cascade's visible submeshes. No pixel shader (depth IS the result).

cbuffer DrawIndexCB : register(b0) { uint DrawIndex; uint3 _pad; }   // set per indirect command

struct ShadowPerDraw { float4x4 LightMvp; };
StructuredBuffer<ShadowPerDraw> PerDraws : register(t0);

float4 VSMain(float3 pos : POSITION) : SV_Position {
    return mul(float4(pos, 1.0), PerDraws[DrawIndex].LightMvp);
}
