// GpuSceneQuery — inline RayQuery (DXR Tier 1.1) spatial queries over the scene TLAS. No SBT / RT PSO /
// hit shaders: plain compute, one thread per query point, the TLAS bound as a descriptor-table SRV.
// All sampling is DETERMINISTIC (fixed axes + a closed-form Fibonacci sphere — no RNG, no frame index),
// so two runs are byte-identical (the engine's verify harness is byte-identical-based).
//
// Bindings (shared root sig): TLAS t0 (table), points t1 + pairs t2 (root SRVs), result u0 (root UAV),
// QueryConstants b0 (CBV). Entry points: Occupancy / Visibility / Classify.

RaytracingAccelerationStructure Scene : register(t0);

cbuffer QueryConstants : register(b0) {
    uint  Count;          // number of query elements (points or pairs)
    float ProbeRadius;    // classify/occupancy max ray distance (world units)
    float RayBias;        // origin offset to avoid self-intersection at a surface
    uint  _pad0;
};

// SpaceClass result codes (must match GpuSceneQuery.SpaceClass in C#).
static const uint CLASS_OPEN     = 0;
static const uint CLASS_ENCLOSED = 1;
static const uint CLASS_SOLID    = 2;

// --- Occupancy: ray-parity along a fixed axis. Count triangle crossings to a far plane; an odd count means
// the origin is inside a closed surface. Robust to non-watertight meshes via a 3-axis vote (one thread does
// all 3 axes and votes). Counts ALL crossings via non-committed traversal (the RayQuery.Proceed loop below).
uint CountCrossings(float3 origin, float3 dir, float maxT) {
    RayDesc ray;
    ray.Origin = origin;
    ray.Direction = dir;
    ray.TMin = 0.0;
    ray.TMax = maxT;

    // We need EVERY crossing, not the first hit. With inline RayQuery, Proceed() only surfaces candidates
    // that need shader evaluation — so triangles must be treated as NON-opaque (don't commit them, so
    // traversal keeps walking). The scene BLAS is flagged Opaque (for shadow/GI perf), so we override it
    // per-ray with RAY_FLAG_FORCE_NON_OPAQUE: every triangle then surfaces as CANDIDATE_NON_OPAQUE_TRIANGLE.
    // Count each, never CommitNonOpaqueTriangleHit, and Proceed() walks the whole ray. Parity (odd = inside)
    // is the ray-stabbing solid test — no separate query AS needed.
    RayQuery<RAY_FLAG_FORCE_NON_OPAQUE> q;
    q.TraceRayInline(Scene, RAY_FLAG_NONE, 0xFF, ray);
    uint crossings = 0;
    [loop] while (q.Proceed()) {
        if (q.CandidateType() == CANDIDATE_NON_OPAQUE_TRIANGLE) {
            crossings++;
            if (crossings > 4096u) break;   // runaway guard (degenerate coincident geometry)
        }
        // Intentionally do NOT commit -> Proceed keeps walking to the next crossing.
    }
    return crossings;
}

bool InsideSolid(float3 p, float maxT) {
    // 3 fixed axes; a point is "inside" when the majority of axes report an odd crossing count.
    uint inVotes = 0;
    inVotes += (CountCrossings(p, float3( 1, 0, 0), maxT) & 1u);
    inVotes += (CountCrossings(p, float3( 0, 1, 0), maxT) & 1u);
    inVotes += (CountCrossings(p, float3( 0, 0, 1), maxT) & 1u);
    return inVotes >= 2u;
}

// --- Visibility: a single ray a->b. Occluded if a committed hit lands before b (minus an epsilon at each
// end so the endpoints' own surfaces don't self-block).
bool Visible(float3 a, float3 b, float bias) {
    float3 d = b - a;
    float dist = length(d);
    if (dist < 1e-5) return true;
    d /= dist;

    RayDesc ray;
    ray.Origin = a + d * bias;
    ray.Direction = d;
    ray.TMin = 0.0;
    ray.TMax = max(0.0, dist - 2.0 * bias);

    RayQuery<RAY_FLAG_FORCE_OPAQUE | RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH> q;
    q.TraceRayInline(Scene, RAY_FLAG_NONE, 0xFF, ray);
    q.Proceed();
    return q.CommittedStatus() == COMMITTED_NOTHING;   // nothing hit -> clear line of sight
}

// --- Closed-form Fibonacci sphere direction i of n (deterministic, no RNG). Golden-angle spiral.
float3 FibSphereDir(uint i, uint n) {
    float fi = (float)i;
    float fn = (float)n;
    float z = 1.0 - (2.0 * fi + 1.0) / fn;          // (-1,1), one band per index
    float r = sqrt(max(0.0, 1.0 - z * z));
    float phi = fi * 2.39996323;                     // golden angle (rad)
    return float3(r * cos(phi), r * sin(phi), z);
}

// Bound resources for all three entry points (plain root SRV/UAV — no bindless heap dependency):
StructuredBuffer<float3>   InPoints : register(t1);   // occupancy/classify: one float3 per element
RWStructuredBuffer<uint>   OutFlags : register(u0);   // occupancy/visibility/classify result code

struct VisPair { float3 A; float3 B; };
StructuredBuffer<VisPair>  InPairs  : register(t2);   // visibility: a/b pair per element
RWStructuredBuffer<float3> OutPoints : register(u1);  // nudge: corrected free-space position per element

[numthreads(64, 1, 1)]
void Occupancy(uint3 tid : SV_DispatchThreadID) {
    uint i = tid.x;
    if (i >= Count) return;
    float3 p = InPoints[i];
    OutFlags[i] = InsideSolid(p, ProbeRadius) ? 1u : 0u;
}

[numthreads(64, 1, 1)]
void Visibility(uint3 tid : SV_DispatchThreadID) {
    uint i = tid.x;
    if (i >= Count) return;
    VisPair pr = InPairs[i];
    OutFlags[i] = Visible(pr.A, pr.B, RayBias) ? 1u : 0u;
}

[numthreads(64, 1, 1)]
void Classify(uint3 tid : SV_DispatchThreadID) {
    uint i = tid.x;
    if (i >= Count) return;
    float3 p = InPoints[i];

    // Solid first (occupancy short-circuits the sphere cast).
    if (InsideSolid(p, ProbeRadius)) { OutFlags[i] = CLASS_SOLID; return; }

    // Fixed 32-ray Fibonacci sphere; count hits within ProbeRadius -> enclosure fraction.
    const uint K = 32u;
    uint hits = 0;
    [loop] for (uint k = 0; k < K; k++) {
        float3 dir = FibSphereDir(k, K);
        RayDesc ray;
        ray.Origin = p + dir * RayBias;
        ray.Direction = dir;
        ray.TMin = 0.0;
        ray.TMax = ProbeRadius;
        RayQuery<RAY_FLAG_FORCE_OPAQUE | RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH> q;
        q.TraceRayInline(Scene, RAY_FLAG_NONE, 0xFF, ray);
        q.Proceed();
        if (q.CommittedStatus() != COMMITTED_NOTHING) hits++;
    }
    // Walls close on most directions -> enclosed; open sky on most -> open. Threshold is internal/hidden
    // (the APV anti-pattern: zero front-door knobs). 0.5 = "more than half the hemisphere is walled".
    float frac = (float)hits / (float)K;
    OutFlags[i] = (frac >= 0.5) ? CLASS_ENCLOSED : CLASS_OPEN;
}

// First-hit distance from p along dir (ProbeRadius if nothing within reach). Deterministic single ray.
float FirstHitDist(float3 p, float3 dir) {
    RayDesc ray;
    ray.Origin = p + dir * RayBias;
    ray.Direction = dir;
    ray.TMin = 0.0;
    ray.TMax = ProbeRadius;
    RayQuery<RAY_FLAG_FORCE_OPAQUE | RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH> q;
    q.TraceRayInline(Scene, RAY_FLAG_NONE, 0xFF, ray);
    q.Proceed();
    return (q.CommittedStatus() != COMMITTED_NOTHING) ? (RayBias + q.CommittedRayT()) : ProbeRadius;
}

// --- Nudge: move an occupied point into free space along the fixed direction with the NEAREST surface exit
// (the shortest path out of solid), placing it just past that surface. A free point is returned unchanged.
// Deterministic: the K probe directions are the same closed-form Fibonacci sphere; ties broken by index.
[numthreads(64, 1, 1)]
void Nudge(uint3 tid : SV_DispatchThreadID) {
    uint i = tid.x;
    if (i >= Count) return;
    float3 p = InPoints[i];
    if (!InsideSolid(p, ProbeRadius)) { OutPoints[i] = p; return; }   // already free

    const uint K = 32u;
    float bestT = ProbeRadius + 1.0;
    float3 bestDir = float3(0, 1, 0);
    [loop] for (uint k = 0; k < K; k++) {
        float3 dir = FibSphereDir(k, K);
        float t = FirstHitDist(p, dir);
        if (t < bestT) { bestT = t; bestDir = dir; }   // nearest exit = shortest way out
    }
    // Step just past the nearest surface (a small margin beyond the exit). If nothing was hit within reach
    // (bestT == ProbeRadius), the point is effectively unbounded -> leave it (can't improve deterministically).
    float margin = max(4.0 * RayBias, 0.1);
    OutPoints[i] = (bestT <= ProbeRadius) ? (p + bestDir * (bestT + margin)) : p;
}
