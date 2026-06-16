// Bindless self-test (BALLISTIC_DX12_BINDLESS_TEST=1): proves SM6.6 DYNAMIC RESOURCES
// (ResourceDescriptorHeap[idx]) + the ...HeapDirectlyIndexed root-sig flag — the second novel API the
// GPU-driven path needs so ONE ExecuteIndirect can draw submeshes with DIFFERENT materials (textures
// fetched by a per-material bindless index, no per-draw descriptor-table rebinding).
//
// Reads texel (0,0) of the texture at bindless index TexIndex and writes its RGBA*255 to a UAV.

cbuffer Params : register(b0) { uint TexIndex; uint3 _pad; };
RWStructuredBuffer<uint> Out : register(u0);

[numthreads(1, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    Texture2D<float4> tex = ResourceDescriptorHeap[TexIndex];   // SM6.6 dynamic resource
    float4 c = tex.Load(int3(0, 0, 0));
    Out[0] = (uint)round(c.r * 255.0);
    Out[1] = (uint)round(c.g * 255.0);
    Out[2] = (uint)round(c.b * 255.0);
    Out[3] = (uint)round(c.a * 255.0);
}
