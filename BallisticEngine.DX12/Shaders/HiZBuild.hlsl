// Hi-Z (hierarchical depth) pyramid build for the DX12 GPU-driven occlusion cull. A full mip chain of an
// R32_Float texture where each coarser texel = the MAX (farthest) window-depth of its 2x2 footprint — the
// conservative reduction, so the cull can only ever OVER-state how far the nearest occluder is and can
// never false-cull. Built from the PREVIOUS frame's G-buffer depth (the cull runs before this frame's
// depth exists); the camera-delta gate disables Hi-Z for a frame after a big jump (stale-depth safety).
//
// Compute build: CSCopy writes mip0 from the depth SRV; CSDownsample MAX-reduces mip (SrcMip) -> the next
// (DstMip) reading/writing UAVs (a UAV barrier orders the passes — avoids the SRV/UAV same-resource hazard).

// ---- mip 0: copy the scene depth's R channel into the pyramid ----
Texture2D<float>   SourceDepth : register(t0);
RWTexture2D<float> Mip0        : register(u0);

[numthreads(8, 8, 1)]
void CSCopy(uint3 id : SV_DispatchThreadID) {
    uint w, h; Mip0.GetDimensions(w, h);
    if (id.x >= w || id.y >= h) return;
    Mip0[id.xy] = SourceDepth.Load(int3(id.xy, 0));
}

// ---- mips 1..N: MAX downsample of the level just written ----
cbuffer DownParams : register(b0) { uint SrcW, SrcH, DstW, DstH; }
RWTexture2D<float> SrcMip : register(u0);   // mip k-1 (read)
RWTexture2D<float> DstMip : register(u1);   // mip k   (write)

[numthreads(8, 8, 1)]
void CSDownsample(uint3 id : SV_DispatchThreadID) {
    if (id.x >= DstW || id.y >= DstH) return;
    int2 src = int2(id.xy) * 2;
    int2 hi = int2(SrcW - 1, SrcH - 1);
    float d0 = SrcMip[min(src + int2(0, 0), hi)];
    float d1 = SrcMip[min(src + int2(1, 0), hi)];
    float d2 = SrcMip[min(src + int2(0, 1), hi)];
    float d3 = SrcMip[min(src + int2(1, 1), hi)];
    float m = max(max(d0, d1), max(d2, d3));
    // Odd source dim: fold in the dropped edge texel so no farther depth is lost from the MAX.
    if ((SrcW & 1) != 0 && src.x + 2 < (int)SrcW) {
        m = max(m, SrcMip[min(src + int2(2, 0), hi)]);
        m = max(m, SrcMip[min(src + int2(2, 1), hi)]);
    }
    if ((SrcH & 1) != 0 && src.y + 2 < (int)SrcH) {
        m = max(m, SrcMip[min(src + int2(0, 2), hi)]);
        m = max(m, SrcMip[min(src + int2(1, 2), hi)]);
    }
    DstMip[id.xy] = m;
}
