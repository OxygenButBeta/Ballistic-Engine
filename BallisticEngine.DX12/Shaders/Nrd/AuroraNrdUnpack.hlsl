// Unpacks NRD's denoised diffuse output (OUT_DIFF_RADIANCE_HITDIST) back into the linear irradiance E that
// Aurora's combine pass expects in `indirectFiltered`. NRD.hlsli is PREPENDED at compile time, so the unpack
// (YCoCg→linear) matches the front-end pack byte-for-byte.

Texture2D<float4>   NrdOut   : register(t0);   // OUT_DIFF_RADIANCE_HITDIST (R11G11B10F / RGBA16F)
Texture2D<float>    Depth    : register(t1);   // to mask sky
RWTexture2D<float4>  OutE    : register(u0);   // indirectFiltered: rgb = E, a = depth (combine reads rgb)

[numthreads(8, 8, 1)]
void CSMain(uint3 dtid : SV_DispatchThreadID) {
    uint w, h; OutE.GetDimensions(w, h);
    uint2 px = dtid.xy;
    if (px.x >= w || px.y >= h) return;

    float depth = Depth.Load(int3(px, 0));
    float4 packed = NrdOut.Load(int3(px, 0));
    float4 r = REBLUR_BackEnd_UnpackRadianceAndNormHitDist(packed);   // .rgb = denoised radiance (E), .w = normHitDist
    OutE[px] = float4(r.rgb, depth);
}
