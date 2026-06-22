// Lumen FAZ 3b — world-space CARD OBB ray-test DEBUG view (separate file from any composite so this TU has a single
// b0/t0 binding — DXC errors on two cbuffers sharing register(b0) in one file).
//
// A fullscreen pass that reconstructs each pixel's world-space view ray (from InvViewProj, exactly like
// GlobalSdfDebug) and intersects every placed card's oriented bounding box (OBB slab test in CARD-LOCAL space). The
// nearest hit is shaded by the card's direction color (derived from its AxisZ outward normal) so the card PLACEMENT
// + ORIENTATION is VISIBLE — the Cornell box appears as distinct-direction-colored interior walls/floor/ceiling
// (-X red, +X cyan, -Y green, +Y magenta, -Z blue, +Z yellow). Opaque replace into the HDR scene color. Gated by
// BALLISTIC_DX12_LUMEN_CARDS_DEBUG on the CPU side (default off).
//
// Cards are treated as one-sided interior surfaces: a card is hit only when the ray reaches its inner face
// (dot(rd, AxisZ) > 0), which culls the front-most occluder card facing the camera so the open box reveals all its
// interior walls instead of one near card painting the screen.
//
// NaN-safe: every divide guards its denominator (matches the GlobalSdfDebug slab test). Driver note: the color math
// is kept branch/int/lerp-free + saturate-clamped because this driver mis-compiled those forms (fed loop-carried
// data) to all-white; only plain abs/max/multiply/add + saturate of the hit normal is robust here.

cbuffer CardDebugConstants : register(b0) {
    float4x4 InvViewProj;    // clip → world (transposed on upload)
    float3   CamPos;         uint   CardCount;
    float    MaxTraceDist;   float3 CardDbgPad;
};

struct GpuLumenCard {        // 64 B, matches the C# struct
    float3 Origin; uint  PageId;
    float3 AxisX;  float ExtentX;
    float3 AxisY;  float ExtentY;
    float3 AxisZ;  float ExtentZ;
};
StructuredBuffer<GpuLumenCard> Cards : register(t0);

struct VSOut { float4 Position : SV_Position; float2 Uv : TEXCOORD0; };

VSOut VSDebug(uint vid : SV_VertexID) {
    VSOut o;
    float2 uv = float2((vid << 1) & 2, vid & 2);
    o.Position = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
    o.Uv = uv;
    return o;
}

// The 6 direction hues (-X red, +X cyan, -Y green, +Y magenta, -Z blue, +Z yellow) are computed inline in PSDebug
// with PURE float arithmetic from the hit card's outward normal — an int direction index carried out of the trace
// [loop] and compared mis-compiled to all-white on this driver, so the whole path is comparison/int-free.

// Safe reciprocal (floor |t| to 1e-8 keeping sign) so a ray parallel to a slab can't divide by zero.
float SafeInv(float v) { return (abs(v) < 1e-8) ? (v < 0 ? -1e8 : 1e8) : (1.0 / v); }

// Ray/OBB intersection in CARD-LOCAL space: project the ray origin+dir onto the card's orthonormal axes, then run a
// standard slab test against [-Extent, +Extent] on each axis. Returns the near hit t (>0) or -1 on miss.
float IntersectCardObb(GpuLumenCard c, float3 ro, float3 rd, out float tHit) {
    tHit = -1.0;
    float3 d = ro - c.Origin;
    // Local-space origin + direction (axes are unit + orthonormal as placed; dot-projects world → card frame).
    float3 lo = float3(dot(d, c.AxisX), dot(d, c.AxisY), dot(d, c.AxisZ));
    float3 ld = float3(dot(rd, c.AxisX), dot(rd, c.AxisY), dot(rd, c.AxisZ));
    float3 ext = float3(c.ExtentX, c.ExtentY, c.ExtentZ);

    float3 inv = float3(SafeInv(ld.x), SafeInv(ld.y), SafeInv(ld.z));
    float3 t0 = (-ext - lo) * inv;
    float3 t1 = ( ext - lo) * inv;
    float3 tmin = min(t0, t1), tmax = max(t0, t1);
    float tNear = max(max(tmin.x, tmin.y), tmin.z);
    float tFar  = min(min(tmax.x, tmax.y), tmax.z);
    if (tFar < max(tNear, 0.0)) return -1.0;   // miss
    float t = (tNear >= 0.0) ? tNear : tFar;   // enter face, or exit face if camera is inside the OBB
    if (t < 0.0) return -1.0;
    tHit = t;
    return t;
}

// Color a card hit by its outward normal (6 distinct direction hues) — factored into its OWN function so the trace
// [loop] in PSDebug and this color math don't get inlined together (that combination mis-compiled to all-white on
// this driver; a function boundary keeps the optimizer honest). Output kept low (~0.03) for the HDR post chain.
float3 ShadeCardHit(float3 n, float hitAny) {
    float xp = max( n.x, 0.0), xn = max(-n.x, 0.0);
    float yp = max( n.y, 0.0), yn = max(-n.y, 0.0);
    float zp = max( n.z, 0.0), zn = max(-n.z, 0.0);
    float3 hue =
        xn * float3(1.0, 0.15, 0.15) +    // -X red
        xp * float3(0.15, 0.9, 0.9) +     // +X cyan
        yn * float3(0.2, 0.9, 0.2) +      // -Y green
        yp * float3(0.95, 0.2, 0.9) +     // +Y magenta
        zn * float3(0.2, 0.35, 1.0) +     // -Z blue
        zp * float3(0.95, 0.9, 0.2);      // +Z yellow
    float3 bg = float3(0.02, 0.02, 0.03);
    return saturate(hue) * 0.03 * hitAny + bg * (1.0 - hitAny);
}

float4 PSDebug(VSOut i) : SV_Target {
    // Reconstruct the world-space view ray for this pixel from the clip-space corners (copy of GlobalSdfDebug).
    float2 ndc = i.Uv * 2.0 - 1.0;
    ndc.y = -ndc.y;
    float4 nearH = mul(float4(ndc, 0.0, 1.0), InvViewProj);
    float4 farH  = mul(float4(ndc, 1.0, 1.0), InvViewProj);
    float3 nearW = nearH.xyz / max(nearH.w, 1e-6);
    float3 farW  = farH.xyz  / max(farH.w, 1e-6);
    float3 ro = CamPos;
    float3 rd = normalize(farW - nearW);

    // Nearest card OBB hit across all placed cards. Carry the hit card's outward NORMAL (AxisZ, a float3) — NOT an
    // int direction index — across the loop: an `int` carried out of the [loop] and then compared (`bestDir == k`)
    // mis-compiled to all-white on this driver (the float3 arithmetic path is robust).
    float  bestT = MaxTraceDist;
    float3 bestN = float3(0, 0, 0);
    float  hitAny = 0.0;
    [loop]
    for (uint c = 0; c < CardCount; ++c) {
        GpuLumenCard card = Cards[c];
        if (card.PageId == 0xFFFFFFFFu) continue;   // unallocated/dropped card → not in the surface cache, skip
        // Show cards as the INTERIOR surfaces they represent: keep only cards whose outward normal points AWAY from
        // the ray (the ray hits the card's BACK / inner face, dot(rd, AxisZ) > 0). This culls the front-most occluder
        // card facing the camera so the ray passes through the open front and reveals all the interior walls (back/
        // sides/floor/ceiling) in their distinct direction colors instead of a single near card painting the screen.
        if (dot(rd, card.AxisZ) <= 0.0) continue;
        float tHit;
        float t = IntersectCardObb(card, ro, rd, tHit);
        if (t > 0.0 && t < bestT) {
            bestT  = t;
            bestN  = card.AxisZ;
            hitAny = 1.0;
        }
    }

    // SHADE — one return, via the factored ShadeCardHit (the trace loop + color math mis-compile when inlined together
    // on this driver; the function boundary fixes it). 6 distinct direction hues, background dark slate by hitAny.
    return float4(ShadeCardHit(bestN, hitAny), 1.0);
}
