// OIDN zero-copy GPU pack/unpack. Converts the half-res RGBA16F GI texture to/from a tightly-packed
// float4 buffer (16 bytes/element, row-major y*W+x) that OIDN's HIP device denoises IN PLACE on the GPU
// — no CPU readback. The conversion runs on the GPU (these compute shaders), so OIDN sees FLOAT data and
// its denoise quality matches the CPU-readback path exactly (a HALF-format OIDN denoise was visibly worse,
// washed-out in bright GI). OIDN reads the buffer as FLOAT3, pixelByteStride 16, rowByteStride W*16.
//
// Pack (texture -> buffer) and Unpack (buffer -> texture) use DISTINCT registers so both compile from one
// file; each has its own root signature.

cbuffer Dims : register(b0) { uint W; uint H; uint2 _pad; };

// --- Pack: RGBA16F texture (t0) -> float4 buffer (u0) ---
Texture2D<float4> SrcTex : register(t0);
RWStructuredBuffer<float4> DstBuf : register(u0);
[numthreads(8, 8, 1)]
void CSPack(uint3 id : SV_DispatchThreadID) {
    if (id.x >= W || id.y >= H) return;
    DstBuf[id.y * W + id.x] = float4(SrcTex[int2(id.xy)].rgb, 1.0);
}

// --- Unpack: float4 buffer (t1) -> RGBA16F texture (u1) ---
StructuredBuffer<float4> SrcBuf : register(t1);
RWTexture2D<float4> DstTex : register(u1);
[numthreads(8, 8, 1)]
void CSUnpack(uint3 id : SV_DispatchThreadID) {
    if (id.x >= W || id.y >= H) return;
    DstTex[id.xy] = float4(SrcBuf[id.y * W + id.x].rgb, 1.0);
}
