// Lumen FAZ 2 — Global Distance Field sphere-trace DEBUG view (separate file from the composite so each TU has a
// single b0/t0/s0 binding — DXC errors on two cbuffers sharing register(b0) in one file).
//
// A fullscreen pass that sphere-marches the clipmap from the camera per screen pixel and shades the hit (SDF-
// gradient normal lit by a fixed key light), so the field's correctness is VISIBLE. Opaque replace into the HDR
// scene color. Gated by BALLISTIC_DX12_GLOBALSDF_DEBUG on the CPU side (default off → not recorded).
//
// NaN-safe: every divide guards its denominator.

cbuffer DebugConstants : register(b0) {
    float4x4 InvViewProj;    // clip → world (transposed on upload)
    float3   CamPos;         float  DbgVoxelSize;
    float3   DbgClipOrigin;  float  DbgClipHalfExtent;
    uint3    DbgClipRes;     float  MaxTraceDist;
    float3   KeyLightDir;    float  HitEpsilon;
};

Texture3D<float> DbgClipmap : register(t0);
SamplerState     DbgLinear  : register(s0);

struct VSOut { float4 Position : SV_Position; float2 Uv : TEXCOORD0; };

VSOut VSDebug(uint vid : SV_VertexID) {
    VSOut o;
    float2 uv = float2((vid << 1) & 2, vid & 2);
    o.Position = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
    o.Uv = uv;
    return o;
}

// World-space bounds of the clipmap volume.
float3 ClipMin() { return DbgClipOrigin; }
float3 ClipMax() { return DbgClipOrigin + float3(DbgClipRes) * DbgVoxelSize; }

// Sample the clipmap distance at a world point (clamped into the volume). Outside the volume → the half-extent
// (treated as far/empty), so the march steps quickly toward the volume.
float SampleClip(float3 worldP) {
    float3 lo = ClipMin(), hi = ClipMax();
    float3 ext = max(hi - lo, float3(1e-4, 1e-4, 1e-4));
    float3 uvw = (worldP - lo) / ext;
    if (any(uvw < 0.0) || any(uvw > 1.0))
        return DbgClipHalfExtent;
    return DbgClipmap.SampleLevel(DbgLinear, saturate(uvw), 0);
}

// Central-difference SDF gradient → surface normal.
float3 ClipNormal(float3 p) {
    float h = DbgVoxelSize;
    float dx = SampleClip(p + float3(h, 0, 0)) - SampleClip(p - float3(h, 0, 0));
    float dy = SampleClip(p + float3(0, h, 0)) - SampleClip(p - float3(0, h, 0));
    float dz = SampleClip(p + float3(0, 0, h)) - SampleClip(p - float3(0, 0, h));
    float3 g = float3(dx, dy, dz);
    float len = length(g);
    return (len > 1e-6) ? g / len : float3(0, 1, 0);
}

// Ray/AABB slab intersection — returns the near/far t along the ray inside the clipmap volume (tNear<tFar = hit).
bool IntersectClipBox(float3 ro, float3 rd, out float tNear, out float tFar) {
    float3 lo = ClipMin(), hi = ClipMax();
    // Safe reciprocal: floor |component| to 1e-8 (keeping its sign) so a ray axis-parallel to a slab can't divide
    // by zero. A huge t on that axis is then correctly excluded/included by the min/max below.
    float3 safeRd = float3(
        abs(rd.x) < 1e-8 ? (rd.x < 0 ? -1e-8 : 1e-8) : rd.x,
        abs(rd.y) < 1e-8 ? (rd.y < 0 ? -1e-8 : 1e-8) : rd.y,
        abs(rd.z) < 1e-8 ? (rd.z < 0 ? -1e-8 : 1e-8) : rd.z);
    float3 inv = 1.0 / safeRd;
    float3 t0 = (lo - ro) * inv;
    float3 t1 = (hi - ro) * inv;
    float3 tmin = min(t0, t1), tmax = max(t0, t1);
    tNear = max(max(tmin.x, tmin.y), tmin.z);
    tFar  = min(min(tmax.x, tmax.y), tmax.z);
    return tFar >= max(tNear, 0.0);
}

float4 PSDebug(VSOut i) : SV_Target {
    // Reconstruct the world-space view ray for this pixel from the clip-space corners.
    float2 ndc = i.Uv * 2.0 - 1.0;
    ndc.y = -ndc.y;
    float4 nearH = mul(float4(ndc, 0.0, 1.0), InvViewProj);
    float4 farH  = mul(float4(ndc, 1.0, 1.0), InvViewProj);
    float3 nearW = nearH.xyz / max(nearH.w, 1e-6);
    float3 farW  = farH.xyz  / max(farH.w, 1e-6);
    float3 ro = CamPos;
    float3 rd = normalize(farW - nearW);

    // Enter the clipmap volume.
    float tNear, tFar;
    if (!IntersectClipBox(ro, rd, tNear, tFar))
        return float4(0.02, 0.02, 0.03, 1.0);   // miss the volume entirely → dark slate background

    float t = max(tNear, 0.0) + DbgVoxelSize * 0.5;
    float tEnd = min(tFar, MaxTraceDist);
    float eps = HitEpsilon;

    bool hit = false;
    float3 p = ro + rd * t;
    [loop]
    for (int s = 0; s < 256; ++s) {
        if (t > tEnd) break;
        p = ro + rd * t;
        float d = SampleClip(p);
        if (d < eps) { hit = true; break; }
        // Conservative step: advance by the distance (sphere tracing). Floor the step at a fraction of a voxel so
        // a near-zero/negative field can't stall the march.
        t += max(d, DbgVoxelSize * 0.25);
    }

    if (!hit)
        return float4(0.02, 0.02, 0.03, 1.0);

    // Shade the hit: SDF-gradient normal lit by a fixed key light + ambient, plus a subtle depth tint so the
    // silhouette and the surface curvature both read clearly.
    float3 n = ClipNormal(p);
    float ndl = saturate(dot(n, -normalize(KeyLightDir)));
    float3 base = float3(0.7, 0.72, 0.78);
    float depth01 = saturate((t - tNear) / max(tFar - tNear, 1e-4));
    float3 tint = lerp(float3(1.0, 0.95, 0.85), float3(0.6, 0.7, 1.0), depth01);
    float3 col = base * tint * (0.15 + 0.85 * ndl);
    return float4(col, 1.0);
}
