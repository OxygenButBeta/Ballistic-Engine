# DX12 Renderer — Performance Execution Plan

**Date:** 2026-06-16
**Branch:** dx12-renderer (work in this branch; commit per phase)
**Companion doc:** [dx12-renderer-perf-analysis.md](dx12-renderer-perf-analysis.md) (the diagnosis)
**Goal:** Make the DX12 renderer efficient. Eliminate the ~40 per-frame GPU stalls, then clean
up CPU waste, then tune the genuinely GPU-bound passes against real measurements.

---

## The thesis (from the analysis)

The renderer is sync-bound, not GPU-bound or draw-bound. Every pass and every resource transition
calls `ExecuteSync` → `WaitForGpu()` (a full pipeline drain). A no-GI frame is ~30–40 serialized
GPU flushes with zero CPU/GPU overlap and zero pass overlap. Present then adds one more full
`dev.Flush()` before the flip. The GPU-driven geometry path and the shaders are fine — the
synchronization model is the problem, and it was a documented "fix later" shortcut
([Dx12Device.cs:152-154](../../BallisticEngine.DX12/Dx12Device.cs#L152-L154)).

**Verified blockers** (the only mid-frame CPU↔GPU deps that a single-submit frame must respect):
- OIDN **CPU readback** path — rare non-RDNA4 fallback, **GI-only** ([DX12HDRenderer.cs:2270](../../BallisticEngine.DX12/DX12HDRenderer.cs#L2270)).
- OIDN **HIP `ExecuteShared`** sequencing — **GI-only**, library sync not a D3D12 fence ([:2255](../../BallisticEngine.DX12/DX12HDRenderer.cs#L2255)).
- DXR acceleration-structure build — once per frame, not per pass ([Dx12SceneAS.cs:113](../../BallisticEngine.DX12/Resources/Dx12SceneAS.cs#L113)).

**Therefore the baseline no-GI frame has ZERO true mid-frame stalls** — every flush in it is pure
artifact and collapses into one submit. That is the entire P0 win, and it's the user's exact case
("slow even without GI").

---

## Non-negotiable guardrails (apply to EVERY phase)

1. **Byte-identical output is the success bar.** The renderer has a deterministic-capture contract
   (`BALLISTIC_SCREENSHOT_PAUSED=1` + `BALLISTIC_DETERMINISTIC=1` → frame N == frame M, diffable).
   Every phase must produce `meanError 0` vs the frozen baseline on the test scenes before commit.
2. **[gpu-hang-launch-safety] is absolute.** A pipelining bug = a GPU hang = a possible whole-PC
   TDR crash. On the FIRST device-removal: stop, make the build safe, commit the safe state,
   diagnose with DRED (`BALLISTIC_DX12_DEBUG=1`) — do **not** relaunch the hanging build in a loop.
3. **Keep a runtime kill-switch on the new path** (`BALLISTIC_DX12_PIPELINED=0` → old per-pass
   `ExecuteSync` path) until P0 is proven byte-identical AND stable across all test scenes. Delete
   the switch only after sign-off.
4. **Commit per phase**, each phase independently verified. Small, reversible steps.
5. **No mid-frame `WaitForGpu` may survive P0** except the documented GI-only OIDN/DXR ones.

### Test matrix (run all, every phase)

| Scene | Why |
|---|---|
| `Assets/Bistro_v5_2/BistroInterior_Wine.scene` | Enclosed, GPU-driven whole-mesh, shadows, many materials |
| `Assets/Bistro_v5_2/BistroExterior.scene` | Open, sky, GPU-driven, cascades |
| `Assets/LightTest/LightTest.scene` | Punctual/clustered lights stress |
| `Assets/TransparentTest/TransparentTest.scene` | Forward transparents path |
| `Assets/SkyTest/SkyTest.scene` | Procedural sky + IBL bake |

### Verification recipe (per phase, per scene)

```bash
# Baseline (freeze ONCE, before any code change — commit the .bmp files or stash them)
BALLISTIC_SCREENSHOT_PAUSED=1 BALLISTIC_DETERMINISTIC=1 \
  bal render <scene> --out base/<scene>.bmp

# After a phase:
BALLISTIC_SCREENSHOT_PAUSED=1 BALLISTIC_DETERMINISTIC=1 \
  bal render <scene> --out work/<scene>.bmp
bal imgdiff base/<scene>.bmp work/<scene>.bmp        # must be meanError 0
bal perf <scene>                                     # watch cpuFrameMs trend
```

`RenderStats.Scene.CpuFrameMs` ([set at :1808](../../BallisticEngine.DX12/DX12HDRenderer.cs#L1808))
currently *includes* all `WaitForGpu` time — it is the headline metric P0 must crush.

---

## Phase order at a glance

| Phase | What | Risk | Expected win | Gate |
|---|---|---|---|---|
| **P0a** | Single recorded list + barrier batching, 1 submit/frame (still 1 wait at end) | High | Huge (kills ~40 flushes) | byte-identical, stable |
| **P0b** | N-buffer allocators/fences + constant buffers → CPU/GPU overlap | High | Large (hides CPU cost under GPU) | byte-identical, no race |
| **P0c** | Present without full Flush (fence-gated backbuffer reuse) | Med | Removes the last per-frame stall | byte-identical, no tearing/corruption |
| **P1** | Coalesce/minimize barriers within the frame list | Low | Moderate | byte-identical |
| **P2** | Cache 35 env-var doors once at init | Trivial | Small CPU + GC | byte-identical |
| **P3** | Cache per-pass descriptor tables (rebuild on invalidation only) | Low | Small CPU | byte-identical |
| **P4** | Shadow/light CPU cleanup (frustum reuse, single light transform, cluster cache) | Low | Small CPU (scales w/ lights/casters) | byte-identical |
| **P5** | Re-measure GPU timeline; tune the actually-bound passes | Med | Data-driven; the real GPU budget | quality-judged + perf |
| **P6** | (optional) async compute, multi-threaded recording | High | Advanced; only if P0–P5 leave headroom on the table | byte-identical |

Do them in order. **P0 is ~80% of the total win.** Everything after P0 is only worth doing — and
only correctly measurable — once the frame is pipelined.

---

## P0 — Pipelined single-submit frame (THE fix)

This is the work the original author deferred. Split into three sub-phases so each is verifiable
and reversible. **Do not attempt all three at once.**

### P0a — One recorded command list, barriers batched, one submit per frame

**Intent:** Stop flushing between passes. Record the whole frame into one open command list; submit
once; keep exactly ONE `WaitForGpu` at the end (so it's still synchronous, but 1 stall instead of
40). This isolates *correctness of recording* from *pipelining*, which is where hangs hide.

**Mechanism:**
1. Add a frame command context to `Dx12Device` (alongside, not replacing, `ExecuteSync`):
   - `BeginFrameList()` → `allocator.Reset(); commandList.Reset(...)` once.
   - A `CurrentList` property the renderer/targets record into.
   - `EndFrameList()` → `Close(); ExecuteCommandList; WaitForGpu()` once.
2. Convert the transition helpers in `Dx12OffscreenTarget` / `Dx12GBuffer` from *submitters* to
   *recorders*: e.g. `ColorToShaderResource()` records a barrier into `CurrentList` instead of its
   own `ExecuteSync`. **Keep the existing `ResourceStates` tracking — it is correct; only the
   submission changes.** Guard them so the screenshot/asset/IBL paths can still call the old
   `ExecuteSync` form (overload or a `bool record` flag, or route through `CurrentList` when one is
   open and `ExecuteSync` when not).
3. Convert the pass wrappers (`RenderGeometry`, `RenderColorOnly`, `RenderColorOnlyCleared`,
   `RenderColorWithExternalDepth`, `RenderIntoCleared`, `CopyColorFrom`) the same way: record into
   `CurrentList` rather than open their own `ExecuteSync`.
4. In `BeginRender`: call `BeginFrameList()` at the top, record all passes, `EndFrameList()` at the
   bottom (before the stats writes). The DDGI/probe/RT passes (GI-only) can stay on `ExecuteSync`
   for P0a — they're outside the no-GI path; migrate them in P0a.2 once the baseline is proven.
5. **Batch adjacent barriers.** Where the old code did N sequential transition flushes, emit one
   `ResourceBarrier(span)`. The G-buffer already does this internally — extend the pattern.

**The two real hazards to watch:**
- **Barrier ordering:** with everything in one list, a barrier must still sit between the producer
  and consumer of each resource. The current per-pass transitions already encode the right order;
  preserve their *sequence*, just stop flushing. A missing barrier = read-before-write = garbage
  or hang. The byte-diff catches garbage; DRED catches hangs.
- **The OIDN readback (GI-only):** it genuinely needs the GPU result mid-frame. For P0a, when the
  readback path is taken, `EndFrameList()` early (submit+wait the work so far), do the readback,
  then `BeginFrameList()` again for the rest. I.e. the frame splits into ≤2 submits *only* when the
  rare non-HIP OIDN fallback runs. No-GI and RDNA4+HIP frames stay single-submit.

**Verify:** byte-identical on all 5 scenes; `cpuFrameMs` should drop dramatically (this is where
the win shows even before overlap). Stable over a few hundred frames (`bal simulate` / a short
editor session under the safety rule). Commit.

**Rollback:** `BALLISTIC_DX12_PIPELINED=0` keeps the old path compiled and selectable.

### P0b — Frame-in-flight: N-buffer allocators, fences, and constant buffers

**Intent:** Let the CPU record frame N+1 while the GPU renders frame N. This converts the single
end-of-frame wait from "CPU idles for the whole GPU frame" into "CPU only waits if it's >N frames
ahead." This is what actually hides CPU submission cost under GPU work.

**Mechanism:**
1. **N = 2 (matches the 2 backbuffers; 3 if measurement shows the CPU still stalling).** Add a
   `frameIndex` ring to `Dx12Device`:
   - `allocator[N]`, `fence` + `frameFenceValue[N]`.
   - At `BeginFrameList()`: `i = frameIndex % N`; wait on `frameFenceValue[i]` (the GPU finished
     that slot ≥1 frame ago — usually already done, no stall); `allocator[i].Reset()`.
   - At `EndFrameList()`: `ExecuteCommandList`; `Signal(fence, ++value)`; store value in
     `frameFenceValue[i]`; `frameIndex++`. **No `WaitForGpu` here anymore.**
2. **N-buffer every per-frame CPU-written constant buffer.** This is the subtle, mandatory part.
   Today `cbRing` ([:675](../../BallisticEngine.DX12/DX12HDRenderer.cs#L675)), `deferredCb`,
   `frameCb`, `motionCb`, `ssrCb`, `ssgiCb`, `ssaoCb`, `bloomCb`, `fogCb`, `transparentCb`,
   `compositeCb`, `lumCb`, `shadowCb`, `procSkyCb`/`skyCb`, `taaCb` are **single-instance UPLOAD
   buffers mapped once and overwritten every frame**. Safe only because the GPU finishes before the
   CPU rewrites. Once the CPU runs ahead, the CPU will stomp constants the GPU is still reading →
   visual corruption (and the byte-diff will scream). Fix: allocate each as `[N]` copies (or one
   buffer sized `N×`), index by `frameIndex % N`, write+bind the current slice. `cbRing` is already
   a per-draw ring — make it `N` rings or size it `N × cbSlotCount`.
   - **Also N-buffer the shader-visible descriptor heaps** that are `Reset()` + refilled per frame
     (`srvVisible`, `deferredSrvVisible`, `ssrSrvVisible`, etc.) for the same reason — the GPU of
     frame N may still be reading descriptors the CPU of frame N+1 would overwrite. Per-frame heap
     segments (offset by `frameIndex % N`) or per-frame heaps.
3. **Upload-thread interaction:** asset uploads use the separate `uploadGate`/`ExecuteUpload`
   path — leave it. But confirm no per-frame CB shares a resource with an upload. (They don't —
   uploads create their own resources.)

**The hazard to watch:** any per-frame-written GPU-read resource NOT N-buffered is a race. Audit
every `*Mapped` field and every `*SrvVisible.Reset()` site. Miss one → intermittent corruption
that the deterministic single-frame diff might NOT catch (it only renders one frame). So **also**
verify with a multi-frame motion capture: `bal render <scene> --orbit 8` (or `bal simulate` watch)
and diff several frames; and a short live editor session (safety rule) looking for flicker.

**Verify:** byte-identical single-frame AND multi-frame; `cpuFrameMs` now well under GPU frame
time; no flicker across frames. Commit. Tune N if the CPU still waits (perf stat).

### P0c — Present without a full GPU flush

**Intent:** Remove the last per-frame stall: `dev.Flush()` before every flip
([Dx12SwapChain.cs:116-117, 156-157](../../BallisticEngine.DX12/Dx12SwapChain.cs#L116-L157)).

**Mechanism:**
- The backbuffer copy + UI list should be recorded/submitted on the same fenced timeline as the
  frame, and the swapchain's flip-model already gates backbuffer reuse. Replace `Flush()` (full
  wait) with: signal the frame fence after the present command list, and gate the *next* frame's
  use of that backbuffer index on its fence (the N-buffer ring from P0b already does this for
  allocators — extend to backbuffers via `swapChain.CurrentBackBufferIndex` + per-backbuffer fence
  value). With vsync on, the present queue naturally paces; the goal is to stop the CPU blocking on
  GPU completion synchronously.
- Editor path (`PresentToScreen=false`): the ImGui pass samples `ldr` via SRV the SAME frame
  ([:1796-1797](../../BallisticEngine.DX12/DX12HDRenderer.cs#L1796-L1797)). Ensure the UI command
  list that samples it is ordered after the composite on the queue (it is, same queue) — no full
  flush needed, just correct fencing before the next frame reuses `ldr` (N-buffer or barrier).

**The hazard:** present/backbuffer reuse races are subtle and card/driver-dependent — this is the
most likely place for tearing or a 1-frame-stale display. Keep vsync on during bring-up. If P0c
proves fiddly, it's acceptable to ship P0a+P0b first (they capture most of the win) and treat P0c
as a follow-up — the per-frame `Flush()` is one stall, vs the ~40 P0a removes.

**Verify:** byte-identical; visually correct in a live editor session and the player; no tearing
with vsync. Commit. Then remove the `BALLISTIC_DX12_PIPELINED` kill-switch.

---

## P1 — Barrier minimization within the frame list

Once recording is unified, audit the barrier stream:
- Coalesce consecutive transitions on different resources into single `ResourceBarrier(span)` calls.
- Eliminate redundant round-trips (e.g. a resource transitioned to SRV then back to RT then SRV
  across adjacent passes that could share a state). The G-buffer's combined
  `PixelShaderResource | NonPixelShaderResource` state ([Dx12GBuffer.cs:138](../../BallisticEngine.DX12/Resources/Dx12GBuffer.cs#L138))
  is the right pattern — look for other resources that ping-pong.
- Verify no over-broad states that disable compression unnecessarily.

Low risk, falls out of P0. Verify byte-identical. Commit.

---

## P2 — Cache env-var doors once at init

35 `Environment.GetEnvironmentVariable` calls run per frame in `DX12HDRenderer.cs` (hashtable
lookup + string alloc + GC each). Read them ONCE in the constructor/`Initialize` into typed fields
(`bool`/enum). Hot path reads fields. Keep `PostFX.*` volume reads (already cheap field reads).

- Examples to migrate: `BALLISTIC_DX12_EXPOSURE` ([:1517](../../BallisticEngine.DX12/DX12HDRenderer.cs#L1517)),
  `BALLISTIC_DX12_RT_SHADOWS`/`_RT_GI`/`_SSGI`/`_RT_REFLECTIONS` ([:1707-1770](../../BallisticEngine.DX12/DX12HDRenderer.cs#L1707-L1770)),
  `BALLISTIC_DX12_SSAO` ([:1777](../../BallisticEngine.DX12/DX12HDRenderer.cs#L1777)),
  `BALLISTIC_FX_VOLUMETRIC` ([:1762](../../BallisticEngine.DX12/DX12HDRenderer.cs#L1762)), the
  `_HIZ_DEBUG`/`_SHADOW_CACHE_DEBUG` doors, etc.
- Note: a few are *meant* to be runtime-togglable for A/B. Those that the harness flips between
  runs (not within a run) are fine to read once at init. Document which stay live (if any).

Aligns with the project's own [no-reflection-in-hot-path] rule. Trivial, mechanical. Verify
byte-identical (env unset = same defaults). Commit.

## P3 — Cache per-pass descriptor tables

The deferred pass copies 13 SRVs into a visible heap every frame
([:1853-1868](../../BallisticEngine.DX12/DX12HDRenderer.cs#L1853-L1868)); SSR/SSGI/SSAO/bloom do
similar `CopyDescriptorsSimple` blocks. Most reference fixed resources (G-buffer, IBL, shadow map,
cluster buffers) that change only on resize / IBL re-bake / target realloc.

- Build each table ONCE; rebuild only on an invalidation event (resolution change, IBL bake,
  target reallocation, RT-shadow-mask presence flip).
- Bind by precomputed GPU handle each frame.
- **Interacts with P0b's N-buffered heaps** — do P3 after P0b so the caching respects the per-frame
  heap segments (cache N copies, or use a non-per-frame static heap for the truly-static tables and
  only N-buffer the dynamic descriptors).

Low risk. Verify byte-identical. Commit.

## P4 — Shadow & punctual-light CPU cleanup

- **Frustum planes:** extract the camera frustum once; reuse across cascades. Pre-allocate the
  shadow caster list (no per-frame `new List<...>` in `RenderShadows`
  [~:3397](../../BallisticEngine.DX12/DX12HDRenderer.cs#L3368)).
- **Light transforms:** transform punctual light positions to view space ONCE; today it's done in
  both `GatherPunctualLights` and again in `Dx12ClusteredLights.Cull`.
- **Cluster AABB grid:** rebuild only on projection/viewport change, not on camera *movement*
  (it's view-space, position-invariant).
- Confirm the cluster cull short-circuits when the light set is unchanged.

Only material at high light counts / many casters. Verify byte-identical. Commit.

## P5 — Re-measure, then tune the genuinely GPU-bound passes

**Only now** (frame is pipelined, CPU waste gone) capture a real GPU timeline:
- Use the engine's per-pass GPU timers (`RenderStats.Scene.GpuPasses`, gated by `GiTimingEnabled` /
  `TimePass(...)`) and/or RenderDoc for ground truth.
- Rank passes by actual GPU ms. Likely candidates from the shader review (verify, don't assume):
  volumetric fog march (full-res, up to 256 steps — [VolumetricFog.hlsl](../../BallisticEngine.DX12/Shaders/VolumetricFog.hlsl)),
  SSGI 8-slice gather (half-res — [Ssgi.hlsl](../../BallisticEngine.DX12/Shaders/Ssgi.hlsl)),
  SSR 32-step march, deferred PCF (9-tap), auto-exposure 32×32 reduce.
- Tuning levers (each behind a quality setting / volume param, each A/B'd for quality vs ms):
  fog step count + depth-aware downsample; SSGI slice/step counts; SSR step count + refine; PCF tap
  count or switch to a cheaper filter; move the luminance reduce to a compute parallel reduction;
  ensure every screen-space pass is at the intended res (no accidental full-res).
- **Do not pre-optimize against the estimates in the analysis doc** — those are guesses while the
  flushes mask reality. Measure first.

Quality-judged (render + eyeball on enclosed + exterior, per
[renderer-screenshot-verification]) AND perf-measured. Commit per tuning change.

## P6 — (Optional, only if headroom remains) Advanced parallelism

After P0–P5, if the GPU timeline shows serial dependencies that could overlap:
- **Async compute:** run independent compute (SSAO, Hi-Z build, light cull, bloom downsample) on a
  compute queue overlapping graphics. Needs a second queue + cross-queue fences. Higher complexity,
  driver-sensitive — only if measurement justifies it.
- **Multi-threaded command recording:** record passes into parallel command lists on JobSystem
  workers, execute as one batch. Only worth it if CPU recording (post-P0b) is still on the critical
  path, which it likely won't be on this GPU.

Defer unless P5 data demands it. These add real complexity and hang surface for diminishing returns
once the frame is already pipelined.

---

## Definition of done

- No-GI frame: **1 submit, ≤1 fence wait per frame** (vs ~40 today); `cpuFrameMs` dominated by
  actual recording, not GPU waits; CPU overlaps GPU (frame N+1 records during frame N).
- All 5 test scenes byte-identical to the frozen baseline, single- and multi-frame.
- No GPU hangs across a sustained editor session and a player run.
- A real GPU timeline captured; the top GPU-bound passes tuned to a sensible quality/ms budget.
- Kill-switch removed; analysis + this plan updated with the measured before/after `cpuFrameMs`.

## Sequencing note for execution

Recommended commit cadence: P0a → (prove) → P0b → (prove) → P0c → P1 → P2 → P3 → P4 → P5 (iterative).
P2 is so cheap and independent it can be done first as a warm-up if desired (it touches only
env-var reads, no sync model), but it's a small win — P0 is the prize and should not wait behind
anything. Keep each phase a separate, reverted-if-needed commit on `dx12-renderer`.
