# DX12 Renderer — Correctness + Performance Execution Plan

**Date:** 2026-06-16 (rev 2 — scope expanded to correctness after the sky-atmosphere + animation
merges and the user's noise/sparkle/bright-spot report)
**Branch:** dx12-renderer (work here; commit per phase)
**Companion doc:** [dx12-renderer-perf-analysis.md](dx12-renderer-perf-analysis.md) (the perf diagnosis)
**Goal:** First make the DX12 image **correct** (the port broke things: noise, random sparkles,
shiny spots in dim areas), validating every system against proper DX12 semantics and rewriting
where the port is wrong. Then make it **efficient** (the ~40-stalls-per-frame sync model). Both
are required before Lumen GI is testable.

> **HARD CONSTRAINT — DO NOT TOUCH LUMEN.** The user implemented Lumen GI and it is off-limits:
> no edits to the DDGI / screen-probe / world-radiance-cache *algorithm* (DdgiTrace/DdgiBlend/
> DdgiGather, ScreenProbe*, DxrGi closest-hit shading, Dx12Ddgi/Dx12ScreenProbe). We validate and
> fix everything Lumen *depends on and composites with* — exposure, IBL, temporal/history,
> motion vectors, denoise sequencing — specifically so Lumen stays **ghosting-free and stable**.
> Any fix that touches a temporal buffer Lumen reads must be proven not to destabilize it.

---

## Why the scope grew

Since the last plan, `sky-atmosphere` (60fec51e) and `animation-system` (d38cb473) were **merged
into dx12-renderer**. The sky merge rewrote the exposure/tonemap chain (Composite.hlsl +187,
LumAverage.hlsl, new AerialPerspective/SkyTransmittance/Dx12SkyLuts). Merges of in-progress
branches commonly leave the DX12 path half-wired. The user now reports the image is **noisy, has
random crawling sparkles, and shiny spots in fully dim areas** — "it wasn't like this." That is a
**correctness regression**, and it must be fixed before (and partly alongside) the perf work,
because a broken image makes Lumen impossible to evaluate.

## Methodology — empirical isolation, NOT trust-the-report

A first validation sweep (4 parallel audits) produced a ranked list of suspects, but several
high-confidence "BUG" verdicts did **not survive direct code re-reading** (e.g. the "deferred
attenuation = 10000× firefly" used a wrong arithmetic example; the "SSGI temporal missing
sanitize" is actually guarded at lines 164/190/208; the "aerial perspective 85% root cause" is a
*conditional* magnitude issue, not the smoking gun, and the pass is `discard`-gated). The project's
own history warns that bug-hunt agents give "dramatic FALSE POSITIVES" — **always verify a claim
against real code + callers before fixing** ([editor-bug-sweep-2026-06]).

**Therefore every suspect below is tagged CONFIRMED / PLAUSIBLE / REFUTED, and the plan's first
phase is to EMPIRICALLY isolate which suspects actually produce the on-screen symptoms** using the
engine's existing A/B doors and the GI-isolate / deterministic-capture harness — before changing
any shader. We fix what the isolation proves, in priority order, re-capturing after each fix.

---

## The symptom → suspect map (from validation; verdicts are MINE after re-reading the code)

| # | Symptom | Suspect | File:line | Verdict | Note |
|---|---|---|---|---|---|
| S1 | Shiny spots in dim areas, near point-light gizmos (see screenshots) | Punctual inverse-square near-field spike (no near clamp; `t²` window doesn't cap close range) | [DeferredLighting.hlsl:132-137](../../BallisticEngine.DX12/Shaders/DeferredLighting.hlsl#L132-L137) | **PLAUSIBLE** | Spikes only sub-cm from a light; comment says "GL parity" so may be pre-existing, not a port regression. Isolate first. |
| S2 | Shiny spots in dim areas | IBL **prefilter** clamp (16384) ≫ **irradiance** clamp (500): sun disk leaks into specular reflection on rough/dim surfaces | [IblBake.hlsl:56 vs :112](../../BallisticEngine.DX12/Shaders/IblBake.hlsl#L56), [ProceduralSky.hlsl sun disk ~60000] | **PLAUSIBLE** | Asymmetric clamp is real; whether it reads as "bright spots" needs the GI-isolate / IBL-off A/B. |
| S3 | Noise / crawling sparkles | Temporal firefly feedback in SSGI/GI resolve; final `accumulated` not re-Sanitized before store | [Ssgi.hlsl:213-220](../../BallisticEngine.DX12/Shaders/Ssgi.hlsl#L213-L220) | **PARTLY REFUTED** | Inputs ARE sanitized (164/190/208) + pre/post firefly caps exist. Residual risk low but a final `Sanitize` is cheap insurance. Not the obvious cause. |
| S4 | Sparkles in reflections | DDGI-field divide returns `sum/wsum` un-sanitized when all probes near-inactive | [DxrReflections.hlsl:175](../../BallisticEngine.DX12/Shaders/DxrReflections.hlsl#L175) | **PLAUSIBLE** | RT-only path (reflections). Touches a Lumen-adjacent read — fix the *guard*, not the algorithm. |
| S5 | Raw/ugly + bright haze in dim interiors | Aerial-perspective inscatter magnitude vs scene radiance; `Exposure` constant declared but unused | [AerialPerspective.hlsl:19,78-90](../../BallisticEngine.DX12/Shaders/AerialPerspective.hlsl#L78-L90), [DX12HDRenderer.cs Exposure=1f] | **PLAUSIBLE (conditional)** | Pass is `discard`-gated on depth + `Strength>0`. Design intends raw-radiance space shared with composite. Suspect only if `Strength>0` by default OR `SkyTint` magnitude is off. Isolate with AP off. |
| S6 | Noise persists with denoiser | OIDN fail-fallback may feed undenoised history to combine | [DX12HDRenderer.cs ~:2259-2275](../../BallisticEngine.DX12/DX12HDRenderer.cs#L2259-L2275) | **PLAUSIBLE** | GI-only; only on HIP failure. Low priority unless isolation shows OIDN failing on the RX 9070 XT. |
| — | (verified OK) | Motion vectors (unjittered, frame-1 safe), TAA variance-clip + disocclusion + luma-adaptive feedback, jitter/unjitter, NaN-scrub ternary compliance in TAA/SSGI/DxrGi | Taa.hlsl, DX12HDRenderer.cs:1498/1685 | **OK** | Don't churn these — they passed. |

**Reading of the screenshots:** the bright blobs sit right at the small point-light sphere gizmos
in otherwise dim geometry → S1 and S2 are the leading candidates for "shiny spots in dim areas";
S3/S4 for the "crawling sparkles." Isolation (Phase V0) decides.

---

## Non-negotiable guardrails (EVERY phase)

1. **[gpu-hang-launch-safety] is absolute.** Never repeatedly relaunch a hanging build — a TDR
   hard-crashed the user's PC before. On first device-removal: stop, make safe, commit, diagnose
   with DRED (`BALLISTIC_DX12_DEBUG=1`), verify headlessly — do not relaunch in a loop. Check in
   after a crash.
2. **Deterministic-capture is the oracle.** `BALLISTIC_SCREENSHOT_PAUSED=1` +
   `BALLISTIC_DETERMINISTIC=1` → diffable frames. Correctness fixes will INTENTIONALLY change
   pixels — so for those, the bar is "the targeted symptom is gone AND nothing else regressed
   (eyeball enclosed + exterior + the GI-isolate view)", judged per [renderer-screenshot-verification].
   Perf phases keep the byte-identical bar.
3. **Lumen stability gate.** After any change to a temporal/history/exposure/IBL path, run the
   motion-stability check (the GI orbit harness, `GI_ORBIT` / `bal render --orbit N`) and confirm
   **no new ghosting or sparkle growth** with Lumen on. This is the user's explicit ask.
4. **Validate against real DX12 semantics, rewrite if the port is wrong.** For each system, the
   question is not "does it run" but "is this correct for D3D12" — coordinate conventions (RH, z∈[0,1],
   row-major transpose-on-upload), descriptor/heap lifetime, resource states, fp16 clamps, sampler
   addressing. Where the GL idiom was carried over wrongly, rewrite the pass cleanly rather than patch.
5. **Commit per phase**, smallest reversible steps. Keep the `BALLISTIC_DX12_PIPELINED=0`
   kill-switch through all of P-series until signed off.

### Test matrix (run all, every phase)

| Scene | Exercises |
|---|---|
| `Assets/Bistro_v5_2/BistroInterior_Wine.scene` | Dim interior + point lights = the S1/S2 symptom scene |
| `Assets/LightTest/LightTest.scene` | Punctual/clustered light stress (isolate S1) |
| `Assets/Bistro_v5_2/BistroExterior.scene` | Sky + aerial perspective + cascades (isolate S5) |
| `Assets/SkyTest/SkyTest.scene` | Procedural sky + IBL bake (isolate S2) |
| `Assets/CornellBox/CornellBox.scene` | GI-isolate ground truth (Lumen on/off) |
| `Assets/TransparentTest/TransparentTest.scene` | Forward path regression guard |

### A/B isolation doors (the empirical toolkit — already in the engine)

`BALLISTIC_DX12_SSGI=0` (GI off) · `BALLISTIC_DX12_RT_GI` / `_RT_SHADOWS` / `_RT_REFLECTIONS` ·
`BALLISTIC_FX_SSGI/SSR/VOLUMETRIC/SSAO=0|1` · `BALLISTIC_DX12_SSGI_OIDN=0` (denoise off) ·
`BALLISTIC_FX_SSGI_DEBUG` / GI-isolate view · `BALLISTIC_DX12_TONEMAP=aces` ·
`BALLISTIC_DX12_EXPOSURE=<v>` · `BALLISTIC_DETERMINISTIC=1` (TAA/SSGI/volumetric off, fixed exposure).
Plus per-suspect doors we may add temporarily (e.g. force aerial-perspective off) — remove before commit.

---

## Phase plan at a glance

| Phase | What | Gate |
|---|---|---|
| **V0** | **Empirical symptom isolation** — bisect noise/sparkles/bright-spots to specific passes via A/B doors. No code changes. | A ranked, *proven* defect list |
| **V1** | **Exposure / EV / tonemap chain** — validate + fix the merge-disturbed exposure pipeline end to end | Correct, stable exposure; Manual==Auto sanity |
| **V2** | **Lighting correctness** — punctual attenuation, specular firefly bounds, normal/TBN, energy | Bright-spots gone; no fireflies; PBR sane |
| **V3** | **IBL / sky / atmosphere** — prefilter/irradiance clamps, aerial-perspective wiring, fp16 safety | No leaked sun-disk specular; haze correct |
| **V4** | **Temporal & denoise infra (Lumen-adjacent, NOT Lumen)** — TAA/SSGI/SSR resolve, motion, OIDN sequencing, NaN guards | No crawling sparkles; Lumen ghost-free |
| **V5** | **Per-system DX12-semantics audit** — sweep every remaining pass for port-correctness | Each pass certified correct-for-DX12 |
| **P0** | **Pipelined single-submit frame** — kill the ~40 per-frame GPU stalls (3 sub-phases) | byte-identical, no hang, CPU↔GPU overlap |
| **P1–P4** | CPU cleanup — barrier batching, env-var caching, descriptor caching, shadow/light | byte-identical |
| **P5** | Re-measure GPU timeline, tune the genuinely-bound passes | quality + perf |
| **P6** | (optional) async compute / threaded recording | byte-identical |

**Order rationale:** correctness (V) before performance (P) — the user can't test Lumen on a
broken image, and chasing perf on a buggy renderer wastes effort. Within V, exposure first (it
gates *everything* visual and is where the merge hit hardest), then lighting (the bright spots),
then IBL/sky, then temporal (the sparkles), then a full DX12-semantics sweep. Then the perf series
from the original plan (unchanged in substance — summarized at the end).

---

## V0 — Empirical symptom isolation (no code changes)

Pin each symptom to a pass before touching anything. Capture the symptom scenes under each door
and diff/eyeball. Deliverable: a table mapping each visible defect → the pass that owns it.

1. **Baseline capture** (current state) of all 6 scenes, both `BALLISTIC_DETERMINISTIC=1` (clean
   reference: TAA/SSGI/volumetric off, fixed exposure) and full-FX.
2. **GI on vs off** (`BALLISTIC_DX12_SSGI=0`): do the sparkles persist with GI off? If they vanish
   → S3/S4/S6 (GI resolve). If they remain → they're in lighting/IBL/temporal-TAA (S1/S2).
3. **Each post effect off in turn** (`BALLISTIC_FX_SSR/SSGI/VOLUMETRIC/SSAO=0`, TAA off via
   deterministic): which toggle removes the sparkles? Which removes the bright spots?
4. **IBL contribution**: capture GI-isolate view + an IBL-off A/B (temporary door if needed) — do
   the dim-area bright spots track IBL specular (S2) or punctual lights (S1)?
5. **Aerial perspective**: is `Strength>0` in these scenes' volumes? Capture with AP forced off —
   does the "raw/ugly haze" change (S5)?
6. **Exposure sanity**: capture Manual EV vs Automatic; do they roughly agree? Is Automatic
   flickering (no eye-adaptation EMA)? Note absolute brightness vs the GL baseline.
7. **OIDN**: confirm the zero-copy path is active on the RX 9070 XT (the `[OIDN] ... ZERO-COPY`
   log line) — if it's falling back to READBACK, S6 is live and also a perf cliff.

**Output:** the proven defect list, each tagged to a pass, ordered by how much of the symptom it
owns. V1–V4 then fix *only what V0 proved*, in that order. (The suspects above are the hypothesis;
V0 is the experiment.)

## V1 — Exposure / EV / tonemap chain (highest visual leverage; merge hit here hardest)

Validate the full HDR→display transform end to end and make it correct + stable. The user flagged
exposure/EV explicitly ("aşırı dikkat").

- **Verify the raw-radiance invariant.** LumAverage/Composite assume the DX12 HDR target holds
  ABSOLUTE radiance (no pre-exposure), unlike GL. Confirm NOTHING pre-exposes before the meter, and
  that the sun/punctual radiance magnitudes are physically consistent across deferred, sky, fog, AP.
  A ~16-stop inconsistency anywhere = wrong exposure. ([LumAverage.hlsl header](../../BallisticEngine.DX12/Shaders/LumAverage.hlsl#L7-L11))
- **Manual vs Automatic parity.** Both must resolve `LegacyMul/(1.2·2^(EV−comp))` consistently with
  the Exposure volume's dial/limits. Test a fixed-EV scene against the metered EV — they should land
  near the same brightness. Fix any unit/sign mismatch.
- **Auto-exposure stability.** Add the eye-adaptation EMA (temporal smoothing of metered EV) that
  LumAverage's header says is a "follow-up" — without it, Automatic mode can flicker frame-to-frame,
  which also destabilizes anything downstream. Keep it deterministic-capture-safe (EMA frozen under
  `BALLISTIC_DETERMINISTIC`).
- **Tonemap.** AgX path looks correct (proper log2 range, sRGB OETF, NaN-safe sharpen). Confirm the
  ACES A/B door still works; confirm grading (contrast/saturation/vignette/CA/grain) ordering is
  display-correct. Don't over-churn — this part read clean.
- **Lumen tie-in:** exposure changes alter the radiance scale Lumen's temporal accumulation sees.
  After V1, re-run the Lumen stability gate.

Gate: exposure correct and stable; Manual≈Auto; no flicker; brightness in the right neighbourhood
vs the GL baseline. Commit.

## V2 — Lighting correctness (the bright spots)

Fix whatever V0 proved owns the "shiny spots in dim areas."

- **Punctual attenuation (S1).** If isolation implicates it: add a near-field clamp so a light can't
  produce a super-physical spike on a sub-cm-close surface, while keeping the smooth range window.
  Verify against the GL formula — if GL had the same behavior this is a *tuning* fix, not a port bug;
  either way bound it. ([DeferredLighting.hlsl:132-137](../../BallisticEngine.DX12/Shaders/DeferredLighting.hlsl#L132-L137))
- **Specular firefly bounds.** Roughness floor (0.045) is present and correct; GGX denominators have
  `+1` / `EPS` guards. If specular still spikes, clamp the specular term's contribution (luma cap)
  rather than loosening the BRDF. Confirm perceptual→linear roughness (α=rough²) is right (it is).
- **Spot cone.** Guarantee `cosInner ≥ cosOuter` CPU-side so the cone falloff can't invert
  ([DeferredLighting.hlsl:151-156](../../BallisticEngine.DX12/Shaders/DeferredLighting.hlsl#L151-L156)).
- **Normal/TBN + BC5.** Confirm world-normal decode (`g1·2−1`, normalized) and the BC5 Z-reconstruct
  in the G-buffer write are correct (validation said OK — spot-check, don't churn).
- **Energy + NaN.** Confirm single ÷π on diffuse, no double-count; all NaN scrubs are ternary selects
  (the `mix(v,0,flag)` trap), per [the documented AMD rule]. Emissive: bound it so authored-huge
  emissive can't sparkle.

Gate: bright spots gone in the symptom scenes; no fireflies under a static camera; PBR matches a
reference sphere set. Commit.

## V3 — IBL / sky / atmosphere

- **Prefilter vs irradiance clamp asymmetry (S2).** The prefilter clamp (16384) lets the procedural
  sun disk (~60000 pre-clamp) leak bright specular into rough/dim surfaces, while irradiance is
  capped at 500. Bring these into a physically coherent relationship (the sun disk should not be
  reflectable as a sharp bright spot on a rough surface) — likely clamp the env *before* prefiltering
  consistently, or tighten the prefilter cap, judged on the IBL A/B. ([IblBake.hlsl:56,112](../../BallisticEngine.DX12/Shaders/IblBake.hlsl#L56))
- **fp16 safety.** Confirm EVERY float-cubemap upload clamps below ~65504 before RGBA16F (the
  documented sun=Inf→NaN gotcha). Audit the new sky/transmittance LUT uploads from the merge.
- **Aerial perspective (S5).** If V0 implicates it: make the inscatter magnitude genuinely share the
  scene's raw-radiance space (verify `SkyTint` units), and either wire the dead `Exposure` constant
  or delete it so intent is unambiguous. Confirm the `discard` gating and that `Strength=0` defaults
  leave the scene untouched (byte-identical with AP off).
- **DX12 semantics:** cubemap face orientation, mip count / roughness→mip mapping (`PrefilterMaxMip`),
  sampler addressing for the octahedral/cube — validate these are DX-correct, not GL-carryover.

Gate: no leaked sun-disk specular in dim scenes; haze reads correct on exteriors and is invisible in
interiors; IBL ambient sane. Commit.

## V4 — Temporal & denoise infrastructure (Lumen-ADJACENT — explicitly NOT Lumen)

This is where ghosting/sparkle compatibility with Lumen lives. Touch only the *infra*, never the GI
algorithm.

- **Firefly feedback hardening.** Add the cheap final `Sanitize(accumulated)` before history store in
  the SSGI temporal pass (S3) and `Sanitize` the DDGI-field divide result guard in DxrReflections
  (S4) — both are *defensive guards around* the resolve, not algorithm changes. Verify they don't
  alter clean-frame output.
- **OIDN sequencing (S6).** Ensure the failure fallback never feeds the undenoised buffer into the
  *temporal history* (raw noise fed back = growing sparkles). Confirm zero-copy is the active path on
  the RX 9070 XT; if it's silently falling back to readback, that's both a noise and a perf bug.
- **Motion vectors / reprojection** read OK in validation — re-confirm under the Lumen orbit harness;
  do not refactor what passes.
- **Lumen stability gate (the user's ask):** with Lumen on, orbit + translate the camera and confirm
  no ghosting trails, no sparkle accumulation a static camera can't flush, no disocclusion smear.

Gate: no crawling sparkles with GI on; Lumen stable in motion. Commit.

## V5 — Per-system DX12-semantics audit (the "validate every system / rewrite if needed" sweep)

A systematic pass over every remaining renderer subsystem to certify it's correct *for DirectX 12*,
not just a literal GL transliteration. For each: check coordinate/clip conventions (RH, z∈[0,1],
row-major + transpose-on-upload), resource states & barriers, descriptor/heap lifetime, sampler
addressing modes, format/fp16 assumptions, and clear/discard semantics. Rewrite cleanly where the
port carried a GL idiom that's wrong or fragile under DX12; leave byte-identical what's already
correct.

Subsystems to certify (each a checklist item, byte-diff or targeted-eyeball gated):
G-buffer fill & layout · deferred lighting · cascaded shadows (matrices, bias, PCF, cascade select)
· clustered light froxel build/cull (CPU vs GPU parity) · procedural sky · skybox cubemap ·
transparents (forward, sort, blend) · SSAO · SSR march/combine · bloom · composite/grade · TAA ·
FSR wiring · auto-exposure · Hi-Z & GPU-driven cull (already proven good — light recheck) · the new
sky/atmosphere LUTs. Explicitly **excluded:** the Lumen GI passes.

Gate: each subsystem signed off as DX12-correct; any rewrite byte-verified where output should be
unchanged, symptom-verified where it was buggy. Commit per subsystem.

---

## P-series — Performance (substance unchanged from rev 1; correctness comes first)

Once the image is correct, execute the performance plan. The headline remains: the renderer does
~40 full GPU flushes per frame because every pass and every resource transition is its own
`ExecuteSync`→`WaitForGpu` — a documented "fix later" shortcut
([Dx12Device.cs:152-154](../../BallisticEngine.DX12/Dx12Device.cs#L152-L154)). The verified blockers
to a single-submit frame are GI-only (OIDN readback fallback, OIDN HIP sync) plus the once-per-frame
DXR AS build — so the no-GI frame collapses to one submit cleanly.

- **P0 — Pipelined single-submit frame (THE perf fix; ~80% of the win), in 3 reversible sub-phases:**
  - **P0a** One recorded command list + barrier batching + one submit/frame (still one wait at end).
    Turn the `Dx12OffscreenTarget`/`Dx12GBuffer` transition helpers and pass wrappers from
    *submitters* into *recorders* into the open frame list. Keep `ExecuteSync` only for asset
    uploads, IBL bake, and the headless screenshot path. When the rare OIDN readback path runs, the
    frame splits into ≤2 submits; no-GI / RDNA4+HIP frames stay single-submit.
  - **P0b** Frame-in-flight: N-buffer (N=2–3) allocators + fences AND **every per-frame-written
    constant buffer + shader-visible descriptor heap** (`cbRing`, `deferredCb`, `frameCb`,
    `motionCb`, the `*Cb`/`*SrvVisible` set) — once the CPU runs ahead it will otherwise stomp data
    the GPU is still reading (intermittent corruption; verify multi-frame, not just one frame).
  - **P0c** Present without the per-frame full `dev.Flush()` — fence-gated backbuffer reuse (keep
    vsync on during bring-up). Acceptable to ship P0a+P0b first if P0c proves fiddly.
- **P1** Coalesce/minimize barriers within the frame list (falls out of P0).
- **P2** Cache the 35 per-frame `Environment.GetEnvironmentVariable` reads once at init.
- **P3** Cache per-pass descriptor tables (rebuild only on resize / IBL re-bake / target realloc);
  must respect P0b's N-buffered heaps.
- **P4** Shadow/light CPU cleanup (frustum extracted once not per-cascade, single light view
  transform, cluster-AABB rebuilt only on projection change, pre-allocated caster list).
- **P5** Capture the real GPU timeline (`RenderStats.Scene.GpuPasses` / RenderDoc) and tune the
  genuinely-bound passes (likely fog march, SSGI slices, SSR steps, PCF) — measure first, never
  pre-optimize against estimates.
- **P6** (optional) async compute / multi-threaded recording only if P5 shows headroom.

Detailed P-series mechanics, hazards, and verification recipes are in the rev-1 body retained
below.

---

## Verification recipe (per phase, per scene)

```bash
# Clean reference (deterministic) + full-FX, both captured
BALLISTIC_SCREENSHOT_PAUSED=1 BALLISTIC_DETERMINISTIC=1 bal render <scene> --out det/<scene>.bmp
BALLISTIC_SCREENSHOT_PAUSED=1                            bal render <scene> --out fx/<scene>.bmp

# Correctness phases (V*): symptom must be gone, nothing else regressed
bal imgdiff base/<scene>.bmp work/<scene>.bmp           # expect CHANGE only where intended
#   + eyeball enclosed + exterior + GI-isolate (renderer-screenshot-verification)
#   + Lumen stability: bal render <scene> --orbit 8  -> diff consecutive frames for ghosting/sparkle

# Perf phases (P*): byte-identical + cpuFrameMs trend
bal imgdiff det/<scene>.bmp work/<scene>.bmp            # meanError 0
bal perf <scene>                                        # cpuFrameMs should fall hard after P0
```

`RenderStats.Scene.CpuFrameMs` ([:1808](../../BallisticEngine.DX12/DX12HDRenderer.cs#L1808))
currently includes all `WaitForGpu` time — the headline perf metric.

---

## Definition of done

- **Image is correct:** no random sparkles, no shiny spots in dim areas, no raw/ugly grade; exposure
  correct and stable (Manual≈Auto, no flicker).
- **Lumen is testable and stable:** with GI on, no ghosting, no sparkle accumulation, clean in
  motion — and Lumen's *algorithm* was never modified.
- **Every subsystem certified DX12-correct** (V5 checklist signed off); ports that were wrong are
  rewritten and verified.
- **Frame is pipelined:** 1 submit + ≤1 fence wait per no-GI frame (vs ~40); CPU overlaps GPU;
  `cpuFrameMs` dominated by real work, not waits.
- All test scenes verified; perf phases byte-identical to the clean reference; correctness phases
  symptom-fixed with no collateral regressions.

## Sequencing note

V0 first (cheap, no code — it tells us what's actually broken). Then V1→V2→V3→V4→V5 (correctness),
then P0→…→P5 (perf). P2 (env-var caching) is trivial and independent and may be slotted in as a
warm-up whenever convenient. Each phase a separate, reverted-if-needed commit on `dx12-renderer`.
Keep the perf kill-switch until P0 is signed off. **Never touch Lumen.**
