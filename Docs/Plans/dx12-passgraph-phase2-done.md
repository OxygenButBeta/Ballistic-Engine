# DX12 Pass-Graph — PHASE 2 ACCEPTANCE (V4 decision + Phase-2 Definition-of-Done sign-off)

**Decision (chunk 17): V4 async-compute is NOT pursued. PHASE 2 is DONE.**
Plan: `C:\Users\suley\.claude\plans\silly-leaping-fog.md` (§"V4 — (optional) async compute",
§"Definition of done (phase 2)"). Branch `dx12-renderer`. Substrate pinned: AMD Radeon RX 9070 XT,
driver 32.0.31019.2002, Win 10.0.26200, commit baseline `6a6ead2b` (chunk-16 V3-complete tree).

---

## The V4 question (measure-first, per plan)

Plan §V4: async compute ships ONLY if "V3 ships clean AND a **measured win** exists" — it is OPTIONAL,
opt-in, and makes BOTH V2's alias planner and V3's barrier derivation queue-aware (large complexity,
the #1 device-removal risk via cross-queue fence/ownership bugs). So the gate is empirical: does
cross-queue (graphics ‖ compute) **overlap headroom** exist on this frame?

## Measurement (the existing per-pass timers — `BALLISTIC_STATS_OUT` / `BALLISTIC_DX12_GI_TIMING=1`)

Per-pass wall-time, ms (the only queue is serial, so a CPU stopwatch around each pass == that pass's
GPU wall-time — see `DX12HDRenderer.cs:985-988`). Run via `Runtime.exe` directly, graph + barriers on.

| Pass         | BistroInt (det) | BistroExt (det) | BistroInt (live, temporal active) |
|--------------|----------------:|----------------:|----------------------------------:|
| Deferred     |          0.0097 |          0.0177 |                            0.0195 |
| Sky          |          0.0020 |          0.0071 |                            0.0026 |
| Transparents |          0.3536 |          0.0392 |                            0.2593 |
| **GI (SSGI+OIDN)** |    **3.5439** |    **3.8198** |                        **3.7534** |
| Reflections  |          0.0139 |          0.0141 |                            0.0198 |
| SSAO         |          0.0113 |          0.0099 |                            0.0129 |
| TAA          |          0.0006 |          0.0005 |                            0.0082 |
| Composite    |          0.0170 |          0.0145 |                            0.0187 |
| cpuFrameMs   |          5.1057 |          5.1127 |                           10.4187 |

(Inline-CORE passes — shadows / geometry / Hi-Z / GatherPunctualLights — are graphics-queue
rasterization outside the graph; they don't change the analysis: still serial graphics work.)

## Verdict: NO meaningful async-compute headroom → declare PHASE 2 DONE

1. **Single-queue by construction.** `Dx12Device.cs:192` creates exactly ONE `CommandListType.Direct`
   queue; there is NO compute queue anywhere. V4 would build a whole second-queue substrate (new
   `CommandListType.Compute` queue + cross-queue fences + queue-ownership transfers) from scratch.
2. **GI is ~90% of graph GPU time but cannot overlap anything.** It sits mid-chain: it READS the full
   opaque+lighting+sky+transparents output (it composites GI INTO `SceneColor`) and its output FEEDS
   Reflections + Composite. The dependency DAG (already built in V1) serializes it — there is no
   independent graphics work to run concurrently with it. AND it contains the immobile
   `ExecuteSyncImmediate` OIDN hard node (cross-device CPU/GPU sync, plan §HAZARDS) which is
   un-overlappable by definition.
3. **Everything else is sub-millisecond.** Perfect overlap of ALL non-GI passes would save < 0.5 ms
   against a ~4 ms graph GPU frame — and the frame is CPU-bound anyway (5–10 ms cpuFrameMs), which
   async compute does not address (that is the P0b CPU↔GPU-overlap axis, deliberately OFF/orthogonal).
4. **Risk/reward is inverted.** V4 is the single biggest device-removal risk (cross-queue ownership)
   for a near-zero, uncertain win. Per plan, it would NOT be implemented blind in one chat regardless;
   here it isn't implemented at all because the precondition (measured headroom) is absent.

If the frame ever becomes GPU-bound with a heavy independent compute workload (e.g. a future
GPU-driven culling chain or async probe relighting that does NOT feed the same-frame SceneColor),
re-run this measurement; until then V4 stays unbuilt.

---

## Phase-2 Definition-of-Done — sign-off (plan §"Definition of done (phase 2)")

A real URP-RenderGraph / Frostbite-FrameGraph-class system, all gates met on the pinned substrate:

- **Passes declare reads/writes; the graph COMPILES a DAG, CULLS unused passes, computes a derived
  topological ORDER.** ✅ V1 (chunk 12, `f6f07d02`). `Dx12PassBuilder.Declare()` on all 11 passes;
  `Dx12RenderGraph.Compile()` (DAG → cull-to-fixpoint → Kahn topo, PQ keyed `(event,regIdx)`).
  `Dx12CullProbePass` exercises the culler every graph frame.
- **Transient targets aliased onto a pooled + descriptor-managed memory plan.** ✅ V2 (chunk 13,
  `18cf17f0`). `Dx12RenderTargetPool` (one placed-resource heap, greedy interval-coloring): 9 transients
  → 3 regions, 37 MB vs 57 MB un-aliased. Read-before-write audit (the load-bearing net) done.
- **Auto-derived BATCHED barriers replace the manual head transitions.** ✅ V3 (chunks 14–16,
  `4bd39194` / `7349c2d0` / `c128f6b6` / `8901e4d4` / `96d86004` / `929f01f6` / `6a6ead2b`). All 14
  (pass,role) boundary rows graph-derived (`Dx12BarrierDeriver`, CompareToManual asserts derived ⊇
  manual at init). Deriver report: OK on all 14, zero UNSOUND, zero NOT-migrated.
- **Cross-frame history IMPORTED, never aliased.** ✅ taaHistoryA/B, ssgiHistoryA/B, lumTarget/lumHistory
  + target/ldr/gbuffer marked imported in the pool.
- **`ExecuteSyncImmediate` points modeled as immobile hard nodes.** ✅ DDGI feedback / OIDN readback /
  screenshot readback never reordered or culled (opaque/imported, non-cullable).
- **Lumen untouched.** ✅ `git diff` across the phase confirms no DDGI / screen-probe / DxrGi / shader
  file changed beyond the 1-line GI-pass grain publish.
- **Pixel-neutral under BOTH oracle regimes (R-NEW-9):**
  - (a) Deterministic numeric golden gate: **15/15 SHA-256 == frozen golden** in ALL FOUR door states
    (default-off, `GRAPH=1`, `GRAPH+GRAPH_BARRIERS`, `+ GRAPH_ALIAS`) — chunk 16 + re-confirmed chunk 17.
  - (b) Non-deterministic temporal-stability: motion-dump boiling BistroInt **29.961352 bit-exact** to
    the frozen phase-1 band; BistroExt within band — aliasing/barriers never touch imported history.
- **GBV zero-NEW-error gate** vs `Docs/Validation/dx12-gbv-baseline.json`: CornellBox + BistroInterior,
  alias OFF and ON, every config exit 0 with BREAK_ON_ERROR.

**Excluded (orthogonal pre-existing, NOT phase-2 defects):** `RT_GI=1` / `RT_SHADOWS=1` device-remove
headless at SaveBmp readback (proven on baseline trees; the move-paths are verbatim, structure verified
via SSGI/SSR; add to golden once the RT-readback fault is fixed). SunTemple renders black headless.
GBV × FidelityFX pathologically slow (FSR derived path verified by SHA matrix + verbatim-move argument).

## What is left = PHASE 3 (the authored `[RenderFeature]` layer)

The graph is now solid (thin orchestrator + DAG/cull/aliasing/auto-barriers). Phase 3 sits on top:
a reflection-discovered, serialized, editor-reorderable `[RenderFeature]` layer that mirrors the
engine's Volume framework (`VolumeComponent` + attribute-driven inspector + type-name serialization +
one `Apply`-style bridge). It is the LAST piece — by plan, done only after the graph is solid, which
it now is. See plan §"phase 3" and §"Two precedents this mirrors".
