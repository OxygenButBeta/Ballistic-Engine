# DX12 Hardware-Lumen GI — From-Scratch Plan

**Status:** PLANNED. Captured 2026-06-15. **Supersedes** the GL-era Lumen roadmap (software voxel/SDF/JFA),
which is explicitly abandoned (see §0). This is the plan to do real-time GI *right* on the DX12/DXR backend.

> Part of the AI-native vision ([ai-native-engine-master-plan.md](ai-native-engine-master-plan.md), "easy good
> graphics"). GI being fully dynamic is the linchpin of the automation doctrine (Tier-1 "eliminate" — no probe
> placement chore). The spatial substrate ([gpu-scene-query-autoplacement.md](gpu-scene-query-autoplacement.md))
> and this GI plan share the same foundation: the **DXR TLAS (`Dx12SceneAS`)** — NOT the broken SDF.

---

## 0. What went wrong before, and the decision

The GL GI (voxel cone tracing + mesh-SDF + GPU JFA) was **slow AND broken** — measured, not opinion:
GDF stuck coarse (32³ → speckle/wash), JFA seed extraction ~6.6s on dense scenes (killed perf + camera
motion), bounce didn't scatter into rooms. **Lifting the GL compute ceiling (4.1→4.6 + GPU JFA) did NOT fix
it** — so the compute ceiling was never the root cause. The real ceiling was **no hardware ray tracing**: GL
forced a *software approximation* (voxel/SDF march) that is coarse + expensive *by nature*.

**Decisions (non-negotiable):**
- **DO NOT port or resurrect the GL voxel/SDF/JFA stack.** Same technique = same speckle. It dies with GL.
  (The dead SDF baker cluster — MeshSdf/MeshSdfBaker/SdfSeedExtractor/TriangleBvh/SdfArtifact/SdfCache — is
  ALREADY deleted, commit 1b0485ad. Do not recreate it.)
- **Build Hardware Lumen on DXR.** DX12 has real HW RT; the TLAS already exists (`Dx12SceneAS`, used by RT
  shadows/reflections/GI). Trace real rays against it — no coarse field, no software SDF.
- **Incremental + measured.** The prior effort burned out by building everything at once and judging on
  composite frames. Here: every phase is build → headless A/B verify (GI-isolated) → perf-measure on a budget →
  commit. No next phase until the current one is *measured* good.
- **Build from PUBLISHED techniques** (Epic Lumen talks/SIGGRAPH, DDGI/RTXGI papers) — NOT UE source (EULA).

---

## 1. Target & non-goals

**"Right" = all of these, verified:**
- Indirect light **reaches into enclosed rooms** (the thing the GL version failed). Judge in interiors.
- **Color bleeding** from surfaces (a red wall tints the floor).
- **Off-screen** contribution works (not screen-space-limited).
- **Multi-bounce** (light bounces more than once; rooms aren't pitch black in shadow).
- **Stable under camera motion** (no boiling/speckle; the GL failure mode).
- **Within a perf budget on the AUDIENCE GPU**, not just the RX 9070 XT (see §6).
- Fully **dynamic** (no bake, no probe authoring) — moving lights/geometry update GI.

**Non-goals:** software voxel/SDF GI; matching UE Lumen feature-for-feature; offline/baked GI as the primary
path; per-knob tuning on the front door (good defaults — doctrine).

---

## 2. Architecture (Hardware Lumen, built on the existing TLAS)

The published Lumen pieces, mapped to what we have:

| Lumen piece | What it does | Our build |
|---|---|---|
| **Surface cache** | radiance parameterized on mesh surfaces (atlas/cards), lit by direct + last-frame GI | NEW — the heart; enables multi-bounce + off-screen + decouples cost from screen res |
| **Screen traces** | short-range, high-detail GI from the depth buffer | we have SSGI (SSILVB) — reuse as the near-field |
| **World RT traces** | rays that leave the screen, hit geometry → sample radiance | DXR rays vs `Dx12SceneAS` TLAS (exists); hit shades from the **surface cache** |
| **Final gather** | screen-space radiance probes, importance-sampled, integrate incoming radiance | NEW — downsampled probes; replaces per-pixel 1-spp noise |
| **World radiance cache** | world-space probes for stable, cheap far-field | NEW (later phase) — DDGI-style irradiance probes |
| **Multi-bounce** | feed last frame's radiance back into the surface cache lighting | feedback loop once the surface cache exists |
| **Denoise/temporal** | clean the noisy gather | **OIDN (zero-copy GPU path, done)** + temporal reproject |

The current DX12 RT GI ("RT-traced SSGI": 1-bounce, shades hits from *screen color*, off-screen→irradiance) is
the **starting point** — Phase 1 upgrades its hit-shading from screen-color to a world-space radiance source.

---

## 3. Phased roadmap (each phase: build → verify → measure → commit)

**Phase 0 — Measurement harness FIRST (do not skip; this is the antidote to the prior burnout).**
- A **GI-isolate debug view** (show ONLY the indirect contribution, not the composite — judging GI on a bright
  composite frame hides everything; memory lesson).
- A/B/C capture script: GI-off (IBL only) / SSGI / RT-GI, on **enclosed interiors** (SunTemple, a deliberately
  GI-starved closed room) + Bistro. GI only shows in enclosed views — patch the camera INSIDE.
- A **perf readout** (per-pass GPU ms for the GI passes) into `.stats.json` so every phase is budget-checked.
- **Exposure sanity FIRST** — confirm darkness is GI, not an exposure/EV regression (the classic misdiagnosis).
- Deliverable: we can *objectively see and measure* GI quality + cost before changing anything.

**Phase 0.5 — GI exposure cleanup + ONE unified volume (do AFTER P0's baseline, BEFORE P1).**
This is the user's explicit request: kill the GI/volume sprawl, one clean override. (The SDF cluster is already
deleted — commit 1b0485ad.)
- **Delete + unwire the dead GL-era GI volumes:** `Engine/Rendering/IrradianceVolume.cs`,
  `Engine/Rendering/ReflectionVolume.cs`, and the `LightProbes.cs` / `Lumen.cs` / `ReflectionProbes.cs` volume
  components. These are HEAVILY referenced (PostProcessSettings, AssetDatabase, Editor, MCP, SceneSerializer,
  VolumePostProcessing, ComponentAttribute) → a careful build-verified refactor, NOT a raw delete.
- **Consolidate the DX12 GI overrides into ONE** `GlobalIllumination` volume (fold in
  `ScreenSpaceGlobalIllumination` + `ScreenSpaceReflections`): **GI Mode** dropdown (Off / Screen-Space /
  Ray-Traced[Lumen]) + **Reflections Mode** dropdown (Off / SSR / Ray-Traced) + **Intensity**; advanced knobs
  (bounce/sky-fallback/ray-length) in a foldout with good defaults (no front-door knobs — APV anti-pattern).
- KEEP the SSGI + RT-GI ALGORITHMS — they sit BEHIND the new modes. This is a UX/exposure consolidation, NOT
  an algorithm delete. Direct lighting (sun/sky/exposure) stays in its own components — this volume is INDIRECT
  only (diffuse GI + reflections, Lumen-style unified). PROPOSE the exact volume API + CHECK IN before wiring.
- This unified volume IS the Lumen exposure design; P8 reflections fold into the same volume's Reflections Mode.

**Phase 1 — World-radiance hit shading (bridge from screen-color to real GI).**
- RT GI rays currently shade hits from screen color (off-screen → flat irradiance). Replace the hit shading
  with a **world-space radiance lookup** so off-screen bounce is correct. Interim source can be the IBL +
  direct light at the hit (proper, not screen-dependent) until the surface cache lands.
- Verify: off-screen bounce now contributes; color bleed from off-screen surfaces appears.

**Phase 2 — World-probe radiance cache (DDGI). [REORDERED 2026-06-16 — user-approved fork.]**
> **DECISION (2026-06-16):** P2 is the **DDGI world-probe radiance cache**, NOT the Lumen mesh-card surface
> cache. Rationale (deep research + adversarial critic): (a) our P1 hit shading ALREADY IS the DDGI 1-bounce
> estimator — the only missing piece is the recursive radiance STORE (probes), a minimal honest delta;
> (b) mesh-cards resurrect the DELETED mesh-SDF/GDF tracer (commit 1b0485ad, proven slow+broken) for the card
> lookup; (c) cards need a per-import card-generation BAKE step the no-authoring doctrine forbids; (d) DDGI
> delivers P2's actual payoff (multi-bounce + off-screen stability) NOW, within the GTX-1660 budget, as a
> complete published technique (Majercik 2019 JCGT + RTXGI — NO shortcut: full octahedral irradiance + depth
> moments + Chebyshev leak test + classification/relocation + hysteresis). SSGI stays as the near-field
> companion (Lumen pairs a world cache with screen traces for the same reason). **Direction (user): DDGI now
> → SURFELS later** (EA SEED GIBS — the research's quality-ceiling pick for whole-mesh content); mesh-cards
> demoted/dropped. This SWAPS the intent of P2 and the old P5.
- Build the full real DDGI: a camera-centered world-probe grid; per probe an **octahedral irradiance** tile
  (R11G11B10F 6×6+border) + a **depth-moments** tile (RG16F 16×16+border mean/mean-sq). Probe update = the
  EXISTING `DxrGi.hlsl` hit shading, redirected (Fibonacci rays, hysteresis EMA blend). Gather at shade time
  = trilinear over 8 probes × **Chebyshev variance** weight (the leak test) + normal/view bias.
- Sub-phases (each judged by the ISOLATED bounce in enclosed interiors): P2.0 grid+atlases → P2.1 update pass
  → P2.2 gather + Chebyshev leak test (thin-wall closed-room gate) → P2.3 multi-bounce + per-bounce energy
  clamp (same commit — the runaway guard) → P2.4 classification/relocation → P2.5 determinism + 1660 budget.
- Verify: GI stable regardless of what's on screen; enclosed rooms lit from surfaces they can't see; NO leak
  through thin walls (Chebyshev); multi-bounce converges + doesn't run away.

**Phase 3 — Multi-bounce. [Folded into P2.3 — DDGI gets multi-bounce for free.]**
- DDGI multi-bounce is intrinsic: probe update rays sample *last frame's* probe irradiance at their hit →
  infinite bounce as a geometric series at zero extra trace cost. Clamp indirect luminance/albedo per bounce
  to avoid runaway (the SSGI-EMA black-hole failure class). Shipped in the SAME commit as the feedback loop.
- Verify: shadowed rooms are not pitch black; second-bounce color bleed visible; no energy explosion.

**Phase 4 — Final gather via screen-space radiance probes. [BUILT 2026-06-16 — P4.0/P4.1/P4.3 committed.]**
- Downsampled screen probes (1 per 16x16 tile, 8x8 octahedral radiance, 64 cosine-hemisphere rays), bilateral
  depth+normal upsample to full-res, instead of per-pixel 1-spp rays. The screen probes are the near/mid field;
  on ray miss they hand off to the DDGI world cache (P2) — Lumen's screen-trace -> world-cache hierarchy. So
  Phase 4 sits IN FRONT of DDGI, not replacing it. NOW THE DEFAULT GI gather when DDGI is on (PRIMARY flip
  2026-06-16); BALLISTIC_DX12_SCREENPROBE=0 opts out to the per-pixel DDGI gather (the byte-identical fallback).
- Sub-phases: P4.0 Place+Trace(uniform,miss->DDGI)+Blend+naive upsample (34559588) -> P4.1 bilateral
  depth+normal integrate (kills 16x16 blockiness + silhouette halos) + far-field E->L=E/PI energy fix (2e5ddfaa)
  -> P4.3 determinism wiring + 1660 budget lock (1ef182c6).
- **P4.2 (importance sampling) — RESOLVED BY MEASUREMENT, not built as a separate pass.** The plan called for
  "importance-sampled hemisphere integration"; we ALREADY cosine-weight the hemisphere rays, which IS the
  diffuse-BRDF importance sample (the half of product-importance that matters for diffuse GI). MEASURED raw
  pre-denoise noise (noise/mean, GI-isolate): SunTemple DDGI-gather 0.0183 -> screen-probe 0.0120 (-35%); Bistro
  0.0779 -> 0.0581 (-25%). The screen-probe gather is ALREADY less noisy than the per-pixel DDGI gather — the
  Phase-4 bar ("noise drops pre-denoise") is met+exceeded with cosine-BRDF-IS. Full PRODUCT importance sampling
  (BRDF x last-frame-lighting CDF: a GenerateRays pass + ping-pong prev-atlas) pays off only when noise is HIGH
  (per-pixel 1-spp); here noise is already low + OIDN-cleaned, so it's deferred as a future refinement (a new
  GPU-hang surface for a marginal win = gold-plating, against the execution discipline). Revisit if a high-noise
  scene ever demands it.
- VERIFIED: noise drops vs per-pixel (above); perf 0.63ms (cheaper, amortised); detail preserved on bilateral
  upsample (SunTemple isolate smooth, per-surface detail, no halos); Bistro leak test PASS; determinism SHA256
  frame-independent.
- **PRIMARY FLIP DONE & VERIFIED (2026-06-16).** Screen probes are now the DEFAULT near/mid-field GI gather
  when DDGI is on (DDGI = the far-field cache); the per-pixel DDGI gather is the BALLISTIC_DX12_SCREENPROBE=0
  fallback. One-line three-state flip (ScreenProbeEnabled: `!= "0"`) + comments — no new GPU resources/barriers/
  shaders. **BYTE-IDENTICAL-OFF PROVEN: SCREENPROBE=0 SHA256 == the pre-flip HEAD default, bit-for-bit**
  (3A9506C0...). A/B (paused f24, RX 9070 XT, DRED on, 4 clean launches no removal/hang): SunTemple GI-isolate
  default 30.7 (smooth, per-surface detail — floor mosaic/column capitals/statue folds, red-pedestal bleed)
  vs DDGI-gather fallback 21.7 (flatter, coarse-grid fill) -> the screen-probe look is strictly richer. Bistro
  interior LEAK TEST PASS (99.8% near-black, bounce contained, no wall bleed, mean 3.4 == fallback). Audit
  wf_5cb47cb8 GO (3/3, 0 blockers; byte-identical-off oracle + barrier brackets + handoff re-verified).

**Phase 5 — World radiance cache / SURFELS (far-field stability). [DEFERRED 2026-06-16 — user chose Phase 6+8 first.]**
> **DECISION (2026-06-16, user, after adversarial research wf_81bc0ec0):** DEFER surfels; do Phase 6 then Phase 8
> first. The "DDGI now → surfels later" fork still holds, but the research's honest finding is that on the engine's
> INTERIOR fixtures (SunTemple, BistroInterior_Wine) surfels are a MARGINAL, mostly-OFF-SCREEN win: the engine
> already ships the exact two-level radiance-cache hierarchy GIBS is one variant of (screen-probe PRIMARY →
> DDGI far-field cache), and DDGI already produces the enclosed-room bounce GL-Lumen failed (BistroInterior
> 17.4→27.3, SunTemple 78→97). ~60-70% of a surfel build is free via the proven rayData producer-swap + the
> identical `DdgiTrace.ShadeHit` loop — BUT the genuinely-new parts (GPU spatial hash grid + screen-driven
> spawn/coverage + atomic free-list + GPU compaction) are the HIGHEST device-removal-hazard class the project
> could add (a TDR once hard-crashed the PC), AND are exactly the passes GIBS hides on an async-compute queue
> that this FULLY SYNCHRONOUS renderer (every pass = ExecuteSync + WaitForGpu) structurally cannot overlap →
> ~0.5-1.5ms of un-hideable serial cost ON TOP of a DDGI trace that already extrapolates to ~1.5-2ms on a
> GTX-1660 (near-saturating the hard ≤2ms GI budget). Phase 6 (denoise/temporal) + Phase 8 (reflections) are
> both UNBUILT, both fit the 1660 budget, and both deliver visible EVERY-FRAME wins on the current fixtures —
> strictly more visible benefit. **Revisit surfels (the measurement-gated `minimal-surfel-reusing-ddgi` build:
> P5.0 dev-card go/no-go like P7.2a → persistent buffer+hash grid → spawn/recycle → trace producer-swap →
> gather+leak) when content scales PAST the ~30m camera-centered DDGI box, where the fixed grid actually breaks.**
- (when reopened) DDGI-style or surfel world-space cache for stable, cheap distant GI; screen/RT traces fall
  back to it at range. Surface-anchored surfels = adaptive density + no grid-straddles-a-wall leak (but their
  own disc-radius/silhouette leak class), the defensible win on large/streamed content. Research: wf_81bc0ec0.

**Phase 6 — Denoise + temporal tuning. [IN PROGRESS 2026-06-16 — chosen next after the Phase 5 deferral.]**
- OIDN (zero-copy, done) on the gather + temporal reprojection (motion buffer). Apply the hard-won temporal
  rules (ternary NaN scrub, pre-exposure consistency, disocclusion rejection).
- The chain ALREADY EXISTS and honors the lessons (Ssgi.hlsl PSTemporal: motion reproject + neighbourhood clamp
  + pre/post firefly clamp + box-drift disocclusion reset + ternary Sanitize; OIDN zero-copy GPU path with CPU
  readback fallback). Phase 6 = TUNING + the known gaps, not a from-scratch build.
- **SUB-PLAN (user-approved 2026-06-16 — "Guided OIDN + motion-test harness"):**
  - **P6.0 — MOTION-stability measurement harness FIRST** (the Phase-6 antidote, like P0 was for the track).
    PAUSED captures can't show temporal stability/boiling. Build a way to capture a live-temporal sequence
    (scripted orbit OR static-camera live-temporal play frames) + a frame-to-frame "boiling" metric (mean abs
    delta of the GI-isolate between consecutive frames) so "stable under motion" is MEASURED, not eyeballed.
    Behind a BALLISTIC_DX12_* door; deterministic paused capture stays byte-identical.
  - **P6.1 — Guided OIDN (albedo + normal AOVs). DONE & COMMITTED — kept OPT-IN (user, marginal win measured).**
    OIDN denoised UNGUIDED (color-only). Added the (half-res) G-buffer ALBEDO (RT0) + WORLD-NORMAL (RT1) as OIDN
    guide AOVs on the ZERO-COPY path: CSPackAux packs them into 2 more shared float4 buffers, imported as the
    filter's "albedo"/"normal" images (filter rebuilt ONCE with guides; PackAux re-packs each frame). Readback
    path stays unguided (rare non-HIP fallback; guiding it = CPU readback of 2 full-res G-buffer textures, not
    worth it). Behind BALLISTIC_DX12_OIDN_GUIDE=1 (default OFF → byte-identical to pre-P6.1). De-risk: 21/21
    shaders CPU-compile; 5-reviewer adversarial wiring audit (wf_31b87367) = **GO, device-removal risk NONE, 0
    required fixes** (root sig↔shader registers exact, sizing self-consistent no-OOB, lifecycle sound); applied
    2 recommended fixes (graceful-degrade a guide-commit failure to unguided instead of killing the color filter;
    free unused aux buffers on the rare import-failure branch). **HONEST RESULT (measured, 4 DRED-guarded CLEAN
    launches no removal): the win is MARGINAL on these fixtures** — SSGI GI-isolate guided-vs-unguided meanAbsDiff
    0.15/255 (max 39), horiz HF energy 0.7177 vs 0.7213 (slightly cleaner), boiling metric unchanged (2.556 both),
    ~0 extra cost (PackAux negligible; denoise ~7-10ms both). ROOT CAUSE: the GI is already low-noise by the time
    OIDN runs (half-res + temporal EMA + the screen-probe gather is inherently clean per P4.2) — guides help most
    on a NOISY 1-spp signal we don't have here. User chose KEEP OPT-IN (correct + cheap cushion for noisier
    content, no default flip, no claim of a big win). Phase 6's real value = the P6.0 motion harness + P6.2.
  - **P6.2 — Temporal motion-stability VERIFIED (no EMA tuning needed). ★ PHASE 6 COMPLETE.** Ran the P6.0
    orbit harness on BOTH fixtures. The GI-ISOLATE bounce (what Phase 6 judges) is STABLE under motion on both:
    consecutive-frame deltas DECAY (SunTemple ratio 0.68, Bistro 0.85) — the EMA settles toward the motion floor,
    not amplifying. Per-frame max + 99.9th percentile are BOUNDED and DECREASING across the sequence (SunTemple
    isolate max 100→92, Bistro 20→17) → NO runaway EMA, NO growing noise field (the classic SSGI-EMA black-hole
    failure mode is absent). The composite's larger deltas (~6.4 on Bistro) are legitimate scene-geometry motion
    under the orbit, not GI boiling (the isolate is 0.44). CONCLUSION: the temporal pass already honors every
    hard-won lesson (motion reproject + neighbourhood clamp + pre/post firefly clamp + box-drift disocclusion
    reset + ternary Sanitize) and is MEASURABLY stable → tuning it would be gold-plating (the agreed bar: tune
    ONLY if boiling shows; it doesn't). The verification IS the deliverable. 4 DRED-guarded launches, no removal.
- Verify: converged + stable under motion; no boiling; no NaN black holes; byte-identical deterministic capture.
  ★ ALL MET. **PHASE 6 (denoise/temporal) DONE: P6.0 motion harness (committed) + P6.1 guided OIDN (opt-in,
  committed) + P6.2 motion-stability verified. NEXT on the GI track = Phase 8 (RT reflections via the cache).**

**Phase 7 — Low-end / no-RT fallback. [★ DONE AT THE FLOOR 2026-06-16 — ships SSGI+IBL+SSAO; the raster-DDGI proxy
was built + measured NON-VIABLE (~2ms/probe) and user chose to stop at P7.1. See P7.2b below.]**
> **THE HONEST FINDING (research wf_12e5d5b6):** "just SSGI + IBL + SSAO" is NOT an acceptable floor — SSILVB's
> own paper: *"if direct light leaves the screen, the indirect lighting disappears"* (a room lit from behind the
> camera goes black). The published fix (Unity HDRP: SSGI *"falls back to APV / Reflection Probes to gather
> lighting not present on the screen"*) is THREE LAYERS: near-field SSGI + a far-field probe grid + IBL/SSAO floor.
> Both shipping no-HW-RT GI paths (Lumen software = Global Distance Field; Flax = software DDGI vs Global SDF)
> reduce to the FORBIDDEN voxel/SDF family. The non-forbidden far-field the DDGI authors themselves list:
> **rasterized cube-relit DDGI** — keep our DDGI octahedral irradiance textures + per-pixel gather UNCHANGED,
> replace only the probe UPDATE (render a small cubemap G-buffer per probe → relight → octahedral project; NO
> rays, NO SDF). Reuses the entire Phase-2 DDGI infra "minus the trace". User chose the FULL floor (gate +
> raster-DDGI), with P7.2+ a MEASURED go/no-go after P7.0 reports the GTX-1660 probe budget.
- **P7.0 — capability gate + measurement/test harness. DONE & VERIFIED (this session).** Eager
  `Dx12Device.HasHardwareRayTracing` (Options5.RaytracingTier>=Tier1_0, queried ONCE at device init); the 3 lazy
  `EnsureRt{Gi,Shadows,Reflections}` checks now read it (was triplicated CheckFeatureSupport). AUTO-DOWNGRADE at
  the GI dispatch: no HW RT → RayTraced GI→ScreenSpace, ABSOLUTE (even BALLISTIC_DX12_RT_GI=1 loses — a forced RT
  path on a non-DXR device is the device-removal/PC-crash hazard); reflections/shadows fall back via their
  Ensure* returning false; one-time `[DX12] No hardware ray tracing — downgraded...` log. **Test door
  BALLISTIC_DX12_FORCE_NORT=1** pins the flag false on the RT-capable dev card (won't crash) → the no-RT path is
  A/B-able on dev hardware. VERIFIED (RX 9070 XT, DRED on): RT-available path BYTE-IDENTICAL to pre-P7.0
  (24E6A874… — gate is a no-op when RT present); FORCE_NORT=1 logs the downgrade + runs SSGI (no SCREENPROBE/DDGI),
  CLEAN exit no device-removal even with RT_GI=1 forced; no-RT GI-isolate = valid SSGI bounce (SunTemple mean 20.4,
  not black) vs RT 30.7. This ships the safety fix even if the floor stops here.
- **P7.1 — SSGI+IBL+SSAO floor as the no-RT GiMode. DONE/VERIFIED via P7.0 (no new code needed).** The downgrade
  already routes no-RT → SSGI over the IBL-lit scene + SSAO multiply, which composes coherently. VERIFIED (FORCE_
  NORT=1, composite, RX 9070 XT, DRED): SunTemple no-RT floor mean 87.3 — bright, well-distributed, clean (looks
  genuinely good; SunTemple is open-ish so the screen-space floor holds). Bistro interior composite no-RT 22.0 vs
  RT 19.2 (close — both dominated by direct point-light+IBL; SSGI's on-screen bounce slightly more aggressive than
  RT-DDGI here). HONEST FINDING: on BOTH fixtures' DEFAULT cameras the no-RT floor is acceptable, not broken — the
  dramatic off-screen "window-behind-camera" hole the research warns about needs an ADVERSARIAL camera; it's a
  principle to fix (P7.2 raster-DDGI), not a catastrophe on these views. So P7.2 is a QUALITY-CEILING build, not a
  rescue. Captures e:/tmp/p4flip/p71_*.
- **P7.2 — rasterized probe G-buffer capture (no relight).** Design (research wf_8c41941d + adversarial audit
  wf_bc98efc7): the DDGI cache is decoupled from its ray source by the rayData buffer, so P7.2 swaps the PRODUCER
  of rayData (raster+relight per probe instead of inline RayQuery), reusing ~99% of the cache (blend/gather/
  Chebyshev/multibounce/round-robin/warm-up/determinism). User chose cube-6-faces (correct first) + measure-one-
  probe-first.
  - **P7.2a DONE & VERIFIED — the go/no-go MEASUREMENT (the gate the user demanded).** Dx12RasterProbe.cs +
    RasterProbe.hlsl/RasterProbeDebug.hlsl: render ONE probe at the camera as 6 cube faces of a 24px G-buffer
    (albedo+normal+depth), reusing the per-submesh draw loop with a probe-face viewProj; NO rayData/blend/grid.
    Behind BALLISTIC_DX12_NORT_PROBES=1 (+_DEBUG=1 blit). Audit GO (synth refuted 6/7 "blockers" as predicated on
    a false frame-pipelining premise — the renderer is fully synchronous ExecuteSync; applied the 1 real fix:
    srvVisible.Reset() before the probe pass to stop intra-list descriptor-ring wrap on heavy scenes). VERIFIED
    (SunTemple, paused f24, RX 9070 XT, DRED on, CLEAN no removal/hang): **★ 1 probe × 6 faces = 949 draws (~158/
    face) in 7.483ms on the DEV CARD → 128 probes/frame ≈ 958ms. FULL-GEOMETRY RASTER IS NON-VIABLE** (even ~16
    probes/frame ≈ 120ms; a GTX-1660 is worse). VERDICT: a reduced-geometry PROXY is MANDATORY (confirms the
    research) — P7.2b/c must NOT use full per-submesh geometry. Captures e:/tmp/p4flip/p72a_*.
  - **★ P7.2b DONE & MEASURED — the proxy WORKS but is NON-VIABLE for a per-frame budget; user chose STOP-AT-P7.1.**
    Built the user-chosen MERGED WHOLE-MESH LOW-CULL proxy (option a, lean-PSO): `GBufferProbeBindless.hlsl` (lean
    2-MRT bindless probe shader = GBufferBindless minus tangent/motion; PS emits albedo + world-normal only, no b1
    motion) + `Dx12GpuDrivenRenderer.{BuildProbePipeline, ProbeBuildFaceMeta, RenderIntoProbeFace}` (a 2nd lean
    `probeDrawPso` built against the EXISTING drawRootSig so cmdSig is reused, RenderTargetFormats={albedo,normal}
    matching the probe cube; reuses the compute cull + ExecuteIndirect + bindless material table; own
    probeCommands/probePerDraws/probeMeta/probeCullParam buffers sized 6×Capacity for disjoint per-face slices;
    HizEnabled=0; WorldAabb cached once/submesh, per-face Mvp re-stamped) + `Dx12RasterProbe.RenderOneProbeGpuDriven`
    (drives the 6 faces via the proxy). EXACTLY mirrors the proven GPU-driven SHADOW path (2nd PSO, different RT
    formats, reused cull). Behind BALLISTIC_DX12_NORT_PROBES=1 + **BALLISTIC_DX12_NORT_PROBES_PROXY=1** (A/B vs P7.2a).
    De-risk: 18/18 shaders CPU-compile (incl the lean shader); 4-reviewer adversarial wiring audit (wf_141f46c5) GO,
    0 device-removal-class bugs, 0 required fixes (refuted format-mismatch / unbound-b1 / Hi-Z-stub-read /
    buffer-overflow / barrier-desync — all verified safe; off-by-one confirmed: f=5 max write 49151 < ProbeCapacity
    49152). **VERIFIED (SunTemple, paused f24, RX 9070 XT, DRED on, 3 CLEAN launches no removal/hang): proxy collapses
    949 draws/probe → 6 ExecuteIndirect/probe (1/face); cost 1.76–2.32ms/probe vs P7.2a 7.483ms = 3.2–4× faster.**
    ★ BUT STILL NON-VIABLE per-frame: 128 probes ≈ 225ms; round-robin 1/8 (~16/frame) ≈ 30ms; even 1 probe/frame ≈
    2ms = the ENTIRE ≤2ms GI budget (and a GTX-1660 is slower than this dev card). ROOT CAUSE of the floor: at 24px
    the PIXEL cost is ~0 but the VERTEX throughput is full — 606k whole-mesh tris × 6 faces ≈ 3.6M vert invocations/
    probe (the AABB cull only drops whole submeshes OUTSIDE each face frustum; a probe inside the temple sees most
    geometry across its 6 faces). Resolution can't shrink this; only DRASTICALLY fewer tris (a true low-poly proxy /
    aggressive LOD — which "merged whole-mesh" explicitly is NOT) would. Captures e:/tmp/p72b/.
  - **★ USER DECISION (2026-06-16): STOP AT P7.1 — ship the SSGI+IBL+SSAO floor as the no-RT GI path.** P7.1's floor
    already looks good on both fixtures' default cameras (re-verified: SunTemple no-RT composite = bright marble,
    lit floor mosaic, clear architecture). P7.2 was always a quality-ceiling build for enclosed/off-screen-lit rooms,
    not a rescue, and the proxy doesn't fit a mid-card budget. The off-screen-only-lit-room hole stays a DOCUMENTED
    no-RT limitation. P7.2b's proxy + measurement are KEPT (committed) as the reusable building block + the cost gate
    the plan demanded — not deleted; a future true-low-poly-proxy effort (reopening the proxy-type decision) could
    build on it. **PHASE 7 (no-RT fallback) = DONE at the floor.** P7.2c/d, P7.3/P7.4/P7.5 are NOT pursued (they
    assumed a viable proxy). Env doors kept: BALLISTIC_DX12_FORCE_NORT (gate A/B), NORT_PROBES + NORT_PROBES_PROXY
    (the measurement door). NEXT on the GI track: Phase 5 surfels / Phase 6 denoise / Phase 8 reflections (RT path).

**Phase 8 — Reflections via the world cache (unifies GI + reflections). [★ P8.0 DONE 2026-06-16.]**
- Lumen reflections reuse the radiance cache + RT; we fold the existing DX12 RT reflections into the DDGI WORLD
  CACHE so reflection-ray HITS are shaded with the SAME world-radiance estimator the diffuse GI uses (Lumen
  "Hit Lighting"), and the reflected surface's own indirect bounce comes from the same cache (multi-bounce
  reflections, free). Research+critic: wf_48eda70d (published Lumen arch + the /PI energy trap). Our world cache
  is a DIFFUSE octahedral IRRADIANCE field (not a directional radiance cache) — so the RIGHT technique is
  trace-then-shade (the hit's ambient = the field), NOT sample-the-field-along-R (that needs /PI AND
  double-counts where the diffuse pass already lights rough surfaces — DEFERRED as an energy trap).
- **P8.0 DONE & COMMITTED — the core win.** DxrReflections.hlsl ClosestHit replaced the placeholder grey
  (Irradiance*0.5) with the real world-radiance hit shading: `albedo*(sun*NdotL*shadowRay + punctual(shadow-rayed)
  + ambient)` where `ambient = SampleDdgiField(hit,Ng)` (the DDGI field when bound; IBL cube fallback when off) —
  byte-identical in math to DdgiTrace.ShadeHit / DxrGi.ClosestHit (bindless geo + 2-sided normal + albedo clamp
  0.9 + luma clamp 1e5 + ternary Sanitize). NO /PI (E used as the receiver's hemisphere irradiance, forming
  albedo*E — matches DdgiGather's no-/PI convention), NO sky double-count (field-only when DDGI on). Reuses the
  existing RT PSO (grew the root sig: HeapDirectlyIndexed + CBV b1 sun / b2 grid + table t0-t6 + root SRV
  t7-t10 mats/instances/lights/ProbeState; reserved bindless tail RtReflTableBase=16352..16359). Mirror rays
  stay deterministic → no denoiser. ssrTarget + SSR combine contract UNCHANGED. Door = the existing
  BALLISTIC_DX12_RT_REFLECTIONS (RayTraced reflection mode). De-risk: 22/22 shaders CPU-compile; 4-reviewer
  adversarial wiring audit (wf_1b047c41) = GO, 0 device-removal blockers, applied C1 (depth NonPixel transition,
  mirror DrawRtGi — was missing) + C2 (Texture2D inert t6 when DDGI off, kills a validation warning). VERIFIED
  (RX 9070 XT, DRED, 5 clean launches NO removal/hang/PageFault): SunTemple floor/statue/pedestal show real
  COLORED LIT reflections (red pedestal bleeds into the floor, off-screen columns reflected — vs the old grey);
  A/B vs SSR = 59.7% px changed, means 91.1 vs 91.4 (no blowout/double-count); Bistro interior coherent warm
  restaurant, no wall-bleed/NaN (leak holds), 19.4% px changed, means 18.3/19.2. DETERMINISM PROVEN: SunTemple
  det f24 == f240 SHA256 BYTE-IDENTICAL. Budget Reflections:RT 1.51ms (SunTemple) / 2.07ms (Bistro) dev card.
- **P8.1 (DEFERRED — budget lock, only if measured over budget on a 1660):** lower MAX_ROUGHNESS 0.6→0.4 (Lumen's
  documented optimization), cap the punctual shadow-ray loop in the reflection variant (sun-only), optional
  quarter-res. Door BALLISTIC_DX12_RT_REFL_BUDGET. NOT built — reflections are a separate optional effect from
  the ≤2ms diffuse-GI budget (a 1660 uses SSR); reopen only with a measured over-budget on real weak HW.
- **DEFERRED (gold-plating / traps):** rough-tail field-along-R term (the /PI energy trap), GGX-jittered glossy
  rays + reflection denoiser (reintroduces noise the deterministic mirror design avoids), true directional
  surface cache (mesh cards = forbidden authoring), recursive reflection rays (DDGI ambient already = multi-bounce).

**EMISSIVE-AS-GI-SOURCE — DONE & COMMITTED (b95ccee5, 2026-06-16).** Emissive surfaces now act as area
lights in the indirect bounce (the published DDGI/RTXGI/Lumen technique — Majercik 2019 §4.2-4.3, RTXGI CHS
emissive, Lumen "Emissive Light Source"). At each GI ray hit, `radiance = albedo*(sun+punctual+ambient) +
emissive`, emissive added RAW (NO /PI, NO albedo multiply — self-emission is outgoing radiance), decoded
byte-identically to the raster GBufferBindless (`HasEmissive ? emissiveMap.SampleLevel(uv)*EmissiveFactor : 0`).
Four lockstep hit-shading sites: DdgiTrace (world cache), ScreenProbeTrace (PRIMARY gather), DxrGi
(SCREENPROBE=0 fallback), DxrReflections (emitters in mirrors). Emissive is a CONSTANT additive source → no
multi-bounce runaway; no double-count (directly-visible emissive = G-buffer on the camera pixel; the GI term
= off-screen bounce hits on OTHER surfaces). Door `BALLISTIC_DX12_GI_EMISSIVE` (default ON, correctness fix)
rides a SPARE slot in each shader's existing b0 CBV (Params2.w / Params.z / SpParams2.w / ReflConstants.Pad0) —
NO root-sig/resource change, hang-surface unchanged, `=0` byte-identical-off. Research wf_131bd51d (GO) +
adversarial audit wf_7c0c8f40 (5 reviewers) found 1 BLOCKER: DxrReflections lacked the per-channel min(60000)
fp16 cap (luma-only clamp + no preExposure) → unbounded emissive could store +Inf into the half-res RGBA16F
ssrTarget that the SSR combine spreads (no read-side scrub) = the Inf/NaN black-hole class; FIXED at source +
defense-in-depth SanitizeSsr on the SSR-combine read (ternary, never mix*0). Verified (RX 9070 XT, DRED, ~14
guarded launches, NO removal): emissive feeds the DDGI world cache PROPORTIONALLY on real textured-emissive
content (BistroInterior lanterns: atlas mean delta 0.00005 @intensity1 → 0.00234 @40, warm R>G>B bounce =
2700K color); determinism preserved (SunTemple isolate f24==f240 SHA256 byte-identical, emissive ON). KNOWN
FOLLOW-UP (pre-existing, NOT this change): COLOR-ONLY emissive on SEPARATE single-submesh whole-mesh renderers
isn't resolved by the RT trace's per-triangle MaterialId mapping (Dx12RtGeometry) while the raster G-buffer is
— textured emissive on imported/merged meshes (the real content path) works; likely affects albedo color-bleed
from such geometry too.

---

## 4. Verification & measurement doctrine (the antidote)

- **Judge GI by the ISOLATED indirect bounce, never the composite mean.** Build the GI-isolate view in Phase 0.
- **Test in ENCLOSED interiors** (GI only shows there; bright exteriors hide it). SunTemple + a closed room.
- **Exposure-first**: rule out EV/exposure before blaming GI for "too dark".
- **Determinism**: `BALLISTIC_DETERMINISTIC`; fixed sample patterns for any stochastic GPU work (byte-identical).
- **Perf budget on the audience GPU**, not the dev card. Every phase reports per-pass GI ms.
- Each phase ships with its env door (`BALLISTIC_DX12_*`) for A/B.

## 5. Hard-won lessons to NOT relearn (from prior GI work)

- Coarse field ⇒ speckle/wash. (Why we use HW RT, not a software field.)
- **NaN scrub MUST be a component ternary-select, never `mix(v,0,flag)`** (NaN*0==NaN) — in every temporal
  feedback shader.
- **Pre-exposure consistency**: GI is pre-exposed (raw HDR ~1e5); gather pre-exposes, combine converts back —
  mismatches make GI invisible or blown out.
- **GI only shows in enclosed views** — a paused exterior shot is a useless test.
- **"Everything dark" is usually EXPOSURE, not GI** — test exposure first.
- Temporal history needs depth/disocclusion rejection or silhouettes get wrong-surface history under motion.

## 6. Hardware reality

Dev card RX 9070 XT (RDNA4) has strong HW RT — far above the audience floor (≈ GTX 1660 / RTX 3060, often
weak/no RT). **HW Lumen is the primary path on RT-capable GPUs; Phase 7 SSGI+IBL is the no-RT fallback and must
look acceptable, not broken.** Measure every phase against a budget a mid-card can afford; don't let the dev
card hide the cost.

## 7. Doctrine

No shortcuts — build the REAL technique (real surface cache, not flat-irradiance stand-in). Published sources
only (no UE source/EULA). Fully dynamic (no bake/probe authoring). Good defaults, no front-door knobs. Every
phase verifiable + measured before the next. Fallback path must look good, not just exist.
