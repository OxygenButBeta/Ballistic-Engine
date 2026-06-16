// Compute self-test for the DX12 backend (BALLISTIC_DX12_COMPUTE_TEST=1). Proves the foundation the
// GPU-driven compute frustum cull is built on: a compute PSO (SM6.6), root UAV buffers, InterlockedAdd
// (the atomic compaction primitive the cull uses), Dispatch, and UAV->readback. Trivial math so the CPU
// can assert byte-exact: Output[i] = 2*i+1, and Counter[0] = the number of EVEN indices in [0,Count).
//
// Root sig (no descriptor heap — raw-buffer root UAVs): b0 = 1x uint Count (root constant),
// u0 = Output (RWStructuredBuffer<uint>), u1 = Counter (RWStructuredBuffer<uint>).

cbuffer Params : register(b0) { uint Count; uint3 _pad; };

RWStructuredBuffer<uint> Output  : register(u0);
RWStructuredBuffer<uint> Counter : register(u1);

[numthreads(64, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    uint i = id.x;
    if (i >= Count) return;
    Output[i] = i * 2u + 1u;
    if ((i & 1u) == 0u) {          // count even indices via the atomic — mirrors the cull's atomicAdd
        uint slot;
        InterlockedAdd(Counter[0], 1u, slot);
    }
}
