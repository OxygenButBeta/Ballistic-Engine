# Lumen FAZ 11 — Spatial Card Acceleration Grid (world-pos surface-cache lookup)

> Status: **COMPLETE** (all 3 stages landed + verified). Branch: `feature/lumen-gi`.
> `33fb6b98` build · `10ce9077` bindless · `3aec6e83` consume. Door BALLISTIC_DX12_LUMEN_CARDGRID (default OFF).
> VERIFIED: door OFF = byte-identical to pre-FAZ-11 golden (8-CB fan-out aligns perfectly); grid ON vs linear scan
> = byte-identical (pure accel, correctness-equivalent); ~100x fewer card tests/sample (avgPerCell 1.86 vs 14804).
> Unblocks SW-trace at Bistro scale + is the foundation for near-field translucency (FAZ 10.6 limitation).

## WHY (the gap this closes)
`LumenTrace.hlsl :: SampleSurfaceCache_WorldPos(hitPos, hitNormal)` currently **linear-scans ALL cards**
(`O(CardCount)`, Bistro = 14804). That makes it unusable per-pixel, which blocks:
1. **SW-trace GI quality** — the SW global-SDF march hits a world point with NO instance id, so it must
   world-pos-scan; today that's O(14804)/hit → too slow → SW path is degraded vs HW.
2. **Near-field translucency GI** — the transparent forward pass has a world pos but no instance id; a
   per-pixel all-card scan is a perf cliff (FAZ 10.6 documented this as the blocker). The radiance cache
   (far-field) is empty in interiors, so transparents there get no GI without this.

A spatial card grid turns `O(CardCount)` into `O(cards-in-cell)` (~handful), unblocking both.

## WHAT (UE parity: FLumenCardGrid / froxel-style)
A coarse uniform 3D grid over the card scene's world AABB; each cell holds the list of card indices whose
OBB overlaps it. Mirror the EXISTING froxel precedent in `Dx12ClusteredLights.cs` (offset+count grid + flat
index list) — same two-buffer layout, built CPU-side at card (re)build (cards are static per topology).

### Data (new, in `Dx12LumenCardScene`)
- `cardGridDim` (int3, e.g. 32³ — env `BALLISTIC_DX12_LUMEN_CARDGRID_DIM`, clamp [8,64]).
- World AABB of all cards (compute in `Rebuild`/`RefreshTransforms` from `cardsCpu` OBB extents).
- `cardGridBuf` (root SRV): `uint2[cellCount]` = {offset into index list, count}.
- `cardGridIndexBuf` (root SRV): flat `uint[]` card indices, cell-contiguous.
- Publish via `ctx.LumenRc`-style struct OR extend the trace CB: gridOrigin, gridCellSize(float3),
  gridDim(uint3), gridBufIdx, gridIndexBufIdx (bindless or root SRV — match the trace's existing binding).

### Build (CPU, in Rebuild after AllocatePages — cards are world-space by then)
1. Compute world AABB over all RESIDENT cards (PageId != 0xFFFFFFFF). Pad by one cell.
2. cellSize = aabbSize / cardGridDim.
3. For each card: rasterize its OBB's AABB into the grid (min..max cell range), append card index to each
   overlapped cell's bucket (two-pass: count → prefix-sum offsets → fill, exactly like ClusteredLights).
4. Upload cardGridBuf + cardGridIndexBuf (DeferredRelease old, like cardBuf).
5. Topology-invariant cells: rebuilt with the card list (same dirty gate). Transform change → RefreshTransforms
   already re-derives world cards → rebuild grid there too.

### Consume (HLSL — LumenTrace.hlsl)
Replace the `SampleSurfaceCache_WorldPos` all-card loop with:
1. worldPos → grid cell (clamp to grid; if outside AABB → return 0 / fall back to sky).
2. read {offset,count} for that cell; loop ONLY those card indices (+ optionally the 26 neighbors for OBBs
   straddling cell borders — start with just the home cell + the 6 face neighbors for safety).
3. same `LtSampleCard` OBB-contain + normal-align scoring as today; pick best; sample FinalLighting.
Keep the old linear scan behind a door (`BALLISTIC_DX12_LUMEN_CARDGRID=0`) for A/B until proven.

### Near-field translucency (the payoff — Dx12TransparentsPass + TransparentForward.hlsl)
The FAZ 10.6 explicit-SRV plumbing (t13-t15 radiance cache) already exists. ADD the card-grid + card/page
buffers + FinalLighting atlas as explicit SRVs (t16-t19) to the transparent light table, and in the shader,
when `RcEnabled==0` OR the radiance-cache sample is black, fall back to a `SampleSurfaceCache_WorldPos`-style
grid lookup at `i.PosW`. This gives glass in interiors real GI (where the far-field cache is empty).
NOTE: the surface-cache atlas (FinalLighting) rests in NonPixelShaderResource between passes — the transparent
pass reads it pixel-shader; verify the state (the fog/screen-probe read it fine cross-pass — established pattern).

## VERIFY
- Bistro SW-trace GI (`BALLISTIC_DX12_LUMEN_PROBE_SW=1`) quality ≈ HW (was degraded) + much faster than O(14804).
- Bistro INTERIOR transparents (656 glass items) pick up GI (the FAZ 10.6 limitation lifts) — A/B vs flat.
- CornellBox unchanged (12 cards, grid trivially small) — readback still 6/12 lit; determinism byte-identical.
- No regression with door OFF (linear scan path retained).
- GPU timing: the grid lookup should make SW-trace screen-probe + translucency cheaper, not just correct.

## IMPLEMENTATION STATUS
- **STAGE 1 DONE** `33fb6b98`: CPU grid build + upload in Dx12LumenCardScene (BuildCardGrid, called from Rebuild +
  RefreshTransforms). Gated BALLISTIC_DX12_LUMEN_CARDGRID (default OFF). Verified: door OFF → CornellBox 6/12 lit (no
  regression); door ON → Bistro dim=32³ resident=496 entries=60808 **avgPerCell=1.86** (vs O(CardCount) linear scan →
  ~100×+ fewer card tests per world-pos sample). No consumer yet → nothing observable changes.
- **STAGE 2 (consume in LumenTrace.hlsl) — CAVEAT:** `SampleSurfaceCache_WorldPos` is in the SHARED LumenTrace.hlsl
  include (screen-probe + reflections + radiance-cache + trace-debug all #include it). Binding the grid cell/index
  SRVs + adding grid CB params means touching EVERY consumer's root sig + CB, even though only the SW path
  (`LumenTraceSW`) calls the world-pos sampler. Wider blast radius than Stage 1 → do with user attention. Option to
  narrow: bind the grid only in the SW-using consumers (screen-probe SW mode), or via bindless ResourceDescriptorHeap[]
  (the consumers are already HeapDirectlyIndexed) to avoid root-sig changes — RECOMMENDED (grid SRVs as 2 reserved-tail
  bindless slots, like the clipmap/FinalLighting; then LUMEN_TRACE_PARAMS just gains gridIdx + grid placement floats,
  no per-consumer root-sig edits). That keeps Stage 2 as contained as Stage 1.

## STAGE 1.5 DONE `10ce9077`
Grid buffers stamped into surface-cache reserved-tail bindless slots +14/+15 (Used 14→16). Exposed
CardGridCellBindless/CardGridIndexBindless + placement. VERIFIED door ON Bistro: grid builds + SRVs stamp,
exit 0, no device-removed, renders normally. Now reachable via ResourceDescriptorHeap[] for stage 2.

## STAGE 2 — REMAINING (needs user review; touches production GI CB)
Add to LUMEN_TRACE_PARAMS (macro) + EACH consumer's inline-mirrored CB tail (LumenScreenProbe / LumenReflections
/ LumenRadianceCache / LumenTraceDebug — they MIRROR the fields, not just #use the macro) + each C# CB struct:
  `float3 LtCardGridOrigin; float LtCardGridEnabled; float3 LtCardGridCellSize; uint LtCardGridDim;`
  `uint LtCardGridCellIdx; uint LtCardGridIndexIdx; uint LtCgPad0, LtCgPad1;`  (16B-aligned, append at END)
Then rewrite SampleSurfaceCache_WorldPos: when LtCardGridEnabled, worldPos→cell, loop the home + 6 face-neighbor
cells' card indices (from the bindless cell/index buffers) instead of `for ci in 0..LtCardCount`. Set the C# fields
from cards.CardGrid* in each driver (Dx12LumenScreenProbe/Reflections/RadianceCache + the trace-debug pass).
RISK: 5 HLSL CBs + 5 C# structs must stay byte-aligned (mismatch = device-removed on the PRODUCTION GI path).
Verify: CornellBox determinism byte-identical + Bistro SW-trace (PROBE_SW=1) GI ≈ HW + faster; door OFF unchanged.

## STAGE 2 — CONFIRMED NEEDS USER REVIEW (re-investigated, no trivially-safe path)
Re-checked the "make it zero-CB" idea: the codebase passes ALL bindless indices via the CB (no HLSL hardcodes a
tail slot — verified by grep; the tail layout is relative/derived so a hardcoded HLSL #define would drift). So the
grid SRV indices + placement (origin/cellSize/dim/enabled) MUST reach the shader via the CB. `SampleSurfaceCache_WorldPos`
lives in the shared LumenTrace include → all 4 consumers (ScreenProbe/Reflections/RadianceCache/TraceDebug) reference
those fields by name → all 4 HLSL CBs + 4 C# CB structs must gain them, byte-aligned. This is the PRODUCTION GI path;
a misalignment = device-removed. → DO WITH USER REVIEW (per "yıkıcı/mimari kararlarda bana sor"). The change is
MECHANICAL (each consumer: append 8 fields to its CB tail in HLSL + C#, set from cards.CardGrid*, rebuild+verify) —
just needs attention because of the alignment fan-out + production blast radius. Stage 1+1.5 (build + bindless SRVs)
are landed + verified; only this consume step remains.

## RISK / SIZE
Medium. Isolated to Dx12LumenCardScene (build) + LumenTrace.hlsl (consume) + TransparentForward (payoff).
Froxel precedent de-risks the build. Door-gated → safe to land incrementally (grid build first, then SW-trace
consume, then translucency payoff — each its own commit, each GPU-verified). ~3 commits.

## OPEN QUESTION FOR USER
Build the grid over RESIDENT cards only (matches what can be sampled) vs ALL cards (future-proof if residency
changes per frame)? Recommend RESIDENT-only (cheaper, matches the sampleable set).
