// R3b — COMPUTE SKINNING. Skins a skinned mesh's position/normal/tangent by its per-bone matrices into a
// transient output buffer, so the result can then be drawn through the SAME non-skinned GPU-driven
// ExecuteIndirect + GBufferBindless path as static geometry (no per-skinned-draw VS skinning, no skinned PSO).
//
// BYTE-IDENTICAL to GBufferSkinned.hlsl's vertex skin stage: the SkinMatrix blend + mul(v, skin) below are a
// verbatim copy of GBufferSkinned::SkinMatrix / VSMain lines 87-90. Only the MESH-LOCAL skin is done here; the
// model/Mvp transform stays in GBufferBindless (byte-identical to the static path). So a skinned surface skinned
// on the compute queue + drawn bindless matches the VS-skinned path to the bit — provided the float math order
// is preserved exactly (it is: same weighted sum, same mul order, same row-vector convention).
//
// Bone matrices: the renderer transposes each on upload (row-vector mul), same buffer the skinned VS used (t0
// here). Output layout MATCHES the source vertex streams so RenderInto binds the skinned buffers as drop-in
// replacements: Pos = float3, Normal = float3, Tangent = float4 (w preserved). UV/index are unchanged (the
// static path reads the ORIGINAL uv/index buffers).

cbuffer SkinParams : register(b0) {
    uint VertexCount;     // vertices in this skinned mesh
    uint3 _pad0;
};

StructuredBuffer<float4x4> BoneMatrices : register(t0);   // per-bone (transposed on upload), == skinned VS t6
StructuredBuffer<float3>   InPos         : register(t1);
StructuredBuffer<float3>   InNormal      : register(t2);
StructuredBuffer<float4>   InTangent     : register(t3);
StructuredBuffer<float4>   InBoneIndices : register(t4);   // float4 (rounded to int, matching the VS convention)
StructuredBuffer<float4>   InBoneWeights : register(t5);

RWStructuredBuffer<float3> OutPos     : register(u0);
RWStructuredBuffer<float3> OutNormal  : register(u1);
RWStructuredBuffer<float4> OutTangent : register(u2);

// VERBATIM from GBufferSkinned::SkinMatrix — do not "optimize" the order; it changes the FP result.
float4x4 SkinMatrix(float4 indices, float4 weights) {
    int4 bi = (int4)(indices + 0.5);
    return weights.x * BoneMatrices[bi.x]
         + weights.y * BoneMatrices[bi.y]
         + weights.z * BoneMatrices[bi.z]
         + weights.w * BoneMatrices[bi.w];
}

[numthreads(64, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    uint i = id.x;
    if (i >= VertexCount) return;

    float4x4 skin = SkinMatrix(InBoneIndices[i], InBoneWeights[i]);
    // mul order + .xyz exactly as GBufferSkinned::VSMain lines 88-90. Tangent w is the handedness sign, preserved.
    OutPos[i]     = mul(float4(InPos[i], 1.0), skin).xyz;
    OutNormal[i]  = mul(float4(InNormal[i], 0.0), skin).xyz;
    float3 st     = mul(float4(InTangent[i].xyz, 0.0), skin).xyz;
    OutTangent[i] = float4(st, InTangent[i].w);
}
