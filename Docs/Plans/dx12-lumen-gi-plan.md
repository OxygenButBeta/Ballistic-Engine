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

**Phase 5 — World radiance cache (far-field stability).**
- DDGI-style world-space irradiance probes (auto-placed via the GpuSceneQuery substrate — invisible,
  tuning-free) for stable, cheap distant GI; screen/RT traces fall back to it at range.
- Verify: far-field stable + cheaper; no light leak (visibility-aware, per the GpuSceneQuery plan).

**Phase 6 — Denoise + temporal tuning.**
- OIDN (zero-copy, done) on the gather + temporal reprojection (motion buffer). Apply the hard-won temporal
  rules (ternary NaN scrub, pre-exposure consistency, disocclusion rejection).
- Verify: converged + stable under motion; no boiling; no NaN black holes.

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

**Phase 8 — Reflections via the surface cache (bonus, unifies with existing RT reflections).**
- Lumen reflections reuse the surface cache + RT; fold the existing DX12 RT reflections into this so rough
  reflections sample the surface cache (consistent GI + reflections).

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
