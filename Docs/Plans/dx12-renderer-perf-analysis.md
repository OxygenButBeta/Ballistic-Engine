# DX12 Renderer Performance Analysis & Action Plan

**Date:** 2026-06-16
**Branch:** dx12-renderer
**Scope:** Why the DX12 renderer is slow even *without* GI, on an RX 9070 XT (high-end).
**Status:** Analysis only — no code changed. Action plan below, ordered by impact.

---

## TL;DR — the one bug that matters

The renderer **flushes the entire GPU pipeline (submit a command list + `WaitForGpu()`) for
every single render pass AND every single resource-state transition.** A normal no-GI frame does
**40–60+ full GPU stalls back to back.** The GPU sits idle between every pass; the CPU sits idle
inside every `WaitForGpu`. There is *zero* CPU/GPU overlap and *zero* pass overlap. This is a
classic GL→DX12 port artifact and it is, by a wide margin, the dominant cost. Everything else in
this document is a rounding error next to it.

This was a **known, documented shortcut**. [Dx12Device.cs:152-154](../../BallisticEngine.DX12/Dx12Device.cs#L152-L154):

> *"Synchronous is exactly right for the offscreen screenshot path (deterministic, simple); the
> real per-frame loop **will pipeline with multiple allocators + fences later**."*

That "later" never happened. The whole interactive renderer still runs on the screenshot-grade
synchronous submit path.

---

## Evidence

### 1. `ExecuteSync` = full GPU flush, per call

[Dx12Device.cs:163-172](../../BallisticEngine.DX12/Dx12Device.cs#L163-L172):

```csharp
public void ExecuteSync(Action<ID3D12GraphicsCommandList4> record) {
    lock (submitGate) {
        allocator.Reset();
        commandList.Reset(allocator, null);
        record(commandList);
        commandList.Close();
        Queue.ExecuteCommandList(commandList);
        WaitForGpu();          // <-- BLOCKS the CPU until the GPU fully drains
    }
}
```

Every call: reset one shared allocator, record, close, submit a one-shot command list, then
**block the CPU on a fence until the GPU finishes everything**. One allocator, one list, one
fence — no double/triple buffering, so the CPU can never get ahead of the GPU.

### 2. Every pass *and* every transition is its own `ExecuteSync`

[Dx12OffscreenTarget.cs](../../BallisticEngine.DX12/Dx12OffscreenTarget.cs) — even a bare
state transition is a full submit+flush:

```csharp
public void ColorToShaderResource()  => dev.ExecuteSync(cl => TransitionTo(cl, PixelShaderResource));   // :178
public void ColorToRenderTarget()    => dev.ExecuteSync(cl => TransitionTo(cl, RenderTarget));          // :181
public void ColorToUnorderedAccess() => dev.ExecuteSync(cl => TransitionTo(cl, UnorderedAccess));       // :189
public void DepthToShaderResource()  => dev.ExecuteSync(...);                                           // :194
```

Same in [Dx12GBuffer.cs](../../BallisticEngine.DX12/Resources/Dx12GBuffer.cs):
`ToShaderResource` ([:139](../../BallisticEngine.DX12/Resources/Dx12GBuffer.cs#L139)),
`DepthToReadOnly` ([:150](../../BallisticEngine.DX12/Resources/Dx12GBuffer.cs#L150)),
`DepthToNonPixelShaderResource` ([:154](../../BallisticEngine.DX12/Resources/Dx12GBuffer.cs#L154))
are each a standalone flush — a transition that in DX12 should cost ~nothing (one barrier batched
into the next command list) instead drains the whole pipe.

There are **99 flush-causing call sites in `DX12HDRenderer.cs` alone**, plus ~22 in
`Dx12OffscreenTarget` and ~10 in `Dx12GBuffer`.

### 3. A minimal no-GI frame — counted flushes

Tracing [DX12HDRenderer.BeginRender](../../BallisticEngine.DX12/DX12HDRenderer.cs#L1455) with
**GI off, SSR off, fog off** (the user's "even without GI" case):

| Step | File:line | Flushes |
|---|---|---|
| Shadows (cascade depth) | [RenderShadows](../../BallisticEngine.DX12/DX12HDRenderer.cs#L3368) | 1 (when re-rendered) |
| Hi-Z depth → non-pixel SRV | [:1575](../../BallisticEngine.DX12/DX12HDRenderer.cs#L1575) | 1 |
| Hi-Z pyramid build | [BuildHiZ](../../BallisticEngine.DX12/Resources/Dx12GpuDrivenRenderer.cs#L309) | 1 |
| G-buffer geometry | [RenderGeometry](../../BallisticEngine.DX12/Resources/Dx12GBuffer.cs#L117) | 1 |
| G-buffer → shader resource | [:1700](../../BallisticEngine.DX12/DX12HDRenderer.cs#L1700) | 1 |
| Deferred lighting | [:1870](../../BallisticEngine.DX12/DX12HDRenderer.cs#L1870) | 1 |
| Depth → read-only | [:1716](../../BallisticEngine.DX12/DX12HDRenderer.cs#L1716) | 1 |
| Sky | [:1717](../../BallisticEngine.DX12/DX12HDRenderer.cs#L1717) | 1 |
| Transparents (+ its own transitions) | [DrawTransparents](../../BallisticEngine.DX12/DX12HDRenderer.cs#L1887) | 2–3 |
| TAA (+ color transitions) | [DrawTaa](../../BallisticEngine.DX12/DX12HDRenderer.cs#L2009) | 2–3 |
| SSAO: main + blurH + blurV + 3 transitions | [DrawSsao](../../BallisticEngine.DX12/DX12HDRenderer.cs#L3137) | **~6** |
| Bloom: bright + blurH + blurV + 4 transitions | [DrawBloom](../../BallisticEngine.DX12/DX12HDRenderer.cs#L3180) | **~7** |
| Auto-exposure (lum reduce) | DrawComposite | 1–2 |
| Composite | [DrawComposite](../../BallisticEngine.DX12/DX12HDRenderer.cs#L3210) | 1–2 |
| ldr → shader resource (editor) | [:1797](../../BallisticEngine.DX12/DX12HDRenderer.cs#L1797) | 1 |

**~30–40 full GPU flushes for the cheapest possible frame.** Turn on SSGI/SSR/fog and it's 60+.
SSAO alone — *one* conceptual screen-space effect — is **6 separate GPU drains**
([:3149](../../BallisticEngine.DX12/DX12HDRenderer.cs#L3149),
[:3160](../../BallisticEngine.DX12/DX12HDRenderer.cs#L3160),
[:3172-3174](../../BallisticEngine.DX12/DX12HDRenderer.cs#L3172-L3174)).

**Why this is so expensive:** each `WaitForGpu` forces the GPU to finish *and go idle*, then the
CPU records the next ~5 µs of commands while the GPU does nothing, then submits and the CPU goes
idle. At 40 flushes/frame with even a conservative ~150–300 µs round-trip each (submit overhead +
fence signal latency + lost overlap), that's **6–12 ms of pure bubble** burned on synchronization
before a single useful shader cycle. On a fast card the GPU work is cheap, so the *stalls
dominate the frame* — which is exactly the symptom: "expensive even on an RX 9070 XT."

### 4. The FSR path adds an *explicit* second stall layer

[RunFsr / RunFsr:2069](../../BallisticEngine.DX12/DX12HDRenderer.cs#L2063-L2076) wraps the FSR
dispatch in its own `ExecuteSync` → another full flush, on top of the ColorToShaderResource /
ColorToUnorderedAccess transitions around it (4 flushes for one upscale).

### 5. Secondary CPU costs (real, but small next to the flushes)

- **35 `Environment.GetEnvironmentVariable` calls per frame** in `DX12HDRenderer.cs`
  ([:1517](../../BallisticEngine.DX12/DX12HDRenderer.cs#L1517),
  [:1707](../../BallisticEngine.DX12/DX12HDRenderer.cs#L1707),
  [:1736-1737](../../BallisticEngine.DX12/DX12HDRenderer.cs#L1736-L1737),
  [:1762](../../BallisticEngine.DX12/DX12HDRenderer.cs#L1762),
  [:1769-1777](../../BallisticEngine.DX12/DX12HDRenderer.cs#L1769-L1777), …). Each is a
  process-environment hashtable lookup + string alloc. ~tens of µs/frame total, plus GC pressure.
  Should be read once at init and cached as fields/enums. (Violates the project's own
  [no-reflection-in-hot-path] preference in spirit.)
- **Per-cascade frustum re-extraction** in the shadow loop
  ([RenderShadows](../../BallisticEngine.DX12/DX12HDRenderer.cs#L3368)) — `ExtractFrustumPlanes`
  (6-plane normalize) re-run for each of 4 cascades + a fresh `List<...>` allocated per frame.
- **Deferred lighting copies 13 SRVs into a visible heap every frame**
  ([:1853-1868](../../BallisticEngine.DX12/DX12HDRenderer.cs#L1853-L1868)) even though most never
  change frame-to-frame. Minor (`CopyDescriptorsSimple` is cheap-ish) but pure waste.
- **Punctual light view-space transforms done twice** (gather, then again in `Cull`) and the
  cluster-AABB grid rebuilt whenever the camera moves at all, not just on projection change
  (`Dx12ClusteredLights`). Only matters with many lights.

### 6. What is NOT the problem (verified — don't waste time here)

- **GPU-driven geometry path is good.** Compute cull + `ExecuteIndirect` + bindless share ONE
  command list (the geometry pass's single `ExecuteSync`); cull dispatch → barrier → indirect
  draw are correctly batched ([Dx12GpuDrivenRenderer.RenderInto:391-429](../../BallisticEngine.DX12/Resources/Dx12GpuDrivenRenderer.cs#L391-L429)).
  Hi-Z and per-cascade GPU shadow cull are likewise batched within their lists. The draw-call
  *count* is already collapsed (Bistro ~1600 submeshes → a handful of indirect draws).
- **OIDN is not the baseline cost.** The zero-copy GPU path is the default on RDNA4+HIP
  ([:2240-2259](../../BallisticEngine.DX12/DX12HDRenderer.cs#L2240-L2259)); the catastrophic CPU
  readback ([:2267-2275](../../BallisticEngine.DX12/DX12HDRenderer.cs#L2267-L2275)) only triggers
  as a fallback, and only when GI is on — which the user explicitly excluded.
- **Shader inner loops are normal.** SSGI 8-slice gather, SSR 32-step march, fog march, 3×3 TAA
  clamp, 9-tap PCF, 32×32 lum reduce — all standard, all sized reasonably, mostly half-res. They
  cost real GPU ms but they are *not* why a near-empty frame is slow. Fix the flushes first, then
  re-measure these against a real GPU timeline.

---

## Action Plan (ordered by impact ÷ effort)

> Guardrails for all of this: **byte-identical output is the success bar** (the renderer has a
> deterministic-capture contract — `BALLISTIC_SCREENSHOT_PAUSED=1` + `BALLISTIC_DETERMINISTIC=1`
> → frame N == frame M). Verify every step with `bal render` + `bal imgdiff` against a frozen
> baseline before committing. **Respect [gpu-hang-launch-safety]:** do not repeatedly relaunch a
> hanging build; on first device-removal, make safe + commit + diagnose via DRED, don't relaunch.

### ★ P0 — Single-command-list frame (THE fix). Highest impact by 10×.

Collapse the whole frame into **one** recorded command list submitted **once**, with **per-frame
double/triple buffering** so the CPU records frame N+1 while the GPU renders frame N. This is the
work the original author deferred.

Concretely:
1. **Add a frame-graph-lite command context.** Replace the `ExecuteSync`-per-pass model with a
   single `ID3D12GraphicsCommandList` opened at `BeginRender` and closed/submitted once at the
   end. Every pass records into *that* list instead of calling its own `ExecuteSync`.
2. **Turn the transition helpers into recorders, not submitters.** `ColorToShaderResource()` etc.
   should *record a barrier into the current frame list* (and ideally batch consecutive barriers
   into one `ResourceBarrier` call), never submit. Keep the existing `ResourceStates` tracking —
   it's correct, it just needs to stop flushing.
3. **N-buffer the per-frame allocator + upload ring + fence.** 2–3 command allocators, round-robin
   per frame index, each fenced; the CPU only waits on allocator[frame % N] which the GPU finished
   ≥1 frame ago. This is what unlocks CPU/GPU overlap. The upload constant-buffer ring
   (`cbRing`, `deferredCb`, etc.) must also be N-buffered or guarded so the CPU doesn't overwrite
   constants the GPU is still reading.
4. **Keep `ExecuteSync` ONLY for**: asset uploads on worker threads (already a separate
   `uploadGate`/`ExecuteUpload` path — leave it), IBL bake (runs rarely), and the headless
   screenshot path (`bal render`, deterministic, one-shot — fine to stay synchronous).

Expected result: **~40 stalls → 1 submit/frame.** This alone should move the frame from
sync-bound to GPU-bound. Everything below is only worth measuring *after* this lands.

Risk/scope: this is the big one — touches `Dx12Device`, `Dx12OffscreenTarget`, `Dx12GBuffer`, and
every pass in `DX12HDRenderer`. Do it incrementally: introduce the persistent frame list + barrier
batching first (still one submit at end), verify byte-identical, then add N-buffering for overlap.

### P1 — Batch barriers within the frame list

Once passes record into one list, coalesce adjacent transitions into single `ResourceBarrier`
calls (e.g. the G-buffer's 4 RTs + depth in one batch — already done inside `RenderGeometry`/
`ToShaderResource`, extend the pattern everywhere). Fewer barriers = fewer GPU cache flushes /
pipeline stalls *within* the now-single submit. Free win, falls out of P0 naturally.

### P2 — Cache env-var doors & static flags once at init

Read all 35 `BALLISTIC_DX12_*` env vars **once** in the constructor / `Initialize` into typed
fields (bool/enum). The per-frame hot path then reads fields. Removes the per-frame hashtable
lookups + string allocs (GC pressure). Trivial, mechanical, zero behavior change. Keep volume-
driven toggles (`PostFX.*`) as-is — those are cheap field reads already.

### P3 — Cache deferred-lighting + post-pass descriptor tables

The 13 deferred SRVs ([:1853-1868](../../BallisticEngine.DX12/DX12HDRenderer.cs#L1853-L1868)) and
the similar per-pass `CopyDescriptorsSimple` blocks (SSR, SSGI, SSAO, bloom) mostly reference
fixed resources. Build each table **once** (and rebuild only on resize / IBL re-bake / target
realloc), bind by GPU handle each frame. Removes dozens of descriptor copies/frame. Small but easy
after P0.

### P4 — Shadow & light CPU cleanup

- Extract camera frustum planes once, reuse across cascades; pre-allocate the caster list (no
  per-frame `new List`).
- Cache punctual-light view-space positions; transform once (not in both gather and `Cull`).
- Rebuild the cluster-AABB grid only on projection/viewport change, not on camera movement.

Only matters at high light counts / many casters; defer until P0 re-measurement shows it.

### P5 — Re-measure GPU-bound passes, THEN tune shaders

After P0, capture a real GPU timeline (the engine already has per-pass GPU timers —
`RenderStats.Scene.GpuPasses`, gated by `GiTimingEnabled`; or RenderDoc). *Then* decide whether
fog step count, SSGI slices, SSR steps, or full-res vs half-res need tuning. Do not pre-optimize
shaders against guessed numbers — the flushes are masking the real GPU costs right now.

---

## Suggested verification recipe per step

```
# Freeze a baseline first (pick an enclosed + an exterior scene)
BALLISTIC_SCREENSHOT_PAUSED=1 BALLISTIC_DETERMINISTIC=1 bal render Assets/Bistro_v5_2/BistroInterior_Wine.scene --out base_interior.bmp
bal render Assets/Bistro_v5_2/BistroExterior.scene --out base_exterior.bmp

# After each change: same captures, diff against baseline — must be byte-identical (meanError 0)
bal imgdiff base_interior.bmp new_interior.bmp
bal perf Assets/Bistro_v5_2/BistroInterior_Wine.scene     # cpuFrameMs should drop hard after P0
```

For the CPU-stall win specifically, watch `RenderStats.Scene.CpuFrameMs`
([set at :1808](../../BallisticEngine.DX12/DX12HDRenderer.cs#L1808)) — it currently includes all
the `WaitForGpu` time, so P0 should slash it.

---

## Bottom line

The renderer isn't slow because of expensive shaders or too many draws — the GPU-driven path
already fixed draw counts, and the shaders are conventional. It's slow because it was ported from
GL by mapping every implicit GL state change to an **explicit submit-and-fully-wait**, so the
frame is ~40 serialized GPU flushes with no overlap. Build the single-list, N-buffered frame loop
(P0) the original author always intended, and the rest is cleanup.
