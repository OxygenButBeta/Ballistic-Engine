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

### R0.3 measured (the baseline — 2026-06-18, same-session captures on the SHIPPING path, GPU-safe)
> PROVISIONAL POLICY applied: every load-bearing R0.2/baseline claim re-measured against the tree. **NO code change** — measurement chunk (the documented `DrawSsgi 0xC0000005` crash did NOT repro, so no mitigation was needed). Full data + SHAs: [gi-revival-R0-baseline.md §R0.3 RE-MEASURED](gi-revival-R0-baseline.md) + [Docs/Validation/gi-noise-floor.json](../Validation/gi-noise-floor.json).

- **★ R0.2's "Bistro MISSING locally" is STALE.** Bistro IS present (115M Exterior FBX / 47M Interior / 1.4G Textures, gitignored but on disk) → it WAS re-measured. The OPEN denominator item is CLOSED for this seat.
- **Captured the SHIPPING path** (volume-on, no SSGI env door = `PostFX.GiMode`=ScreenSpace from R0.1), 8 scenarios: the canonical 5 GiFixtures (Outdoor/MultiLightInterior/ColorOnly/ThinWall/MovingLight) + CornellBox + BistroInterior_Wine + BistroExterior. ~34 headless launches, **all EXIT=0, zero device-removal.**
- **GI-isolate A/B (oracle GEÇTİ):** GI pass **present** in every GI-ON capture (3.3–5.1ms), **absent** in every GI-OFF (`SSGI=0` → `Dx12GiPass.Enabled()` false). STRONG correct-direction bounce on CornellBox (isolate 43.1, color-bleed) / Bistro Int (24.4) / Ext (12.3) / MultiLight (115.3) / MovingLight (65.1). **ColorOnly isolate=2.30 = the R1.0 MaterialId bug, visible & pre-R1.0-expected.** ThinWall isolate=0 = leak-pass (no bleed-through). **Outdoor=NO-OP because the scene renders BLACK** (scene-data, like SunTemple — OPEN: re-light the fixture).
- ⚠ **Per-pass ms caveat re-confirmed:** `gpuPasses[]`=CPU stopwatch, `gpuFrameMs`=always 0. But the GI pass shows REAL cost (readback/OIDN forces a fence-wait) — 3.3–5.1ms is a usable GI signal; everything else under-reports. Real frame proxy = `cpuFrameMs` (GI-on 5.6–7.8 / GI-off 3.6–5.2 on the local floor scenes).
- **Determinism CHARACTERIZED (DoD-1):** PAUSED same-frame run-to-run **byte-identical** (CornellBox `cc80835d`, BistroInterior `290f54`); PLAY-mode (scripts+physics, MovingLight) **also byte-identical** (`86052bd2`); motion-dump byte-identical even temporal-active (`4e672f06`); **f60≠f240** (expected — temporal converging). → byte-identical is a VALID smoke-check for the GI passes, BLIND on the §4 denylist passes.
- **Noise-floor MEASURED (DoD-2):** resting (static, converged) GI-isolate boiling **0.027** (CornellBox) / **0.084** (BistroInterior). **§4 imgdiff gate = floor+margin ≈ ≤0.3 mean per-channel delta** for a converged static GI-isolate capture (under-motion 5.8–17.2 = converge-cost, NOT a floor; feeds R2.2).
- **‼ BOTH X (R0.2) AND this noise-floor are PRE-R1.0 → RE-MEASURE + re-freeze the §4 gate after R1.0** (R1.0 lights color-only surfaces → cost + temporal-noise shift; rev7-crack-2).

**R0.3 DoD met:** 8-scenario SHIPPING-path baseline captured GPU-safe (zero device-removal, crash did-not-repro); GI-isolate A/B passes (per-pass GI present/absent + isolate bounce, never composite mean); determinism characterized (paused+play byte-identical run-to-run, f60≠f240, smoke-check validity scoped); per-pass perceptual noise-floor measured → §4 gate written (≤0.3, floor+margin); X and noise-floor both marked PRE-R1.0 / re-measure-post-R1.0. No code changed.

### R0.4 measured (the extrapolation — 2026-06-18, MODELED-ONLY, no real 2060; **dev-enable SELECTED**)
> PROVISIONAL POLICY applied: re-grepped every input from the tree + the R0.3 RE-MEASURED table + the §0 GI-component ms (memory `dx12-lumen-gi-p0-2026-06-16`, NOT memory headline). **NO code, NO capture** — pure model/document chunk. Full model: [gi-revival-R0-baseline.md §"R0.4 RE-WRITTEN — RTX 2060 class"](gi-revival-R0-baseline.md) (the STALE GTX-1660 section is PRESERVED above the new one under its banner). Min target = **RTX 2060 class** (rev3+), NOT GTX-1660 — the old baseline R0.4 was the out-of-order worker's wrong-class model.

- **★ RT and COMPUTE modeled SEPARATELY (the R0.4 mandate, not a single TFLOPS-divide).**
  - **COMPUTE passes** (SSGI SSILVB ~4.2ms, screen-probe gather ~0.18ms — no RT cores, bandwidth+ALU bound): scaled by R0.2's **5× (opt) … 8× (pess)** RX9070XT→2060 interval, ADOPTED unchanged.
  - **RT-CORE passes** (DDGI trace ~0.41ms, RT-GI hit ~4.0ms, RT-refl ~1.5–2.07ms — DXR BVH rays): scaled by a **separate, more pessimistic 8× (opt) … 14× (pess)** because the ratio is **NON-LINEAR** — RX 9070 XT is RDNA4 (2nd-gen+ ray accelerators, ~2× RDNA3 RT throughput) vs RTX 2060 Turing (1st-gen RT cores: no concurrent RT+shading, no SER/OMM). The raw FP32 7.5× is the FLOOR, not the ceiling, for RT work.
- **Inputs = R0.3 RE-MEASURED (3.3–5.1ms whole GI-pass) + §0 re-grepped component ms** — NOT the stale 3.2–4.2 from the old baseline R0.4 table.
- **Modeled 2060, High = screen-probe + SSGI + DDGI + RT-refl** (DDGI gather REPLACES the full RT-GI per-pixel march, memory line 152 — so the gate stack does NOT include the 4.0ms RT-GI hit): GI-on **~42 ms (opt) … ~70 ms (pess)** @ 1080p native; +non-GI (R0.2: 2060 ~17–38ms) = **~59 .. 108 ms = 9–17 fps native.**
- **★ VERDICT (pessimistic gate): 60fps@1080p-native on a 2060 is NOT credible (now QUANTIFIED with GI on, confirming R0.2's pre-GI finding); FSR is MANDATORY.** Modeled **FSR-Quality (720p internal, ~2.25× fewer pixels)**: compute/screen-space passes ÷2.25, **DDGI res-independent (UNCHANGED), RT-refl BVH-floor a touch higher** → total **~28 .. 51 ms** → **fits 30fps (33ms) at the optimistic end, the credible ship target = 30fps@1080p via FSR-Quality, High preset.** 60fps@1080p needs a 3060-class (2nd-gen RT + ~2× FP32) OR a Low preset (SSGI half-res + RT-refl off). **This is the R2.1 preset-math crux** — R0.4 gives the PRE-MODEL, R2.1 finalizes with real per-preset FSR internal-res numbers.
- **Two-stage closure DECIDED (user, this run):** **(a) dev-enable = SELECTED** (model suffices to close R3: target + FSR-mandatory + RT-GI-per-pixel-stays-off established; dev proceeds on RX 9070 XT against this modeled budget). **(b) target-met = PERMANENTLY MODELED** (no real 2060/3060 on hand; ship-gating physical measurement deferred indefinitely, recorded NOT silent — borrow/cloud/telemetry stay open if a card appears; any "fits on RT-min HW" headline stays a MODEL).
- **‼ PRE-R1.0** — every GI-side input is pre-R1.0; R1.0 lights color-only surfaces → more lit RT hits → GI cost rises → **RE-MEASURE this whole model after R1.0** (re-validate at R2.5 with the §4 gate, ORDER FINDING line 44).

**R0.4 DoD met:** RT vs compute modeled separately (compute 5–8× adopted; RT 8–14× strictly more pessimistic + non-linear, RDNA4-vs-Turing justified); inputs from R0.3 RE-MEASURED (3.3–5.1ms) + §0 re-grepped, not stale; FSR-mandatory + 30fps@1080p-FSR-Quality target derived; two-stage closure written, dev-enable SELECTED + target-met PERMANENTLY MODELED; STALE GTX-1660 section preserved, new 2060 section added below it; flagged PRE-R1.0. No code, no capture. **★ FAZ R0 COMPLETE (R0.0a→R0.4).**

### R1.0 RE-VALIDATED (2026-06-18, against the now-existing R0 baseline — GPU-safe ScreenSpace path)
> PROVISIONAL POLICY applied: re-measured e1ccbbf6's content, the R1.0 fix code, the R0.3 ColorOnly=2.30,
> the noise-floor, AND the ancestry of the R0.3 substrate by fresh git/grep/read/headless-capture (NOT the
> handoff/memory). **NO code change** — the fix is correct + committed; this is re-validate + re-measure.
> Full record: [gi-revival-R0-baseline.md §"R1.0 RE-VALIDATED"](gi-revival-R0-baseline.md). Raw:
> `e:/tmp/gi-r1validate/`.

- **★ ORDER-FINDING CORRECTED (the load-bearing discovery).** `git merge-base --is-ancestor 3f3406e9
  928d3fe2` = **YES** → the R1.0 **code fix** (`3f3406e9`, the user's post-FX commit) is an ANCESTOR of the
  R0.2/R0.3 baseline substrate. **The R0 baseline was NEVER a pre-R1.0 denominator on the code level** —
  `3f3406e9` predates every R0.x commit. `e1ccbbf6` itself touches ONLY `gi-revival-R1.0-materialid.md`
  (the repro+verify RECORD). So "PRE-R1.0" on R0.3/R0.4/noise-floor was a *plan-phase ordering* label, not
  a code state — the ORDER FINDING (line 44) over-read it as "ran on a missing denominator."
- **★ The R1.0 fix is RAYTRACED-PATH ONLY → DEAD CODE on the ScreenSpace SSGI shipping path.**
  `ResolveOrRegisterMaterialId` (`Dx12GpuDrivenRenderer.cs:303`) has exactly one caller —
  `Dx12RtGeometry.BuildTriMaterials` (`Dx12RtGeometry.cs:128`) — which feeds the **DXR closest-hit** shaders
  (RT-GI hit + RT reflections). The R0.3 baseline + this re-capture run the ScreenSpace SSGI path, which
  never reads the RT MaterialId buffer. ‼ **The R0.3 doc's "ColorOnly isolate=2.30 = the R1.0 MaterialId
  bug signature" is a MISLABEL** — it is the ScreenSpace SSGI bounce off a small/oblique color-only emitter
  (screen-space-coverage limited), NOT the RT MaterialId degenerate buffer. The MaterialId bug only
  manifests on the RayTraced path (NOT opened headless — device-remove safety, §4 PRE-EXISTING). So
  "ColorOnly isolate should RISE post-R1.0 on the ScreenSpace path" is **false by construction.**
- **Re-captured GI-isolate A/B (ScreenSpace, paused f60, 8 launches, all EXIT=0, ZERO device-removal):**
  CornellBox isolate **43.154** (== R0.3 43.1, strong color-bleed); ColorOnly isolate **2.288** (== R0.3
  2.30, UNCHANGED — fix dead on this path, correct); ThinWall isolate **0.000** (== R0.3 0.00, LEAK-PASS
  HOLDS). Determinism: CornellBox GI-ON `81dbf7a5667f` byte-identical run-to-run.
- **Soft-gate (R1.0(c)) re-confirmed:** ScreenSpace shipping path = **byte-zero diff** vs R0.3 (no
  unexplained regression). The fix is a strict superset on the RT path + dead code on SSGI. The RT-path
  "color-only now bounces" claim is proven by the commit-time **CPU harness CASE 2** (old buffer `[0,…,0]`
  degenerate → fix resolves the real id) — the proper oracle since RT_GI=1 headless SaveBmp is device-unsafe.
- **Re-measured X + noise-floor → §4 gate RE-FROZEN (both UNCHANGED).** Noise-floor (CornellBox static
  motion-dump, temporal active, f180): mean boiling **0.0270** = R0.3's 0.027 byte-for-byte → **§4 gate
  stays ≤0.3** (the floor cannot shift — the fix is dead on the SSGI temporal chain it measures). Budget X:
  the High shipping preset uses DDGI gather (NOT per-pixel RT-GI, R0.4), so the GI-pass input to the X model
  is the ScreenSpace cost, **unchanged post-R1.0** → R0.4's 2060 verdict (FSR mandatory, 30fps@1080p-FSR-
  Quality, dev-enable, target-met permanently-modeled) **stands.** `gi-noise-floor.json` `PreR1_0` → false,
  `R1_0_ReFreeze` recorded.

**R1.0 RE-VALIDATE DoD met:** (a) e1ccbbf6 DoD verified from code (fix present + correct; e1ccbbf6 = doc
record, fix in parent `3f3406e9`); (b) ORDER-FINDING corrected (R0.3 substrate already had the fix → no
missing denominator on the code side); (c) GI-isolate A/B re-captured (ColorOnly 2.288 UNCHANGED+EXPLAINED,
ThinWall 0.000 leak-pass, CornellBox 43.154 no-regress; the "ColorOnly=MaterialId-bug" mislabel corrected);
(d) soft-gate ScreenSpace byte-zero, RT-path correctness via CPU harness CASE 2, RT_GI NOT opened; (e) X +
noise-floor re-measured (both unchanged), §4 gate ≤0.3 RE-FROZEN, R0.4 budget stands; determinism + 8 clean
launches no-removal + build 0-err. **NO code change** (fix already correct). ★ **R1.0 RE-VALIDATED.**

### R1.1 + R1.2 RE-VALIDATED (2026-06-18, HEAD `96c41d4d` + 8-file post-FX WIP — GPU-safe ScreenSpace path)
> PROVISIONAL POLICY applied: re-measured `fa3d6bb6`'s `--stat`, the `Dx12BindlessTail.cs` code, the offset
> enumeration (grep, NEVER hand-listed), `6b7e9565`'s no-code claim, and the §4 GBV gate by fresh
> `git`/`grep`/`read`/build/headless-capture (NOT memory/handoff). **NO code change** — both already committed
> + correct (R1.1 byte-identical pure const refactor; R1.2 doc-only). Both confirmed ANCESTORS of HEAD
> (`git merge-base --is-ancestor` ×2 = YES) → the rebuilt binary contains both. Full record:
> [gi-revival-R0-baseline.md §"R1.1 + R1.2 RE-VALIDATED"](gi-revival-R0-baseline.md). Raw: `e:/tmp/gi-r1revalidate/`.

- **R1.1 (bindless tail `fa3d6bb6`) GEÇTİ:** `--stat` = 4 files (Dx12BindlessTail.cs NEW +105, +3 consumers).
  Code re-read: `HeapCapacity=16384` named ONCE; 4 reserved counts + cap = the ONLY layout inputs; all 4 bases
  DERIVED by cumulative subtraction (RtGi 16376 / DDGI 16372 / ScreenProbe 16368 / RtRefl 16352); 8
  COMPILE-TIME asserts (CS0020 div-guards) verify derived==historical + used≤reserved + tail-sane. **Offsets
  ENUMERATED FROM THE TREE (never hand-listed):** grep `16384` over DX12/ → only the `HeapCapacity` def +
  historical comments + the UNRELATED `Dx12Backend.cs:73` UiHeap (separate ImGui heap) + `IblBake.hlsl:112`
  (unrelated radiance clamp); **NO `16384 - N` computation literal in any active code**; `Dx12GiPass.cs:155-157`
  + `Dx12ReflectionsPass.cs:87` read `Dx12BindlessTail.*`; the base values 16352/16368/16372/16376 appear ONLY
  in the allocator (comments + assert equalities, runtime bases are the derived expressions). **Build 0-err
  on a full DX12 rebuild = compile-time asserts PASSED.** GPU-safe byte-identical smoke (ScreenSpace, paused
  f60): CornellBox GI-ON 43.154/`81dbf7a5667f`, GI-OFF 37.676/`4a50b5b7c70f`, ColorOnly GI-ON 2.288/
  `55ec21c5cffb`, GI-OFF 102.172/`e42fe2013a73`, ThinWall 0.000/`30bc4b4368f5` — ALL == R1.0 re-validate ref,
  determinism run2==run1. R1.1 only re-points RT-path SRV table indices (inert on the ScreenSpace shipping
  path) → byte-identical is CORRECT, not a missed regression; the compile-asserted equality is the RT-path
  correctness oracle (RT_GI device-unsafe headless SaveBmp NOT opened, §4 PRE-EXISTING).
- **R1.2 (barrier audit `6b7e9565`) GEÇTİ:** `--stat` = 1 doc file (`gi-revival-R1.2-barrier-audit.md` +110),
  **NO code change CONFIRMED.** Audit (5 DDGI irradianceTex raw-barrier paths + idempotent state-tracked
  helpers) is CLEAN — all UAV-on-entry → UAV-on-exit symmetric. **8 DRED-on headless launches** this
  re-validate (5 R1.1 smoke + ColorOnly-off + MultiLight + ThinWall-run2), ALL EXIT=0, ZERO device-removal,
  ZERO faults. Since R1.1+R1.2 changed NO barrier code, the **GBV signature set is invariant by construction**
  (substrate-matched baseline `dx12-gbv-baseline.json` RX9070XT/driver32.0.31019.2002 holds). **‼ GBV LIVE
  RUN SKIPPED per §4 HARD RULE** — raising TdrDelay needs elevation (`IsAdmin=False`, `TdrDelay NOT SET`=2s
  default); GBV at 2s TDR = false-device-removal/PC-freeze (the documented crash path). GPU-SAFETY constraint,
  not a solvable issue; substitute-evidence path (static audit + baseline invariance + DRED clean launches +
  byte-identical render) is §4-sanctioned for an audit-only no-code chunk. GBV-with-raised-TdrDelay stays OPEN
  for a privileged/real-HW closure (same elevation reason it was skipped before). ★ **FAZ R1 (R1.0/R1.1/R1.2)
  ALL RE-VALIDATED. Sıradaki = R1.3 (OIDN — fix already in tree per R0.0a, re-verify it HOLDS) → R2.1 presets.**

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
| **★ R1.0** MaterialId ✅ | Move per-triangle MaterialId from raster G-buffer path (submesh-range→triangle) into RT geometry-build. R1's BIGGEST item; **changes BVH/geometry-build → R1.1 + MegaLights depend on it → FIRST.** | **DONE + RE-VALIDATED** (`e1ccbbf6` doc; fix in `3f3406e9`). (a) repro+CPU-harness CASE 2 (degenerate buffer); (b) moved (`ResolveOrRegisterMaterialId`→`BuildTriMaterials`); (c) soft-gate: RT path = color-only bounces (CPU CASE 2), SSGI shipping path = byte-zero diff (fix is RT-only dead code there). **Re-measured X + noise-floor = UNCHANGED** (0.0270; §4 gate ≤0.3 RE-FROZEN). ‼ R0.3 "ColorOnly=2.30=MaterialId-bug" MISLABEL corrected (it's the SSGI bounce, not the RT path). |
| **R1.1** Bindless tail | Replace manual `16384-N` bases with one `BindlessTailAllocator` (compile-time asserted). | Enumerate offsets **from the tree at R1.1 time** (R1.0 shifted them); zero hand-listed integers. Zero manual magic numbers after. |
| **R1.2** Barrier audit | Adversarial barrier-audit workflow (4–6 reviewers) + GBV oracle for UAV↔SRV asymmetries. | DRED+GBV (TdrDelay-raised, §4) + 5+ clean launches no-removal. |
| **R1.3** OIDN crash ✅ | PID-handle fix NOT in tree → does 2nd-run `NAME_ALREADY_EXISTS` still repro? If yes fix; if no, find what fixed it + readback fallback default-safe. | **DONE + RE-VERIFIED** (no code change). Fix WAS already in tree (`Dx12OidnGpuPath.cs:31/124-126` shareSeq+PID, commit `b86e2b4a0`; aux `:163-168` `9a799cec2`; both HEAD ancestors). Two back-to-back zero-copy captures BOTH SUCCEED (cap1==cap2 `81dbf7a5667f`, no NAME_ALREADY_EXISTS/0x887A002C, no device-removal); guided 3-handle path also clean. Readback fallback default-safe (`Dx12GiPass.cs:345-389` sticky downgrade). Full record: gi-revival-R0-baseline.md §R1.3. |
| **R2.1** Presets ✅ | **High** (2060): screen-probe+SSGI + DDGI far-field + RT-refl roughness-split + emissive. **Epic** (3070+): +more update/rays/res. **Low** deferred. | **DONE** (no code change — DATA+calibration; `GiQuality` enum WIRING is R3.2). Preset tables WRITTEN over the EXISTING dials (no new technique); calibrated to R0.4 modeled-2060 with per-preset FSR internal-res (High `UpscaleMode=Quality`=720p=2.25× fewer pixels → High @ FSR-Quality ~28–51ms = fits 30fps at the optimistic end = R0.4 verdict). Crux RESOLVED: DDGI-gather-without-per-pixel-RT-GI is the R2.2 wiring dependency → runtime High degrades to `GiMode=ScreenSpace` (byte-identical to shipping path) until R2.2; High-on-2060 stays target (Epic-only fallback NOT triggered). Full record: gi-revival-R0-baseline.md §R2.1. |
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
