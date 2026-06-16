// OIDN zero-copy GPU pack/unpack. Converts the half-res RGBA16F GI texture to/from a tightly-packed
// float4 buffer (16 bytes/element, row-major y*W+x) that OIDN's HIP device denoises IN PLACE on the GPU
// — no CPU readback. The conversion runs on the GPU (these compute shaders), so OIDN sees FLOAT data and
// its denoise quality matches the CPU-readback path exactly (a HALF-format OIDN denoise was visibly worse,
// washed-out in bright GI). OIDN reads the buffer as FLOAT3, pixelByteStride 16, rowByteStride W*16.
//
// Pack (texture -> buffer) and Unpack (buffer -> texture) use DISTINCT registers so both compile from one
// file; each has its own root signature.
//
// P6.1 GUIDED denoise: CSPackAux additionally packs the G-buffer ALBEDO + NORMAL into two more half-res float
// buffers so OIDN can use them as AOV guide images (edge-preserving — it won't blur across an albedo/normal
// discontinuity). The GI denoise runs HALF-res, the G-buffer is FULL-res, so the aux pack point-samples the
// G-buffer at the half-res pixel's matching full-res texel (Scale = full/half ≈ 2). Albedo = RT0.rgb (the
// surface base color); normal = RT1 decoded from [0,1] to [-1,1] (OIDN wants signed world/view normals).

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

// --- P6.1 PackAux: full-res G-buffer albedo (t2) + normal (t3) -> half-res albedo buf (u2) + normal buf (u3) ---
// Scale.x = fullW/halfW, Scale.y = fullH/halfH (≈2). Point-sample at the half-res pixel centre mapped to
// full-res. Normal RT1 is [0,1]-packed world normal → decode to [-1,1]. Albedo RT0.rgb is the base color.
cbuffer AuxDims : register(b1) { uint AW; uint AH; float SX; float SY; };
Texture2D<float4> GAlbedo : register(t2);
Texture2D<float4> GNormal : register(t3);
RWStructuredBuffer<float4> AlbedoBuf : register(u2);
RWStructuredBuffer<float4> NormalBuf : register(u3);
[numthreads(8, 8, 1)]
void CSPackAux(uint3 id : SV_DispatchThreadID) {
    if (id.x >= AW || id.y >= AH) return;
    int2 full = int2((float2(id.xy) + 0.5) * float2(SX, SY));
    float3 alb = GAlbedo[full].rgb;
    float3 nrm = GNormal[full].rgb * 2.0 - 1.0;   // [0,1] -> [-1,1]; (0,0,0)-packed sky stays ~(-1,-1,-1), fine for OIDN
    uint idx = id.y * AW + id.x;
    AlbedoBuf[idx] = float4(alb, 1.0);
    NormalBuf[idx] = float4(nrm, 1.0);
}
