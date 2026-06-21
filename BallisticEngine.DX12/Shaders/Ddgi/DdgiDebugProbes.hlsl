// DDGI debug — draw every probe as a small world-space sphere tinted by its irradiance, so you can SEE the
// probe grid + the radiance each probe holds. One instanced draw: 6 verts (a camera-facing quad) per probe;
// the pixel shader shapes the quad into a lit sphere impostor and tints it with the probe's irradiance in the
// fragment's view-space normal direction. Depth-tested against the scene so probes behind geometry are hidden.
// Door: BALLISTIC_DX12_DDGI_DEBUG_PROBES=1 (additive-free OPAQUE overlay after combine).
//
// Bound: b0 constants | t0 Irradiance (StructuredBuffer) | (no textures).

cbuffer DdgiDebugConstants : register(b0) {
    float4x4 ViewProj;       // world → clip (transposed for row-vector mul)
    float3 GridOrigin;   float ProbeRadius;
    float3 ProbeSpacing; float Pad0;
    float3 CameraRight;  float Pad1;
    float3 CameraUp;     float Pad2;
    uint   CountX, CountY, CountZ;  uint Pad3;
};

StructuredBuffer<float4> Irradiance : register(t0);

static const int OctRes = 8;   // MUST match Dx12DdgiProbeGrid.OctRes + DdgiRelight.hlsl
static const int OctTexels = OctRes * OctRes;

float2 OctEncode(float3 n) {
    n /= (abs(n.x) + abs(n.y) + abs(n.z));
    float2 e = n.xy;
    if (n.z < 0.0) e = (1.0 - abs(e.yx)) * float2(e.x >= 0.0 ? 1.0 : -1.0, e.y >= 0.0 ? 1.0 : -1.0);
    return e * 0.5 + 0.5;
}

struct VSOut {
    float4 pos    : SV_Position;
    float2 quad   : TEXCOORD0;   // [-1,1]² impostor coord
    nointerpolation uint probe : TEXCOORD1;
};

VSOut VSMain(uint vid : SV_VertexID, uint inst : SV_InstanceID) {
    // 6 verts → two triangles of a [-1,1] quad.
    float2 corners[6] = {
        float2(-1,-1), float2(1,-1), float2(-1,1),
        float2(-1,1),  float2(1,-1), float2(1,1)
    };
    float2 q = corners[vid];

    uint ix = inst % CountX;
    uint iy = (inst / CountX) % CountY;
    uint iz = inst / (CountX * CountY);
    float3 probePos = GridOrigin + float3(ix, iy, iz) * ProbeSpacing;

    // Camera-facing billboard at the probe, sized by ProbeRadius.
    float3 world = probePos + (CameraRight * q.x + CameraUp * q.y) * ProbeRadius;

    VSOut o;
    o.pos = mul(float4(world, 1.0), ViewProj);
    o.quad = q;
    o.probe = inst;
    return o;
}

float4 PSMain(VSOut i) : SV_Target {
    float r2 = dot(i.quad, i.quad);
    if (r2 > 1.0) discard;                         // round the quad into a disc/sphere
    float z = sqrt(1.0 - r2);                      // impostor sphere normal.z (camera space)
    // Sphere surface normal in camera space → tint by the irradiance facing that way (use the quad-space normal
    // as a cheap stand-in: front-facing texel ≈ camera direction). Average a couple of oct texels for a stable hue.
    float3 nCam = float3(i.quad, z);
    int2 ot = clamp((int2)floor(OctEncode(normalize(nCam)) * float(OctRes)), 0, OctRes - 1);
    float3 E = Irradiance[i.probe * OctTexels + ot.y * OctRes + ot.x].rgb;

    // Simple lambert shade off the camera so the sphere reads as 3D, then show the irradiance as its albedo.
    float shade = saturate(z * 0.7 + 0.3);
    return float4(E * shade, 1.0);
}
