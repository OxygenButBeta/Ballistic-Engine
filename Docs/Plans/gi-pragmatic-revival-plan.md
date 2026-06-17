# Pragmatic GI — "not Lumen, but shippable" plan

**Status:** PLANNED (2026-06-18, rev7 — single file: operative spine + rationale). Branch: `dx12-renderer`.

**Goal:** Re-enable & harden a GI system from the existing committed pieces — **fully dynamic, bake-free, no manual probe placement, real bounced light + real reflections that don't fall to IBL in near/mid-field, leak-free, dynamic cost minimized.** Min target hardware: **RT-capable (RTX 2060 class).**

> **One file, two parts.** Part A = what to DO (the runner's contract). Part B = WHY (the decision trail).
> If you only want to run it, Part A is enough. Don't split this back out — keep rationale in Part B.

> **★ PROVISIONAL POLICY (the one rule that governs everything).** This plan was repeatedly written against
> stale memory/snapshots. So: **every decision-gating claim — a `file:line`, a `--stat`, a
> "committed/VERIFIED/measured", a memory-cited fix, even a conclusion reached earlier in this doc — is
> re-measured against the working tree at its first load-bearing use. No claim is exempt, headline included.**

---

# PART A — THE PLAN (what to do)

## Ground truth (measured 2026-06-18 via `git`, not assumed)
- GI **re-enable + bridge commits are in HEAD** (committed). The **bridge OVERPROMISED**: HEAD and tree both still force `GiMode=Off / SsgiEnabled=false / ReflectionMode=Off / SsrEnabled=false` at [VolumePostProcessing.cs:66-76](../../Engine/Rendering/Volumes/VolumePostProcessing.cs#L66). → **R0.1 (flip the bridge) is genuinely UNSTARTED.** Choke-point ([DX12HDRenderer.cs:1644](../../BallisticEngine.DX12/DX12HDRenderer.cs#L1644)) IS reverted; GI runs only via env door, OFF from volume.
- Current uncommitted tree = **small, mostly shader work** (196 ins/51 del; `DeferredLighting/StandardOpaque/Transparent .hlsl`). **Separate, mine, leave untouched** (provenance: user-confirmed in conversation — see Part B). No untangling, no provenance gate. **Plan runs autonomously.**
- `ibl.EnvSrv` compile-blocker is gone (0 matches at HEAD).
- §0 bindless offsets exist but are stale-by-form (`16384-N` in [Dx12GiPass.cs:150](../../BallisticEngine.DX12/Resources/Dx12GiPass.cs#L150) + [Dx12ReflectionsPass.cs:85](../../BallisticEngine.DX12/Resources/Dx12ReflectionsPass.cs#L85)) → **enumerate from tree, never hand-list.**
- OIDN PID-handle fix is NOT in the tree → R1.3 starts from "does it still repro?", not "verify the fix."
- Determinism is a **partial denylist** (`DeterministicCapture` gates TAA/grain/GTAO/SSGI-history/DDGI) → byte-identical is a **smoke-check, not a hard gate** (§4).

### R0.0a re-ground (re-measured 2026-06-18, same-session `git`/`grep`/`read` against the working tree)
> PROVISIONAL POLICY applied: every load-bearing claim above was re-verified against the tree. **NO code change** —
> this is the measurement chunk. Each row below was consumed by a fresh git/grep, not memory. **Several
> Ground-truth bullets are STALE** (R1.0/R1.1/R1.2 already landed since they were written) — the in-place bullets
> are left as the rev6/rev7 record; the corrections live here so later chunks key off the fresh measurement.

| Ground-truth claim | Plan said | Re-measured in tree (2026-06-18) | Verdict |
|---|---|---|---|
| **Bridge target = `Off`** | VolumePostProcessing.cs:66-76 forces `GiMode=Off/SsgiEnabled=false/ReflectionMode=Off/SsrEnabled=false` | EXACT: lines 66-76 force `GiMode.Off`(66) `SsgiEnabled=false`(67) `Ddgi=false`(71) `ScreenProbes=false`(72) `ReflectionMode.Off`(75) `SsrEnabled=false`(76). File is **unmodified vs HEAD** (`git diff HEAD` empty; not in `git status`). `git show HEAD:` == tree, both Off. | **CONFIRMED** — bridge target is `Off`; **R0.1 (flip the bridge) genuinely UNSTARTED.** |
| Choke-point reverted | DX12HDRenderer.cs ~1644 reverted; GI env-door only | The R0.1 revert IS in HEAD (committed in `3f3406e9`): DX12HDRenderer.cs:1662-1669 has the `=== GI PRAGMATIC REVIVAL R0.1 === ... hard-disable reverted to env/PostFX resolve` comment. The **uncommitted** DX12HDRenderer.cs diff (15 lines) is UNRELATED — a `volumesDriving` shadow-cascade default fallback (post-FX WIP), not the GI choke point. | **CONFIRMED** — choke point reverted+committed; GI runs via env door, OFF from volume (because bridge still Off). |
| Uncommitted tree = shader work | "196 ins/51 del; 3 `.hlsl` only — separate, mine, leave untouched" | **STALE numbers.** Actual `git diff --stat` = **176 ins/36 del across 8 files**: 3 `.hlsl` (DeferredLighting 79/10, StandardOpaque 41/9, TransparentForward 38/8) **PLUS** `Dx12DeferredLightingPass.cs` (4: ViewProjFwd matrix for contact-shadow march), `Dx12GtaoPass.cs` (6: `dev.Flush()` before AO target re-alloc), `DX12HDRenderer.cs` (15: `volumesDriving` shadow defaults), `.idea/vcs.xml`(-1), `.sln.DotSettings.user`(+1). All are **post-FX/contact-shadow/GTAO session WIP** (matches untracked `Docs/Plans/post-fx-realism-suite.md`), separable from GI. **None touch GI.** | **STALE (counts) but conclusion holds** — leave ALL 8 untouched; the 3 `.hlsl` + the 2 pass `.cs` are the named "do-not-touch" set, not just the shaders. |
| `ibl.EnvSrv` blocker | 0 matches at HEAD | `grep ibl\.EnvSrv` over `BallisticEngine.DX12/` = **0 matches** in tree. | **CONFIRMED** — no compile blocker. |
| Bindless offsets `16384-N` stale-by-form | "enumerate from tree, never hand-list" (R1.1) | **SUPERSEDED — R1.1 ALREADY LANDED** (`fa3d6bb6`). `Dx12BindlessTail.cs` is the single source: `HeapCapacity=16384`; bases DERIVED (RtGi 16376 / DDGI 16372 / ScreenProbe 16368 / RtRefl 16352) with compile-time asserts. `Dx12GiPass.cs:155-157` + `Dx12ReflectionsPass.cs` read `Dx12BindlessTail.*`. **No `16384 - N` magic number in any code** — only historical comments. | **DONE (R1.1)** — do NOT re-do; offsets are enumerated+centralized+compile-asserted. |
| OIDN PID-handle fix NOT in tree | R1.3 starts from "does it still repro?" | **STALE — fix IS in the tree.** `Dx12OidnGpuPath.cs:31` `static int shareSeq; // process-wide counter → unique shared-handle names (avoid 0x887A002C)`; lines 119-126/164-166 build names `BallisticOidn*_{Environment.ProcessId}_{Interlocked.Increment(ref shareSeq)}` — per-process+per-instance unique = exactly the NAME_ALREADY_EXISTS fix. | **STALE (fix present)** — R1.3 should re-verify the fix HOLDS (two back-to-back captures), not "does it still repro from scratch." |
| P0.5 unified volume | re-verify before R3.2 | **CONFIRMED done.** `Engine/Rendering/Volumes/Components/` has only `GlobalIllumination.cs` (no `ScreenSpaceGlobalIllumination`/`ScreenSpaceReflections`). It carries `giMode`(ScreenSpace) `reflectionsMode`(ScreenSpace) `enabled`(true) `emissiveAsGi` `screenProbes` `giIsolate` + advanced dials. Volume defaults are **GI-ON**; the bridge overwrites them with Off → that overwrite is the R0.1 gap. **No `GiQuality` enum yet** (R3.2 unstarted, expected). | **CONFIRMED** — P0.5 unified; R3.2 GiQuality not added. |
| P7.0 no-RT auto-downgrade | KEEP, untouched | Present: `Dx12GiPass.cs:25` "Enabled = ctx.GiMode != Off (the giMode resolve + the no-RT auto-downgrade...)". | **CONFIRMED present** — untouched. |

**★ ORDER FINDING (consumed by the runner, not the plan author).** `git log` shows **R1.0 (`e1ccbbf6`), R1.1 (`fa3d6bb6`), R1.2 (`6b7e9565`)** are ALL committed on `dx12-renderer` — but **R0.0/R0.1/R0.2/R0.3/R0.4 were NEVER done**. A prior worker ran R1.x OUT OF the plan's mandated `R0.0 → R0 → R1` order. The plan §5 rule "no R0 before R0.0; no changes before R0 finishes" was violated by that worker. **R0.0a (this chunk) is now the first plan chunk actually executed.** Consequence for the runner: **R0.0b is next** (5 fixtures + `bal validate`), THEN **R0.1 (flip the bridge) → R0.2 → R0.3 → R0.4**, after which R1.x is re-validated against the now-existing R0 baseline (R1.0's "explain any nonzero diff" + re-measured X/noise-floor were done WITHOUT an R0.3 baseline, so they ride on a missing denominator — re-confirm at R2.5).

**DoD met:** No load-bearing claim consumed without a same-session re-grep. **Bridge target confirmed `Off`** (HEAD==tree, lines 66-76). No code changed in this chunk.

### R0.2 measured (the denominator — 2026-06-18, same-session `git`/`grep`/`read` + headless GI-OFF perf)
> PROVISIONAL POLICY applied: re-measured every load-bearing claim against the tree. **NO code change** — measurement chunk. The pre-existing `gi-revival-R0-baseline.md` §R0.2 was written by the OUT-OF-ORDER prior worker (before this R0 run) and is **superseded here**: its "TSR/FSR 4K, Lumen model" line was loose, and its GTX-1660 extrapolation contradicts the rev3+ min target (RTX **2060**, not 1660). The numbers below replace it.

**(1) Upscaler is FSR, NOT TSR — confirmed in tree.** `Abstraction/Rendering/PostProcessSettings.cs:52-59` `enum UpscaleMode { Off, NativeAA(1.0x), Quality(1.5x), Balanced(1.7x), Performance(2.0x), UltraPerformance(3.0x) }` (per-dimension ratios), default `UpscaleMode.Off` (`:133`). Wired via the `Upscaling` volume component (`Engine/Rendering/Volumes/Components/Upscaling.cs` — "AMD FidelityFX FSR") → `Dx12FsrPass` / `Dx12FsrUpscaler` / `native/fsr/`. **No TSR anywhere.** FSR temporal pass REPLACES TAA when mode≠Off.

**(2) Target res + frame budget.** Render substrate = **1920×1080** (headless `Dx12HeadlessRuntime` default; same res the noise-floor json pins). With FSR the 1080p can be EITHER the display target reconstructed from a lower internal res (e.g. Quality → 720p internal → 1080p), OR the internal res reconstructed to 4K — both supported; the budget here is pinned to the **measured 1920×1080 native render** (`UpscaleMode.Off`, what headless actually does). Frame budget: **60fps = 16.6 ms / 30fps = 33.3 ms.**

**(3) Non-GI cost measured (GI-OFF, RX 9070 XT, headless paused frame 60, `BALLISTIC_DX12_SSGI=0 BALLISTIC_FX_SSGI=0`).** ⚠ **HONEST TOOLING CAVEAT:** the only headless GPU signal is `bal perf`/`BALLISTIC_STATS_OUT`: `cpuFrameMs` (full-frame CPU submit wall-time) + `gpuPasses[]` (per-pass **CPU stopwatch** around submit+fence-wait, NOT GPU timestamp queries — `gpuFrameMs` is always 0; "per-pass GPU ms is a DX12 follow-up", CLAUDE.md). Under the pipelined frame (P0a) the per-pass wall-times are ~0.02ms each (fence-wait ≈ 0), so they UNDER-report true GPU exec — use `cpuFrameMs` as the frame proxy, not the pass sum.

| Scene (available locally) | tris | draws | non-GI cpuFrameMs | graph post-pass sum (excl GI[absent]+Refl) | Refl pass ms |
|---|---|---|---|---|---|
| CornellBox | 86 | 1 | 4.74 | ~0.10 | 0.046 |
| LightTest | 1.5k | 2 | 4.62 | ~0.09 | 0.018 |
| SunTemple | 606k | 1 | 3.25 | ~0.08 | 0.015 |

⚠ **Bistro (Interior_Wine / Exterior) = the heavy real-content scenes — gitignored 1.6GB, NOT present locally** (CLAUDE.md repo-facts). The prior baseline doc's Bistro numbers (cpuFrame 7.0–7.4ms GI-on / ~4.0ms GI-off) were measured by a worker who had Bistro; **here they are an OPEN ITEM** — whoever has Bistro re-measures non-GI there (it dominates the real-content denominator). The 3 local scenes are CPU-submit-bound (trivial geometry) so their non-GI cpuFrameMs ≈ **3.3–4.7 ms** is a FLOOR, not the heavy-scene cost.

**(4) GI budget X (PRELIMINARY, pre-R1.0 — WILL TIGHTEN after R1.0; pessimistic X is the gate).**
- **GI-side cost (RX 9070 XT, from §0 + prior baseline, re-grepped):** §0 table SSGI ~4 ms · DDGI ~0.41 · screen-probe ~0.63 · RT-refl ~1.5–2. Prior baseline measured GI-pass cpuFrame ~3.2–4.2 ms (SSGI path only; **stale-but-cited**, re-measured at R0.3 GI-ON + post-R1.0).
- **X on the dev card (RX 9070 XT)** = 16.6 − non-GI. Non-GI ≈ 3.3–4.7 ms (local floor) → **X ≈ 12–13 ms** on the dev card. **The dev card is NOT the constraint** (SSGI ~4 + DDGI ~0.41 + probe ~0.63 + RT-refl ~2 ≈ 7 ms ≪ 12 ms). The real denominator constraint is the **target GPU (RTX 2060)** — see R0.4.
- **Target-GPU (RTX 2060) modeled interval (MODELED-ONLY — no real 2060 on hand; closure path = R0.4 'dev-enable', per user decision this run):** RX 9070 XT (~48 TFLOPS FP32, ~1557 GB/s) → RTX 2060 (~6.5 TFLOPS FP32, ~336 GB/s) for a bandwidth+ALU-bound compute+RT stack: **optimistic ~5×, pessimistic ~8×** slowdown (RT-core path is non-linear — model RT separately + conservatively per R0.4).
  - Non-GI on 2060 (model): 3.3–4.7 ms × 5–8 = **~17–38 ms** for the LOCAL floor scenes ALONE → **already over 16.6 ms at the pessimistic end even before GI.** This mirrors the prior baseline's GTX-1660 finding (SSGI alone 20–37 ms) but at the corrected 2060 class.
  - **★ PRELIMINARY GATE (pessimistic, 2060, 1080p native, will tighten post-R1.0):** at 8× the non-GI floor alone (~26–38 ms) blows the 60fps budget → **a 60fps@1080p-native target on 2060 is NOT credible for the heavy path; the realistic target is 30fps@1080p (33 ms) OR 60fps via FSR (internal res < 1080p).** GI budget X under 30fps@1080p-native-on-2060: 33 − (17–38)non-GI = **X ∈ [−5 .. +16] ms (pessimistic .. optimistic)** — i.e. at the pessimistic model GI does NOT fit even at 30fps@native, and **FSR (lower internal res) is REQUIRED**, not optional. Under FSR-Quality (720p internal, ~2.25× fewer pixels) the per-pixel passes scale down ~2.25× → non-GI ~7.5–17 ms on 2060 → **X ∈ [16 .. 25] ms @ 30fps**, comfortably fitting SSGI+DDGI+probe+RT-refl (~7 ms dev → modeled ~35–56 ms?? — NO: GI passes are also FSR-internal-res for screen-space parts; DDGI/RT are res-independent). **This is the crux R2.1 must resolve with the real preset math; flagged PRELIMINARY.**
- **R2.1 High-fit, modeled on current P8.0 refl cost (~1.5–2 ms RX9070XT, §0):** flagged for **RE-RUN if R2.3 changes the reflection path** (cache vs re-shade roughness-split shifts the refl cost).

**R0.2 DoD met:** target res (1080p) + FSR-not-TSR confirmed + frame budget (16.6/33.3 ms) WRITTEN; non-GI measured (GI-OFF, 3 local scenes, GPU-safe, zero device-removal) + RX9070XT→2060 extrapolation method (5–8×, RT separate) WRITTEN; GI budget X computed as a PESSIMISTIC/OPTIMISTIC interval, marked **PRELIMINARY / will-tighten-post-R1.0**, pessimistic X is the gate. Key finding the gate hangs on: **on the modeled 2060, FSR (internal res < 1080p) is REQUIRED for the heavy path; 60fps@1080p-native is not credible — target is 30fps@native or 60fps@FSR.** No code changed.

## §0. Keep / drop
**KEEP (committed; "VERIFIED-under-WHICH-content", re-verified in R0.3):**
| Piece | ms (RX 9070 XT) | Role / asterisk |
|---|---|---|
| SSGI (SSILVB) | ~4 | on-screen FAST update (few-frame latency, §R2.2) |
| DDGI world cache (Majercik 2019) | ~0.41 | off-screen far-field, loose round-robin (latency spent here) |
| Screen-space radiance probes (PRIMARY) | ~0.63 | on-screen FAST update |
| RT-GI hit shading (inline RayQuery) | — | ⚠ verified only on per-triangle-MaterialId meshes; NOT color-only/whole-mesh → R1.0 |
| OIDN + temporal | — | ⚠ R1.3 handle-fix claim stale → re-verify |
| RT reflections via world cache | ~1.5–2 | ⚠ "Hit Lighting" (sharp re-shade) vs "via cache" (blurry)? → R2.3 measure + roughness-split |
| Emissive-as-GI | — | ⚠ same MaterialId path → no bounce on color-only → R1.0 |

**DROP (measured-out — see Part B):** Surface Cache mesh-cards · Software RT (SDF/GDF) · No-RT raster-proxy (225ms) · **No-RT Low floor (deferred)** · Surfels.

**Stack = screen-probe + SSGI (on-screen) → DDGI far-field (off-screen) → cascaded boundary → IBL/sky + RT reflections (roughness-split) + emissive-GI. No new technique.**

## §2. Operative spine (order: R0.0 → R0 → R1 → R2 → R3 → [R3+]; each: build → A/B verify → budget-measure → commit)

| Phase | Action | DoD / gate |
|---|---|---|
| **R0.0a** Re-ground | Apply PROVISIONAL POLICY: confirm bridge still `Off` in tree; confirm shader diff is separate+untouched; re-grep each memory claim at first use (offsets, OIDN, P7.0/P7.1/P0.5-volume). | No load-bearing claim consumed without a same-session re-grep. Bridge target confirmed `Off`. |
| **R0.0b** Scenes | Build 5 fixtures + `bal validate`: (1) outdoor (cascaded test), (2) multi-light interior (≥8 punctual), (3) whole-mesh single-submesh color-only emissive+albedo, (4) thin-wall (leak), (5) moving-light (two-rate). | Scene-3: **assert `Dx12RtGeometry` MaterialId buffer actually EMPTY** (else R1.0 gets false "fine"). Scenes 1/2: **assert ≥1 whole-mesh renderer present** (else bug invisible till R2). |
| **R0.1** Flip bridge | Bind [VolumePostProcessing.cs:66-76] to live volume values; [PostProcessSettings.cs:141-143] defaults → GI/SSR-on; unlock editor toggles. **Leave shader diff untouched.** Define precedence: **volume authoritative, env-door = debug override.** | GI driven by volume (shipping path), not just env door. Precedence defined HERE, not R3. Window labeled "dev-only, R1.0-incomplete". |
| **R0.2** Denominator | Write target res (1080p→FSR; confirm engine has FSR not TSR) + frame budget (60fps=16.6ms/30fps=33ms). Measure non-GI (direct+shadow+post) extrapolated → **GI budget X (pessimistic/optimistic interval).** | X is **PRELIMINARY (pre-R1.0)** — marked "will tighten". Pessimistic X is the gate. R2.1 High-fit modeled on current P8.0 refl cost, flagged for re-run if R2.3 changes it. |
| **R0.3** Baseline | Capture 5 scenarios on the **SHIPPING path (volume-on), not env-door.** GI-isolate + composite + per-pass ms + two-rate latency (scene-5). | **Characterize determinism** (which passes deterministic vs not → byte-identical BLIND-marked on the rest). **Measure per-pass perceptual noise-floor** → imgdiff gate = noise-floor+margin. ⚠ both X AND noise-floor are pre-R1.0 → **re-measured after R1.0**. |
| **R0.4** Extrapolation | All RT-min numbers are MODEL (no 2060 on hand). RT budget modeled **separately + conservatively** (RX9070XT→2060 RT ratio non-linear). | Two-stage closure: **(a) dev-enable** (model suffices, closes R3) / **(b) target-met** (real 2060/3060, ship-gating). Closure path = **explicit user decision before R2 sign-off** (borrow/cloud/telemetry/accept-modeled), never a silent default. |
| **★ R1.0** MaterialId | Move per-triangle MaterialId from raster G-buffer path (submesh-range→triangle) into RT geometry-build. R1's BIGGEST item; **changes BVH/geometry-build → R1.1 + MegaLights depend on it → FIRST.** | (a) repro scene-3 "no bounce"; (b) move it; (c) **soft-gate**: color-only now bounces+bleeds; imported mesh NOT hard byte-identical — **explain any nonzero diff** (unexplained = regression). **Then re-measure X + noise-floor.** |
| **R1.1** Bindless tail | Replace manual `16384-N` bases with one `BindlessTailAllocator` (compile-time asserted). | Enumerate offsets **from the tree at R1.1 time** (R1.0 shifted them); zero hand-listed integers. Zero manual magic numbers after. |
| **R1.2** Barrier audit | Adversarial barrier-audit workflow (4–6 reviewers) + GBV oracle for UAV↔SRV asymmetries. | DRED+GBV (TdrDelay-raised, §4) + 5+ clean launches no-removal. |
| **R1.3** OIDN crash | PID-handle fix NOT in tree → does 2nd-run `NAME_ALREADY_EXISTS` still repro? If yes fix; if no, find what fixed it + readback fallback default-safe. | Two back-to-back zero-copy captures both succeed. |
| **R2.1** Presets | **High** (2060): screen-probe+SSGI + DDGI far-field + RT-refl roughness-split + emissive. **Epic** (3070+): +more update/rays/res. **Low** deferred. | Calibration: "all of High fits 2060 at target-fps" — if R0.2 refutes, switch to Epic-only-3070. |
| **★ R2.2** Two rates | On-screen → **fast** (SSGI+screen-probe every frame; few-frame latency, NOT "instant"). Off-screen → loose DDGI round-robin (1/8→1/16; few-seconds, accepted). Optional: priority-update near changed lights. | Budget on-screen latency (≤ N frames / ~100ms) + off-screen latency (≤ few s); derive round-robin rate. |
| **★ R2.3** Reflections | Measure what P8.0 actually is (cache vs re-shade) on a 2060. Build roughness-split: rough→cache (cheap), sharp→re-shade-at-hit (clamp rays). | Glossy surface: sharp reflection from re-shade; IBL fallback only OUTSIDE cascaded far-field. |
| **★ R2.4** Cascaded + cull | Finite volume (~30m near + clipmap fade); distant horizon → IBL/sky (**intentional**, document it). Culling = perf lever only. | ⚠ **GUARD: never cull geometry a probe's visibility depends on** (aggressive culling creates leaks). Leak-test PASSES with culling ON. |
| **R2.5** VRAM | Budget the real cost: **BLAS/TLAS acceleration structures** (not the tiny DDGI/probe buffers). Tie to preset. | Per preset: reference-GPU-extrapolated total GI ms + AS VRAM on all 5 scenes, **vs re-measured (post-R1.0/R2.3) X**. |
| **R3.1** Doors | Sub-system toggles (SSGI/DDGI/SCREENPROBE) → **debug-only, NOT deleted** (bisect tools). Off the shipping surface, not the code path. | `BALLISTIC_DX12_.*GI` grep returns only debug doors. |
| **R3.2** Volume | Add `GiQuality (High/Epic)` enum to the unified `GlobalIllumination` volume (re-verify P0.5-unified first); advanced knobs derive from preset. Use the inspector attribute pipeline (no new type-switch). | GI behavior changes ONLY via `GiMode` + `GiQuality`. |
| **R3.3** Maintainability | — | DoD: 1 enum + 1 preset control GI; zero manual bindless magic numbers; grep = debug doors only; Part B records kept/dropped+why. |
| **R3+** Auto-downgrade | OPTIONAL, off critical path. Wall-clock-ms preset-drop on the P7.0 gate. | Determinism-gate (OFF under DETERMINISTIC) + forced-transition test + hysteresis (median-over-N). "Fits" proof does NOT depend on it. |

## §3. Out of scope
Surface Cache mesh-cards · SWRT SDF/GDF · raster-proxy · No-RT Low (deferred) · new denoiser (OIDN suffices) · MegaLights stochastic direct lighting (later plan; benefits from R1.0+R1.1) · per-cvar front-door tuning (APV anti-pattern).

## §4. Verification gates (every phase)
- **GI-isolate A/B** — never composite mean.
- **Leak test** (scene-4) — no bleed-through; **PASS with culling ON** (R2.4 guard).
- **Two-rate latency** (scene-5) — on-screen ≤ budget (few frames), off-screen ≤ budget (few s); measure the two **separately**.
- **Reflection** (glossy surface) — sharp from re-shade, near/mid never falls to IBL.
- **byte-identical = SMOKE CHECK only** — valid only where R0.3 proved determinism holds; **explicitly BLIND** elsewhere. Real oracle = GBV + GI-isolate visual A/B + R1.0 "explain any nonzero diff".
- **Perceptual diff** (`bal imgdiff`, non-deterministic passes) — gate = **R0.3 noise-floor + margin (re-measured post-R1.0)**, never arbitrary.
- **GBV + TDR** — GBV is the real oracle for new code; **raise TdrDelay (~60s)** (full-disable only for one capture); a hang counts only if it **repros with GBV OFF**; DRED always-on (VA=0⇒bad-bind, VA≠0⇒UAF).
- **Target-GPU budget** — per-preset reference-GPU-extrapolated, pessimistic-X gate; **target-met awaits real 2060/3060** (R0.4).
- **Adversarial wiring audit** — 4–6 reviewers for every GPU-hang-sensitive change.
- **fail → rollback** — revert to last green commit, inspect offline, NO relaunch-loop.

## §5. Execution (plan-runner compatible)
- cwd: `e:/Unity Projects/Ballistic-Engine`, branch `dx12-renderer`. **Runs autonomously** (re-enable is committed; the only uncommitted work is separable shader work — leave it).
- **`git add -A` FORBIDDEN.** Stage only the GI files you touch; **do not stage the uncommitted shader diff** (separate, not yours to commit here).
- New shader → CPU-DXC via `%TEMP%/bal-ddgi-shadertest` before launch.
- Capture recipe: P0 recipe (memory `dx12-lumen-gi-p0-2026-06-16`) — **re-verify its env doors against the tree first** (POLICY).
- Before a GBV run: raise `TdrDelay` (§4); restore after.
- Order: **R0.0 → R0 → R1 (R1.0 first) → R2 → R3 → [R3+]**. No R0 before R0.0; no changes before R0 finishes.

---

# PART B — THE RATIONALE (why)

## Why "not Lumen" (the core pivot)
User decision (2026-06-18): *"full Lumen is both a perf and an implementation problem; we can use its pieces but the real point is to land on something more shippable."* The prior plan ([dx12-lumen-gi-plan.md](dx12-lumen-gi-plan.md)) built real Lumen on DXR through P0–P8, then the system was disabled 2026-06-17. This plan re-enables a *subset* and hardens it, rather than chasing feature-parity.

Deep-research on UE Lumen (2026-06-18, 17 sources, SIGGRAPH 2022 + Epic docs + Narkowicz) confirmed the pivot is sound engineering, not a shortcut:
- **Surface Cache mesh-cards** need per-import bake (bake forbidden here) + resurrect the deleted SDF/GDF tracer. We use DDGI instead — but honestly: DDGI does NOT fully replace it (see quality-ceiling below).
- **Software RT (SDF/GDF)** was Lumen's *historical* necessity (no RT GPUs existed in 2018). Our min target (RTX 2060) has RT cores → no reason to imitate SWRT.
- **Lumen's hybrid pipeline** (Screen → SWRT/HWRT → Skylight, each handing off via TraceDistance+hit-flag) is the same shape as our screen-probe → DDGI → IBL stack. The architecture we kept is the right one.

## Honest quality ceiling (don't let future-self be surprised)
Saying "DDGI delivers Surface Cache's payoff" is FALSE. DDGI far-field = low-frequency. Surface Cache gives off-screen near-field detail + reflection off real surface albedo. Our stack: on-screen near-field → screen-space (disocclusion-limited); off-screen low-freq → DDGI; **off-screen high-frequency near-field → nobody solves it = ACCEPTED LOSS.** On the *reflection* side this partly closes — the roughness-split re-shade branch real-traces off-screen geometry on sharp surfaces (viable on 2060). Ceiling stays for diffuse, not glossy reflection.

## What was eliminated by measurement (not opinion)
- **No-RT raster-proxy fallback:** measured 128 probes ≈ 225ms. Non-viable, permanently dropped.
- **No-RT Low survival floor (SSGI+IBL+SSAO):** deferred. Min floor is RT-capable (Steam HW survey: RT-less cards are a shrinking minority). Building it now = premature opt. P7.1 code stays, untouched.
- **Surfels (GIBS):** marginal on interior fixtures + highest device-removal risk. Only if content exceeds the cascaded DDGI volume.

## The rev-by-rev correction trail (why the plan reads the way it does)
The plan went through 7 revisions, each fixing a class of error the previous missed. The meta-lesson — which became the PROVISIONAL POLICY — is that **every revision kept finding the previous one had trusted a stale or unverified claim**, and the fix kept being applied locally while the staleness was global.
- **rev1→rev2 (first adversarial pass):** added R0.0 precondition gate, nailed the budget denominator, per-preset reference GPU (not one scalar to GTX-1660), MaterialId bug → sub-phase, auto-downgrade off the critical path, env-doors debug-only (not deleted), GBV-oracle + TDR-footgun rule, honest quality ceiling.
- **rev3 (requirement clarification, user):** RT-min floor = 2060; reflections mandatory → RT-refl to High; roughness-split; two update rates; leak ≠ culling (+ the guard that aggressive culling *creates* leaks); cascaded far-field correctly falls to IBL; two-stage closure.
- **rev4 (first git look):** discovered rev1–3 were written against a stale 2026-06-17 memory snapshot.
- **rev5 (re-ground):** demoted byte-identical to smoke-check (determinism is a partial denylist, confirmed in code); found the OIDN handle-fix claim stale; **but** declared its own headline ("the +602 uncommitted blob is ours → autonomous") from commit *logs* — the same crime it was built to fix.
- **rev6 (re-measure the headline):** proved the "+602 uncommitted WIP" never existed — that `--stat` predated the commits landing (both now HEAD ancestors, `git merge-base --is-ancestor`). Real working tree = small shader work (196/51, mostly `.hlsl`), separate, untouched. The `GI bridge` commit *overpromised* — bridge still forces `Off` in HEAD and tree (`git show HEAD:` == tree), so R0.1 is genuinely unstarted. Generalized PROVISIONAL into a POLICY: re-measure every decision-gating claim at first load-bearing use, **headline included.**
- **rev7 (single-file consolidation):** the doc had grown to 220 lines / 30 annotations — the disease R3 fixes in code, now in the doc, with §5 (the runner's executed contract) still carrying rev5's dead "finish/commit the +602" reasoning. Fixed §5 to the measured reason; corrected the three incomplete-propagation cracks the rev6 review caught (below); kept everything in ONE file (Part A spine + Part B rationale).

## rev6-review cracks corrected in rev7 (incomplete propagation, not new architecture)
1. **§5 head-tail inconsistency:** §5 still told the runner to "finish/commit the in-flight +602 gi-revival work" and justified autonomy with "(rev5: ...)" — the exact premise rev6's headline voided. Autonomy is still correct, but for the *right* reason: the re-enable is already committed; the only uncommitted work is separable shader work. Fixed in §5.
2. **Re-measure rule applied to X but not the oracle:** R0.3 produces two things R1.0 invalidates — the budget X *and* the perceptual noise-floor + determinism characterization. R1.0 changes what DDGI/RT-GI compute (more lit hits) → temporal-noise characteristics may shift → an R0.3 (pre-R1.0) noise-floor may not be the post-R1.0 floor, re-miscalibrating the §4 imgdiff gate. The "re-measure after R1.0" rule now binds BOTH the budget and the oracle-calibration.
3. **"INSTANT" is an overclaim:** on-screen near-field uses SSGI history + screen-probe bilateral/temporal — these accumulate history, history = latency. On-screen moving light converges over a few frames (less than DDGI, not zero). So scene-5 needs an on-screen latency *budget* (≤ N frames / ≤ ~100ms), not "instant vs slow." Two-rate = "fast (few frames) vs slow (few seconds)," not "instant vs slow."
4. **Ownership inferred from log, state from diff:** rev6 proved a commit *message* can overpromise (says "wired," tree says "Off"). So a log subject describes intent, not realized state — and the same applies to provenance: ownership claims come from diff content, not log-subject/author. (`mergetest` author + log subject was weak evidence; the user confirmation is what makes it solid — and per (a) below, that confirmation is itself a claim, recorded here as the source.)
5. **(a) "user-confirmed" is itself a claim:** the shader diff being "mine, leave alone" was confirmed by the user in conversation 2026-06-18 (AskUserQuestion: "It's all mine/this session — no gate needed"). Recorded here as the provenance source so the plan can state it as fact without an unshown assertion.

## Standing verification doctrine (the *why* behind §4's gates)
- **GI-isolate, never composite mean** — judging GI on a bright composite hides everything (the original P0 burnout lesson).
- **byte-identical is a SMOKE CHECK, not a hard gate** — `DeterministicCapture` is a hand-maintained denylist (TAA/grain/GTAO/SSGI-history/DDGI turned off), not a guarantee; a regression in a pass *not* on the denylist leaves the SHA unchanged and passes silently. Real oracle = GBV + GI-isolate visual A/B + "explain any nonzero diff." (rev5 decision; the answer to "what's the deal with byte-identical.")
- **GBV + TDR footgun** — GBV is 10–100× slower → trips the 2s TDR watchdog → FALSE device-removal. Raise TdrDelay (~60s) by default; full-disable only for one controlled capture (a real hang = power-cycle = lost work); always check a hang reproduces with GBV OFF before treating it as real.
- **Target-GPU budget** — RX 9070 XT alone is never sufficient; the headline "fits on RT-min HW" stays a *model* until measured on a real 2060/3060. How that closes (borrow / cloud GPU / Steam telemetry / accept "permanently modeled") is a user decision, surfaced before R2 sign-off — not a default that sets in by neglect.
- **Adversarial wiring audit** — 4–6 reviewer workflow for every GPU-hang-sensitive change (proven pattern).
- **fail → rollback** — failed gate → revert to last green commit, inspect offline, NO relaunch-loop.
