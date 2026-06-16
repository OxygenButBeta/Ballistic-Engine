// P7.2a debug view: blit the probe G-buffer CUBE (albedo or normal) to the screen so we can SEE the rasterized
// probe is correct geometry (not garbage) before wiring relight/resolve. Compute, 1 thread/pixel: map the screen
// pixel to a direction (an equirect/lat-long unwrap of the cube) and sample the cube → ssgiTarget (so the shared
// SsgiResolveAndCombine + GI-isolate path displays it). MEASUREMENT/DEBUG ONLY (BALLISTIC_DX12_NORT_PROBES_DEBUG=1).

cbuffer DebugConstants : register(b0) {
    float4 Params;   // x = screenW, y = screenH, z = mode (0 albedo, 1 normal), w = exposure scale
};

TextureCube ProbeCube : register(t0);
SamplerState LinearClamp : register(s0);
RWTexture2D<float4> Output : register(u0);

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    uint w = (uint)Params.x, h = (uint)Params.y;
    if (id.x >= w || id.y >= h) return;

    // Equirect unwrap: screen UV -> spherical direction. Lets a single screen show the WHOLE probe cube.
    float2 uv = (float2(id.xy) + 0.5) / float2(w, h);
    float lon = (uv.x * 2.0 - 1.0) * 3.14159265;      // -pi..pi
    float lat = (0.5 - uv.y) * 3.14159265;            // +pi/2 (top) .. -pi/2 (bottom)
    float cosLat = cos(lat);
    float3 dir = float3(sin(lon) * cosLat, sin(lat), cos(lon) * cosLat);

    float4 c = ProbeCube.SampleLevel(LinearClamp, dir, 0);
    float3 rgb = c.rgb;
    if (Params.z > 0.5) rgb = c.rgb;                  // normal cube is already [0,1]-packed
    rgb *= max(Params.w, 0.0);                        // pre-exposure scale (so the GI-isolate path shows it)

    Output[id.xy] = float4(rgb, 1.0);
}
