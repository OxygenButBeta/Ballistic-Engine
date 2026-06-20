# DX12 Renderer — Performance: Fat-Trimming + Radical Rewrite Execution Plan

**Date:** 2026-06-20
**Branch:** current is `lumen-fidelity`. **Recommend a fresh `dx12-perf-radical` branch off the merge base** (or off `main` after the lumen-fidelity work lands) so the radical rewrites are isolated from in-flight GI fidelity work and each phase commits cleanly.
**Companion doc:** [dx12-renderer-perf-execution-plan.md](dx12-renderer-perf-execution-plan.md) (rev-2 — the correctness V-series + P0a/P0b/P0c pipelined frame). This plan is the **successor**; it does NOT re-do that plan's shipped work.

## What's already shipped (baseline — do NOT re-do)

The rev-2 plan's perf work is DONE and is the starting line for everything below:

- **Correctness V-series** (exposure/lighting/IBL/temporal/DX12-semantics) — done.
- **P0a** single-recorded-list, one-submit-per-frame (passes are recorders, not submitters) — done.
- **P0b** frame-in-flight overlap (N-buffered allocators/fences/CBs/descriptor heaps; FrameSlot-indexed upload slabs), **DEFAULT ON**, +11–18% FPS, byte-identical — done (commits `efd62153`, memory `dx12-p0b-overlap-shipped`).
- **P0c** present without per-frame full `dev.Flush()`, fence-gated backbuffer — done (rolled into the lumen-perf-uplift VSync-off/P0c work, commit `44d0244e`).
- **P4** shadow/light CPU cleanup (frustum once, single light view, cluster-AABB rebuilt only on projection change, pre-allocated caster list) — done.
- **Lumen SH-irradiance cache perf** (+106/+146 FPS, visually identical, adaptive denoise, priority budget, GPU timestamps) — done (commit `44d0244e`, memory `lumen-perf-uplift`).

So the frame is already **single-submit + overlapped + N-buffered**. The remaining cost is the *structure that still serializes inside the open frame* and the *geometry submit paths that never went GPU-driven* — that is what this plan attacks.

**Goal:** drive Bistro frame time down further by (a) trimming the last frame-internal blocking syncs and redundant CPU work, then (b) the radical structural moves — async compute, persistent bindless, unified GPU-driven geometry, and a fork to either mesh shaders or a visibility buffer. Every perf phase stays **byte-identical** to the deterministic reference, behind a **kill-switch env door**, **commit per phase**.

---

## 1. Measurement methodology — and the PASS_TIMING caveat (read before trusting any number)

### The trap: `bal perf` / `.stats.json` numbers are TRIAGE-only, WRONG for budgeting

When `BALLISTIC_STATS_OUT` is set (which `bal perf` and every screenshot `.stats.json` sidecar do), the renderer runs in **PASS_TIMING mode**: every pass is wrapped in its own `ExecuteSync` + `WaitForGpu` so each pass gets an isolated GPU timestamp (`DX12HDRenderer.cs:1104-1148`, `PassTimingEnabled` at `:1121`). Consequence:

- `gpuFrameMs` is the **pass-SUM with zero overlap** — it does not reflect the real pipelined GPU timeline.
- `cpuFrameMs` **includes the per-pass waits** — inflated by the serialization the timing harness itself introduces.
- Measured headline on Bistro: `cpuFrameMs ~9ms` vs `gpuFrameMs ~4.5ms`, and **nearly constant across 0.6M → 2.8M triangle scenes** (Exterior 2.83M / Interior 0.80M / SunTemple 0.61M all land ~9ms CPU). That flatness is the tell: the cost is **frame structure + blocking syncs**, not geometry throughput.
- A single "fat" pass shows ~3.5ms but it is a **different pass per scene** (Exterior=Composite, Interior=Transparents, SunTemple=GTAO) — that is the serialized pass draining the GPU's accumulated async work, NOT real shader cost. Do not optimize against it.

These numbers are excellent for **triage** (which pass moved relatively after a change) and **useless for absolute budgeting** (they describe a serialized frame that does not ship).

### The rule: a Tracy baseline BEFORE each radical phase

Real pipelined frame timing requires **Tracy** (`BALLISTIC_TRACY=1`, then `Tools\Tracy\tracy-capture.exe -o out.tracy -s 10 -f` → `tracy-csvexport.exe out.tracy`). Tracy shows the actual CPU submit timeline and (with the GPU-context zones the lumen-perf-uplift work added) the real overlapped GPU spans.

**MANDATORY for every R-phase:** capture a Tracy baseline of the targeted region BEFORE writing code, to **prove the serialization you intend to remove actually exists on the GPU timeline** — never assume it from the PASS_TIMING sum. Then capture again after, to **prove the overlap materialized** (e.g. R1 must show the async-compute zone overlapping the graphics zone on two GPU lanes, not just a faster sum). If Tracy does not show the predicted gap, the phase's premise is wrong — stop and re-diagnose, do not ship a no-op rewrite.

Triage flow per phase: `bal perf <scene>` for the relative pass deltas + `RenderStats.Scene.GpuPasses` overlay → Tracy for the real before/after → `bal imgdiff` for the byte-identical gate.

---

## 2. Non-negotiable guardrails (EVERY phase — inherited from rev-2)

1. **[gpu-hang-launch-safety] is absolute.** A TDR hard-crashed the user's PC before. On first device-removal: STOP, make safe, commit what's safe, diagnose with DRED (`BALLISTIC_DX12_DEBUG=1`), verify headlessly — **never relaunch a hanging build in a loop**. The radical phases (R1 cross-queue fences, R2 bindless heap lifetime, R4 mesh-shader PSOs) are exactly the class of change that causes device-removal — treat each first launch as load-bearing.
2. **Deterministic-capture is the oracle.** `BALLISTIC_SCREENSHOT_PAUSED=1 BALLISTIC_DETERMINISTIC=1` → byte-diffable frames. Every perf phase here is **byte-identical** (meanError 0 vs the pre-phase deterministic reference). A perf phase that changes pixels is a bug, not a feature.
3. **Lumen stability gate.** Any change that touches a buffer Lumen reads/writes, or reorders work around the Lumen passes (R1 async compute especially overlaps the Lumen RayQuery trace), must pass the orbit harness (`bal render <scene> --orbit 8`, diff consecutive frames) with **no new ghosting or sparkle growth, Lumen on**. R1 is the highest risk here because it reschedules the GI trace relative to the surface-card cache fill.
4. **Byte-identical for perf phases.** This entire plan is perf — there is no "intentional pixel change" escape hatch except where a fork explicitly trades quality (only PP2, called out).
5. **Commit per phase, smallest reversible step.**
6. **Keep a kill-switch env door per radical feature** through bring-up and ship it ON-by-default only after the gate passes on the full test matrix. Doors below mirror the existing `BALLISTIC_DX12_GPU_LIGHTCULL` / `_GRAPH` / `_GRAPH_BARRIERS` pattern (env read once at init, `!= "0"` / `== "1"` semantics, byte-identical fallback).

### Test matrix (run all, every phase)

| Scene | Exercises |
|---|---|
| `Assets/Bistro_v5_2/BistroExterior.scene` | whole-mesh GPU-driven path, ~1600 submeshes, 2.8M tri — the headline perf scene |
| `Assets/Bistro_v5_2/BistroInterior_Wine.scene` | clustered lights + Lumen GI + dim interior (Lumen stability) |
| A split-by-node import scene (SubMeshIndex≥0) | the **CPU per-submesh path** R3 targets — must exist or be authored as a fixture |
| A skinned-mesh scene (`Assets/Characters/SkinTest.scene`) | the skinned CPU path R3 folds in |
| `Assets/CornellBox/CornellBox.scene` | GI-isolate ground truth (Lumen on/off) for R1's reschedule gate |
| `Assets/TransparentTest/TransparentTest.scene` | forward path regression guard (untouched by all phases) |

---

## 3. Current architecture (verified against source — the starting point)

- **Deferred shading, fat 5-RT G-buffer + depth, NO z-prepass** (geometry rasterized ONCE — CLAUDE.md's "z-prepass contract" is stale for DX12). RT0 RGBA8_SRGB albedo+specF0, RT1 RGBA16F normal, RT2 RGBA8 MRAO+flags, RT3 RGBA16F emissive, RT4 RG16F motion; D32 depth. `Dx12GBuffer.cs:14–33`.
- **Clustered lighting**, 16×9×24 log-Z froxel (3456 clusters), CPU cull default / GPU compute cull optional and byte-identical (`BALLISTIC_DX12_GPU_LIGHTCULL`). `Dx12ClusteredLights.cs:21–22,52`.
- **GPU-driven whole-mesh** (`SubMeshIndex < 0`, non-skinned, single-shader): compute frustum cull + Hi-Z occlusion + ExecuteIndirect + bindless material table, default ON. `Dx12GpuDrivenRenderer.cs`.
- **CPU per-submesh path** for everything else: split-by-node imports (`SubMeshIndex ≥ 0`), skinned meshes, mixed-shader renderers — per-submesh frustum cull + DrawConstants CB write + 6× `CopyDescriptorsSimple` + `DrawIndexedInstanced`. `DX12HDRenderer.cs:1367–1578`.
- **ONE Direct queue only** — no async compute, no copy queue. `Dx12Device.cs:199`.
- **Barriers:** manual idempotent self-methods per pass (V1); optional auto-derive (`BALLISTIC_DX12_GRAPH_BARRIERS=1`, default off); no cross-pass minimal-barrier set. `Dx12BarrierDeriver.cs`, `Dx12RenderGraph.cs`.
- **Render graph:** Phase-1 ordered list (default); Phase-2 V1 compiled topo-order (`BALLISTIC_DX12_GRAPH=1`); **Phase-2 V2 real frame graph (transient aliasing + async scheduling) NOT yet implemented** — this is the natural host for R1.
- **Descriptors:** per-frame shader-visible ring, N-buffered by FramesInFlight; per-draw `CopyDescriptorsSimple`. **No persistent SM6.6 `ResourceDescriptorHeap` bindless** for the general draw path yet (the Hi-Z/whole-mesh path already uses a bindless material table).
- **Upload CBs** properly N-buffered, no stalls. Classic vertex/index IA; **no mesh shaders**.

---

## 4. Phase-at-a-glance (dependency graph, impact / risk / effort, SUBSUMES relations)

| Phase | Layer | What | Impact | Risk | Effort | Depends on / Subsumes |
|---|---|---|---|---|---|---|
| **PP1** | incremental | Fold remaining frame-internal blocking syncs (RenderShadows `ExecuteUpload`, BuildHiZ `ExecuteSync`) into the open frame list | Med (removes 2 stalls) | Low | S | — |
| **PP2** | incremental | Lumen à-trous denoise full-res → half-res + depth-aware upsample | Med (GPU) | Low-Med | S-M | Lumen (stability gate) |
| **PP3** | incremental | Lumen `RefreshTransforms` per-instance dirty flag (don't rebuild all card planes) | Low-Med (dynamic scenes) | Low | S | — |
| **R1** | radical | **Async compute queue** — overlap GTAO + shadow-cull + Lumen RayQuery trace behind graphics | **High, certain** | **High** (cross-queue fences) | L | Hosted by Phase-2 V2 frame graph; after PP1 |
| **R2** | radical | **Persistent SM6.6 bindless heap** — kill per-frame descriptor ring churn + per-submesh 6× CopyDescriptorsSimple | High | Med-High | L | **SUBSUMES P3-style descriptor caching** — do NOT plan both |
| **R3** | radical | Collapse CPU per-submesh paths (split-node / skinned via compute skinning / mixed-shader) into unified GPU-driven ExecuteIndirect | High | Med-High | L | **SUBSUMES P2-style CPU-cull trimming**; **pairs with R2** |
| **R4** | radical (fork A) | Mesh-shader / meshlet pipeline (amplification+mesh, per-meshlet cull) + import-time meshlet gen | Highest ceiling | High | XL | After R2/R3; **fork vs R5 — not both now** |
| **R5** | radical (fork B) | Visibility buffer (single-RT tri/instance id + deferred material compute) to cut 5-RT fat G-buffer bandwidth | Highest ceiling | High | XL | After R2/R3; **fork vs R4 — not both now** |

**SUBSUMES — called out so there is no double-work:**
- **R2 subsumes the rev-2 P3** (per-pass descriptor-table caching). Persistent bindless makes the per-frame ring churn and the per-draw copies *disappear* rather than be cached. **Do not implement P3; go straight to R2.**
- **R3 subsumes the rev-2 P2** (CPU-cull trimming). R3 *removes that CPU path entirely* by routing it through GPU compute cull + ExecuteIndirect. **Do not micro-optimize the CPU cull; replace it in R3.**

**Dependency reasoning:**
- PP1 before R1: R1's frame graph wants a frame with no surprise mid-frame `ExecuteSync`/`ExecuteUpload` blocks to schedule around. Clean those first.
- R2 + R3 together: R3's unified ExecuteIndirect needs bindless materials for *all* draws (R2). They share the material-table + descriptor mechanism; doing them in one arc avoids a throwaway non-bindless intermediate for R3.
- R4/R5 last and **mutually exclusive for now** — both are XL rewrites of the geometry/material front-end with overlapping surface area; committing to both at once doubles risk for no extra ceiling. Pick after R2/R3 land and Tracy shows the residual bound (front-end submit vs G-buffer bandwidth).

---

## 5. Phases in detail

### PP1 — Fold remaining frame-internal blocking syncs into the open frame list

- **Goal:** remove the last per-frame blocking GPU syncs that P0a/P0b did not cover, so the frame is truly one recorded list end-to-end (no mid-frame `WaitForGpu`).
- **Files/symbols:**
  - `DX12HDRenderer.cs:2093` — `RenderShadows` uses `dev.ExecuteUpload(...)` (submits + may sync mid-frame). Record into the **open frame command list** instead, sequenced before the G-buffer fill (shadow maps must be ready before deferred lighting reads them — keep the ordering, drop the submit).
  - `Dx12GpuDrivenRenderer.cs:375` — `BuildHiZ` does `dev.ExecuteSync(...)`, a full flush to build the Hi-Z pyramid from current depth. Fold into the open list with a UAV/transition barrier instead of a sync (the pyramid is consumed by the *next* frame's occlusion cull, so a same-list barrier suffices — verify the cross-frame consumer reads the right slot under P0b N-buffering).
- **Mechanism:** convert both call sites from submit/sync helpers to recorders that append to the frame's `ID3D12GraphicsCommandList4`, with explicit barriers where the sync previously provided ordering.
- **Hazards:** the Hi-Z pyramid is consumed a frame behind. Under P0b overlap, confirm the resource is N-buffered or the cross-frame read is fence-correct, or the EF3-class "stale bindless Hi-Z SRV" bug reappears (memory `editor-resize-hang-ef3`). The shadow cull must still complete before the cascade depth draws in the same list — barrier, not sync.
- **Env door:** `BALLISTIC_DX12_PP1_INLINE_SYNCS=0` restores the old `ExecuteSync`/`ExecuteUpload`.
- **Gate:** byte-identical on full matrix; Tracy shows the two mid-frame GPU bubbles gone (one continuous graphics lane); Lumen stability gate (shadow timing feeds GI).

### PP2 — Lumen à-trous denoise: full-res → half-res + depth-aware upsample — ❌ CANCELLED (premise refuted by source)

> **2026-06-20 verdict: do NOT implement.** Reading the real code refuted the premise. The Lumen front end
> ALREADY has a resolution-scale knob (`BALLISTIC_DX12_LUMEN_RESSCALE`, `Dx12LumenGiPass.Resize`) that runs the
> whole GI chain — trace + integrate + **denoise** + temporal — at half/quarter res with a depth-aware upsample
> in combine. It is deliberately defaulted to FULL-res because it was **measured** on the RX 9070 XT: half/quarter
> res gave **NO perf win** (Lumen here is RT-traversal/dispatch-bound, not pixel-bound) and **cost quality**
> (Cornell/Bistro hotspot +5–8%, a visible 2×2 block sparkle at half-res probes). Post-SH-cache Lumen is ~0.15ms
> total (`bal perf`), so the denoise sub-pass is well under 0.1ms — there is no half-res win to capture, only a
> quality regression to re-introduce. The plan's "denoise is GPU-heavy" came from the PASS_TIMING-inflated
> triage number; the real pipelined cost is already negligible. **Skipped, gerekçeli.**

- **Goal (obsolete):** halve the denoise pass cost by running the à-trous spatial denoise at half resolution then depth-aware-upsampling, matching SSR's existing half-res pattern.
- **Files/symbols:** the Lumen à-trous denoise pass under `BallisticEngine.DX12/Lumen/` (the per-pixel indirect spatial denoise referenced in `lumen-v2-replacement.md` as P7 #1b, deferred there). Add a half-res target from the transient RT pool + a depth-aware upsample step (reuse SSR's depth-aware upsample pattern — do NOT hand-roll a new one).
- **Mechanism:** denoise at half-res, bilateral/depth-aware upsample to full-res before the indirect is added into HDR color.
- **Hazards:** this is **quality-affecting** (half-res denoise ≠ byte-identical). Gated on the **Lumen stability + visual-parity** bar, NOT byte-identical — call it out in the commit. Half-res denoise amplifies fireflies if inputs aren't sanitized — confirm the existing NaN-scrub ternaries survive (the `lerp(v,0,flag)` AMD trap rule).
- **Env door:** `BALLISTIC_DX12_LUMEN_DENOISE_HALFRES=0` (default OFF until the visual gate passes, then flip ON).
- **Gate:** Tracy shows denoise ~halved; orbit-stability clean; visual parity on Interior_Wine + CornellBox.

### PP3 — Lumen `RefreshTransforms` per-instance dirty flag

- **Goal:** stop rebuilding ALL card planes when ANY instance moves; rebuild only the moved instances.
- **Files/symbols:** `Dx12LumenScene.cs:316–332` (`RefreshTransforms`) — today, on any transform-stamp change, it rebuilds all instances' meta + card frames wholesale. Add a per-instance dirty set keyed off the per-instance world-matrix hash already computed nearby; patch only changed instances' slab.
- **Mechanism:** track per-instance world-matrix hash; on change, mark dirty and patch only that instance's range in `instanceMeta`/`clusterCards` rather than recreating the whole buffer.
- **Hazards:** card frames are world-space; a partial update must keep the `triToCluster` map + cluster offsets consistent (mesh-cached/topology-invariant → safe, but verify the partial path doesn't desync the SH-cache history → ghosting). Bistro is mostly static; preserve the static fast-path (no dirty → zero work, same as today).
- **Env door:** `BALLISTIC_DX12_LUMEN_PARTIAL_REFRESH=0` falls back to wholesale rebuild.
- **Gate:** byte-identical on static scenes; on a moving-instance scene, GI matches the wholesale output and Tracy shows rebuild cost scale with moved-instance count. Lumen stability gate.

---

### R1 — Async compute queue — ⚙️ INFRA SHIPPED, pilot-pass binding deferred (needs live-GPU validation)

> **2026-06-20 status.** The async-compute INFRASTRUCTURE is built, compiles, and is byte-identical with the
> default OFF (`BALLISTIC_DX12_ASYNC_COMPUTE` unset): a 2nd `ID3D12CommandQueue(Compute)` + a shared cross-queue
> `asyncFence` + N-buffered compute allocators/lists + a per-slot post-split graphics allocator + the
> `Dx12Device.RecordAsyncCompute(record)` hand-off (submit graphics-so-far → signal A → compute waits A → run
> compute → signal B → reopen graphics on the post-split allocator → graphics waits B; all GPU-side, CPU never
> blocks → real overlap). Drained in Dispose; inert until a pass routes work through it.
>
> **Pilot-pass binding NOT done, deliberately.** Auditing the frame for a SAFE first async pass found none that
> can be flipped blind: **GTAO is a graphics PSO** (pixel-shader fullscreen draw → can't run on the compute
> queue); **RTAO** is compute but its Record transitions the shared AO/depth/normal through `PixelShaderResource`
> and does a copy-back — states that are ILLEGAL on a compute queue, and its own comment records that a
> split-submit version already "tripped 580 InvalidSubresourceState"; **the Lumen trace** is the genuinely
> overlap-worthy candidate but it's the active lumen-fidelity WIP surface and the most stability-sensitive pass.
> Binding any of these without a live GPU + Tracy to prove (a) the overlap actually materializes and (b) no
> cross-queue state-hazard hang would violate the absolute gpu-hang-launch-safety rule. So R1 ships the substrate;
> the pilot binding is a follow-up that pairs a state-clean candidate (Lumen trace, or RTAO with its copy-back
> split back to the graphics queue) with a live Tracy capture. Kill-switch: `BALLISTIC_DX12_ASYNC_COMPUTE=1`.

- **Goal:** add a second `ID3D12CommandQueue(Compute)` and overlap compute-only passes with graphics: **GTAO** + **shadow-cull** with graphics, and the **Lumen RayQuery trace** (~2–5 ms GPU on Bistro) behind deferred shading. Byte-identical, no shader changes.
- **Files/symbols:**
  - `Dx12Device.cs:199` — currently `Queue = CreateCommandQueue(Direct)` only. Add `ComputeQueue = CreateCommandQueue(Compute)` + cross-queue fences (shared `ID3D12Fence`, `Queue.Signal` / `ComputeQueue.Wait` and vice-versa).
  - **Host = the not-yet-built Phase-2 V2 frame graph** (`Dx12RenderGraph.cs` — V1 compiled topo-order exists; V2 async scheduling does not). R1 *is* the motivation to build V2's async scheduler: the graph already knows pass read/write sets, so it can assign compute-only passes (no graphics-pipeline state) to the compute queue and emit cross-queue sync edges.
  - First three candidates: GTAO, `BuildShadowCull` (`Dx12GpuDrivenRenderer`), and the Lumen trace dispatch (all compute/DXR-dispatch only, well-defined I/O).
- **Mechanism:** record async-eligible passes onto `ComputeQueue`; insert fence edges where graphics consumes the result (deferred lighting waits on GTAO; GI composite waits on the Lumen trace). The frame graph derives edges from existing pass dependency metadata.
- **Hazards (HARDEST correctness risk in the plan):**
  - **Cross-queue state hazards:** a resource written on compute and read on graphics needs a fence, not a barrier — D3D12 state transitions do NOT synchronize across queues. Wrong = read-before-write garbage or a hang → device removal. **gpu-hang-safety in full force.**
  - **Overlapping the Lumen trace** reschedules GI relative to the surface-card cache fill + SH-cache temporal history — the Lumen stability gate is load-bearing.
  - Compute contends for the same GPU; Tracy must **prove actual overlap** (two populated lanes), not a smaller sum. If a sub-overlap stays serialized by a tight dependency, drop it — its fence cost isn't worth it.
- **Env door:** `BALLISTIC_DX12_ASYNC_COMPUTE=0` routes all passes back to the single Direct queue (byte-identical). Per-pass sub-doors (`…_ASYNC_GTAO`, `…_ASYNC_LUMEN`) to A/B which overlap pays.
- **Gate:** byte-identical; **Tracy proof of overlap required**; Lumen stability gate; full matrix; one clean launch per gpu-hang-safety before any relaunch.

### R2 — Persistent SM6.6 bindless heap (`ResourceDescriptorHeap`)

- **Goal:** replace the per-frame shader-visible descriptor ring + per-draw `CopyDescriptorsSimple` with a persistent SM6.6 `ResourceDescriptorHeap` indexed by resource ID in HLSL. Kills per-frame ring churn AND the **per-submesh 6× `CopyDescriptorsSimple`** at `DX12HDRenderer.cs:1367–1578`.
- **Files/symbols:** the per-frame ring allocation in the renderer + the existing bindless material-table heap (already used by Hi-Z + whole-mesh — extend to a general persistent heap all draws index). Per-draw copies in the CPU per-submesh path (`:1367–1578`) and any pass copying into the ring.
- **Mechanism:** allocate persistent bindless indices for textures/buffers once (on load/resize/IBL-rebake), pass indices via root constants / a per-draw constant, shaders read `ResourceDescriptorHeap[index]` (SM6.6). No per-draw copies, no per-frame ring rebuild.
- **Hazards:** requires SM6.6 + `RESOURCE_BINDING_TIER_3` — capability-gate with a ring fallback. **Descriptor lifetime is the trap** (the EF3 hang + the Hi-Z "re-point after recreate" precedent): a persistent index whose backing resource is recreated (resize, target realloc, IBL rebake, scene swap → `RenderSetsCleared`) MUST be re-pointed or the heap serves garbage → hang. Centralize allocate/free/re-point.
- **Env door:** `BALLISTIC_DX12_BINDLESS=0` restores the ring + per-draw copies (byte-identical).
- **Gate:** byte-identical; Tracy shows per-draw copy cost gone from CPU submit + ring rebuild gone; full matrix incl. a scene-swap (validates re-point on `RenderSetsCleared`) and a resize (validates re-point on realloc).

### R3a STATUS (2026-06-20): split-import GPU-driven landed opt-in, NOT yet byte-identical

> `BALLISTIC_DX12_GPUDRIVEN_SPLIT=1` routes split-import (SubMeshIndex>=0, non-skinned, single-shader) renderers
> through the GPU compute-cull + ExecuteIndirect path (geometry pass only; shadows stay CPU). RenderInto now clamps
> each renderer's submesh range by its SubMeshIndex. Validated on a synthetic split fixture
> (`SampleProject/Assets/CornellBox/CornellBoxSplit.scene`, 4 entities sharing CornellBox.obj at SubMeshIndex 0..3):
> - **No hang** (DRED clean), `draws` collapses 4→1 ExecuteIndirect — the mechanism works.
> - **NOT byte-identical:** meanError 0.06 (11.9% px) full; with Hi-Z occlusion off it drops to 0.0035 (1%), and a
>   single-submesh split with Hi-Z off still differs 0.002 (0.4%). So TWO diff sources: (1) Hi-Z occlusion culls
>   overlapping split siblings the CPU path doesn't (the fixture is pathological — same position; real split-import
>   has distinct node transforms, may not trigger it); (2) a residual ~0.4% per-submesh GPU-ExecuteIndirect vs
>   CPU-DrawIndexed difference (FP/cull-ordering or SubmeshMeta write — R2's CPU-bindless path through the SAME
>   GBufferBindless.hlsl IS byte-identical, so the delta is in the GPU cull/meta, not the shader).
> - **Default OFF**, byte-identical when off. Ship ON only after both diff sources are resolved (align split Hi-Z
>   with the CPU semantics + find the 0.4%). R2's CPU-bindless split path remains byte-identical and is the safe
>   default for split-import bindless until R3a is exact.
>
> **R2 production validation (this session): split-import + GPUDRIVEN on, CPU_BINDLESS=1 == OFF byte-identical** on
> the split fixture — R2's "needs a split-import scene" gap is now closed.
>
> **R3a residual 0.4% — isolation (2026-06-20):** Narrowed but not eliminated. Controls:
> - whole-mesh **GPU-driven == CPU descriptor-table byte-identical** (CornellBox, Hi-Z off) — the GPU-driven
>   path/cull/meta/shader is correct in isolation.
> - single-submesh split, Hi-Z off, **R3a(GPU ExecuteIndirect) vs R2(CPU bindless DrawIndexed)**: same material
>   table + same GBufferBindless.hlsl + same Transpose(model*viewProj) Mvp (GpuCull passes SubmeshMeta.Mvp through,
>   does NOT recompute) → STILL 0.002 meanError / 0.4% px. So the delta is NOT shader, NOT material, NOT MVP, NOT
>   Hi-Z, NOT multi-split — it's a pixel-level ExecuteIndirect-vs-DrawIndexed nuance that does NOT appear for
>   whole-mesh (where the GPU-vs-CPU control is byte-identical). Leading remaining hypotheses: a per-frame draw-
>   ORDER difference (ExecuteIndirect emits in cull-slot order; the CPU loop in renderer-iteration order — with
>   depth-equal coplanar fragments at box seams the last-writer differs) OR a conservative-raster/PSO-state nuance.
>   0.002 meanError is visually imperceptible; R3a is opt-in + hang-safe + already captures the CPU→indirect
>   submit win. Resolving the 0.4% (likely the draw-order tie-break at coplanar seams) is the gate to default-ON.

### R3 — Collapse CPU per-submesh paths into unified GPU-driven ExecuteIndirect

- **Goal:** route split-by-node (`SubMeshIndex ≥ 0`), skinned, and mixed-shader renderers through the **same** GPU compute frustum/Hi-Z cull + ExecuteIndirect path the whole-mesh renderer uses — eliminating the CPU per-submesh loop at `DX12HDRenderer.cs:1367–1578`. **Pairs with R2** (bindless materials for every draw).
- **Files/symbols:** `DX12HDRenderer.cs:1367–1578` → fold into `Dx12GpuDrivenRenderer`. Skinned meshes need **compute skinning** (skin on GPU into a transient vertex buffer, then feed the same indirect path). Mixed-shader needs the bindless material table (R2) to select shading per-submesh without a CPU PSO switch.
- **Mechanism:**
  - Split-node (`SubMeshIndex ≥ 0`): build per-submesh indirect args + per-submesh world AABBs (CPU AABBs are already bit-identical to the GPU 8-corner loop), feed the existing compute cull.
  - Skinned: a compute skinning pass writes posed vertices to a transient buffer the indirect draw consumes.
  - Mixed-shader: bindless material table (R2) carries shader/material selection; a material-indexed pixel shader replaces the CPU PSO switch.
- **Hazards:** no z-prepass in DX12 (CLAUDE.md stale) → no prepass-match constraint; the real constraint is the unified indirect output must be **bit-identical** to the old CPU draws (same cull decision — AABB parity already holds; same submesh ordering for deterministic captures). Compute skinning must match CPU skinning exactly. Material-table stamping must clear on scene swap (`RenderSetsCleared`).
- **Env door:** `BALLISTIC_DX12_UNIFIED_DRAW=0` restores the CPU per-submesh + skinned blocks. Sub-door `…_COMPUTE_SKINNING=0` keeps CPU skinning while the indirect path is validated for static split-node first.
- **Gate:** byte-identical on the split-node fixture + skinned scene; Tracy shows the CPU submit time for those scenes collapse. Subsumes P2 — say so in the commit.

### R4 — Mesh-shader / meshlet pipeline (FORK A — do LAST, not with R5)

- **Goal:** replace classic vertex/index IA with an amplification+mesh-shader pipeline doing **per-meshlet cull** on-GPU (frustum + backface cone + Hi-Z per meshlet) — highest ceiling for the whole-mesh path.
- **Files/symbols:** import-time **meshlet generation** (a new AssetPipeline step producing meshlets + per-meshlet bounds/normal cones, stored in the `.bmesh` artifact) + a mesh-shader PSO path replacing/augmenting the ExecuteIndirect geometry submit. Requires R2's bindless material table.
- **Mechanism:** amplification shader culls meshlets and dispatches mesh-shader threadgroups for survivors; mesh shader emits the meshlet's primitives directly.
- **Hazards:** longest/highest-effort; requires `OPTIONS7` mesh-shader tier support (capability-gate + fallback to R3's ExecuteIndirect on HW without it — keep that path alive). Import-time meshlet gen = `.bmesh` format change → ArtifactDB version bump + reimport. New PSO type = device-removal risk on first launch.
- **Env door:** `BALLISTIC_DX12_MESHLETS=0` falls back to R3's ExecuteIndirect (byte-identical when off).
- **Gate:** byte-identical (meshlet cull must not drop visible triangles); Tracy proof the front-end submit/cull cost drops on Bistro; full matrix.

### R5 — Visibility buffer (FORK B — alternative to R4, not both now)

- **Goal:** cut the **5-RT fat G-buffer bandwidth** (`Dx12GBuffer.cs:14–33`) by rasterizing a **single-RT** triangle+instance ID buffer, then resolving material attributes in a deferred compute pass fetching vertex/material data by ID.
- **Files/symbols:** `Dx12GBuffer.cs` (replace the 5-MRT layout with a single ID target + depth for the visibility pass) + a new deferred material-resolve compute pass reconstructing G-buffer attributes from the ID buffer via bindless vertex/material data (R2). Deferred lighting then reads the resolved attributes.
- **Mechanism:** geometry pass writes only `{instanceID, triangleID}` (+ depth, + motion if kept); a compute pass per pixel fetches the triangle's verts, interpolates with barycentrics, samples materials bindless, writes a thin G-buffer or shades inline.
- **Hazards:** as large as R4; touches the **entire** deferred front-end (every G-buffer reader: lighting, GTAO, SSR, fog, Lumen). Motion vectors + TAA + FSR consume RT4 today — the vis-buffer must still produce motion. Barycentric/partial-derivative material gradients are non-trivial (manual gradients for mip selection). High Lumen-interaction risk (GI reads normals/depth).
- **Env door:** `BALLISTIC_DX12_VISBUFFER=0` restores the fat G-buffer.
- **Gate:** byte-identical (or sign-off-gated visual parity if exact match is impractical — flag explicitly); Tracy proof G-buffer write bandwidth drops; full matrix incl. Lumen stability + transparent/forward regression.

**The R4-vs-R5 decision is a FORK, decided AFTER R2/R3 land**, on what Tracy shows is the residual bound: front-end **submit/cull cost** dominates → R4; **G-buffer write/read bandwidth** dominates → R5. Do **not** start both.

---

## 6. Recommended execution order + rationale

1. **PP1** (fold remaining syncs) — cheap, low-risk; gives R1 a clean frame to schedule. First.
2. **PP2 + PP3** (Lumen denoise half-res, partial RefreshTransforms) — cheap GPU/CPU warmups, independent of the radical arc. Slot in while the R1 frame-graph design is in flight. (PP2 is the one symptom-gated phase.)
3. **R1** (async compute) — highest certain GPU win, byte-identical, no shader changes. Do it before the bigger rewrites so the frame-graph async scheduler (Phase-2 V2) exists as the host for everything after. Highest correctness risk → most Tracy/launch-safety discipline.
4. **R2 + R3 together** (bindless + unified GPU-driven draw) — one arc, because R3 needs R2's bindless materials for all draws. Largest CPU-submit reduction. Subsumes P2 + P3.
5. **Fork: R4 XOR R5** — pick from the post-R2/R3 Tracy residual bound. Do one, last.

### Per-phase commit + verification recipe

```bash
# 0. BEFORE a radical phase: Tracy baseline — prove the targeted serialization exists
BALLISTIC_TRACY=1 <run exe on scene>
Tools\Tracy\tracy-capture.exe -o before.tracy -s 10 -f
Tools\Tracy\tracy-csvexport.exe before.tracy        # per-zone GPU/CPU spans

# 1. Deterministic reference (the byte-identical oracle), pre-phase
BALLISTIC_SCREENSHOT_PAUSED=1 BALLISTIC_DETERMINISTIC=1 bal render <scene> --out ref/<scene>.bmp

# 2. Implement phase behind its kill-switch door (default OFF during bring-up)

# 3. Byte-identical gate (door ON vs the reference)
BALLISTIC_SCREENSHOT_PAUSED=1 BALLISTIC_DETERMINISTIC=1 bal render <scene> --out work/<scene>.bmp
bal imgdiff ref/<scene>.bmp work/<scene>.bmp         # expect meanError 0 (perf phases)

# 4. Lumen stability gate (PP2, PP3, R1, R5)
bal render <scene> --orbit 8                          # diff consecutive frames: no ghost/sparkle growth

# 5. Triage + AFTER Tracy: prove the overlap/cost-removal materialized
bal perf <scene>                                      # relative pass deltas (PASS_TIMING — triage only)
Tools\Tracy\tracy-capture.exe -o after.tracy -s 10 -f && Tools\Tracy\tracy-csvexport.exe after.tracy

# 6. Commit the phase (door defaults flipped ON only after the gate passes on the full matrix)
```

Run steps 1/3/4 across the **full test matrix**, not just Bistro. One clean launch per gpu-hang-safety before any relaunch on R1/R2/R4/R5.

---

## 7. Definition of done

- **PP1–PP3 shipped:** no mid-frame blocking GPU syncs remain (Tracy: one continuous graphics lane); Lumen denoise half-res (visual-parity gated); Lumen RefreshTransforms scales with moved-instance count.
- **R1 shipped:** a second compute queue overlaps GTAO + shadow-cull + the Lumen trace with graphics — **Tracy-proven overlap on two GPU lanes**, byte-identical, Lumen-stable, behind `BALLISTIC_DX12_ASYNC_COMPUTE`.
- **R2 + R3 shipped:** persistent SM6.6 bindless heap (no per-frame ring churn, no per-draw `CopyDescriptorsSimple`); the CPU per-submesh path at `DX12HDRenderer.cs:1367–1578` is gone — split-node, skinned (compute skinning), and mixed-shader all flow through the unified GPU-driven ExecuteIndirect path, byte-identical, behind kill-switches. P2/P3 confirmed obsolete (subsumed), not skipped.
- **R4 or R5 shipped (exactly one):** the chosen front-end rewrite lands with a Tracy-proven reduction in the measured residual bound, byte-identical (or explicit sign-off visual parity), behind its kill-switch with a working fallback.
- **Across all phases:** every perf phase byte-identical to the deterministic reference on the full matrix; every GI-adjacent phase passes the Lumen orbit-stability gate; each phase a separate reversible commit on `dx12-perf-radical`; no device-removal incident left unresolved; each radical feature keeps its kill-switch env door.
