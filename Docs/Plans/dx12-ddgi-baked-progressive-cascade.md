# DX12 DDGI — Baked Progressive Cascade GI (high-fidelity, GPU-driven, freeze-on-converge)

**Branch:** `dx12-renderer`  ·  **Owner:** plan-runner  ·  **Date opened:** 2026-06-18

## Why (user intent, verbatim distilled)

The live DDGI is "performanssız + çirkin ghosting". Root cause = it updates the probe field **every
frame** (round-robin trace + EMA temporal). The user wants the **opposite shape**, Lumen-but-baked:

1. **Freeze-on-converge.** Compute the GI once, then FREEZE it → 0 rays/frame at runtime. Ghosting
   becomes structurally impossible (no temporal feedback once frozen) and runtime GI is ~free.
2. **Progressive, non-blocking bake.** Do NOT stall on scene open. The region **around the camera**
   converges first (playable immediately); the rest fills in **amortized over subsequent frames**
   until the whole grid is done, then it freezes.
3. **High fidelity / maks görsellik.** Because the per-frame budget drops to zero once frozen, spend
   the saved budget on QUALITY: denser probes (cascade), more rays/probe, higher oct resolution,
   deeper converge.
4. **Cascaded grid** (user choice): near = dense (detail), far = sparse (coverage). Multi-level.
5. **Reflections feed from the same field** (user: "reflection da bundan beslensin"). The RT/SSR
   reflection path already reads `SampleDdgiField`; it must read the SAME frozen cascade.
6. **As much work on the GPU as possible** (user: "işin ne kadarı gpu da olursa o kadar performans").
   The distance-priority bake QUEUE lives on the GPU — no CPU readback/sync to pick probes. CPU only
   uploads camera pos + frame index; the GPU decides which probes trace this frame.
7. **Auto re-bake** (user choice): a large camera move (outside the converged region) OR a light/sun
   change restarts the progressive bake (again near-first). Manual editor "Rebake GI" button too.

## Invariants that must not regress (carry from CLAUDE.md + memory)

- **DO NOT TOUCH the rest of Lumen** beyond what these features need. RT-GI hit shading, OIDN,
  exposure, the post chain stay byte-identical when the new mode is OFF.
- **Default OFF = byte-identical.** New mode is opt-in (env door first, then a Volume enum). A scene
  that doesn't enable it renders exactly as today. This is the volume-framework contract.
- **Shader edits don't re-embed on incremental build** (memory `dx12-shader-edit-build-gotcha`):
  every chunk that edits `.hlsl` MUST clean `obj/` + grep-verify the embed before trusting a render.
- **NaN scrubs are component SELECT (ternary), never `mix(v,0,flag)`** (memory). Temporal-feedback
  shaders only.
- **Determinism:** `bal render` forces `BALLISTIC_DETERMINISTIC`. Capture-path warm-up already
  exists; the new progressive path must still produce a byte-deterministic PAUSED capture (warm-up
  converges fully before the capture frame — same contract as today's `WarmupEnabled`).
- **No CPU reflection in the render hot path** (memory `pref-no-reflection-render-hotpath`).
- **git add -A FORBIDDEN** — tree has unrelated dirty/untracked files. Stage only this plan's files.

## Oracle (how each chunk proves itself)

- Build: `dotnet build BallisticEngine.slnx` 0-err (root engine csproj, not just Runtime).
- A/B render: `bal render <scene>` with the mode OFF must be SHA-identical to the pre-change golden
  (`Docs/Validation/dx12-golden-set.json` — FLOOR=0). Mode ON is a NEW golden, frozen once it looks
  right (judged by eye + the GI-isolate view, then SHA-pinned).
- DDGI debug: `BALLISTIC_DX12_DDGI_DEBUG=1` → irradiance atlas mean/min/max/nonzero sane (no NaN/Inf).
- GPU validation: GBV 0-NEW errors (`BALLISTIC_DX12_GBV=1` if wired, else DRED clean).
- Progressive proof: a per-frame `[DDGI-BAKE] converged X/Y probes` log shows the queue draining to
  100% over frames, NOT all-at-once, NOT stuck.

Test scenes: `SampleProject/Assets/Default/Main.scene` (Bistro — needs local content), plus a small
always-present scene for headless CI (pick one from `bal map SampleProject`).

---

## CHUNK 0 — Land the half-done freeze edits + establish the A/B baseline

State: `Dx12Ddgi.cs` already has uncommitted partial edits (BakedMode/Rebake/SetBakedMode props,
WarmupIterations Baked branch=128, a `BakedSpacing` helper, a `Spacing` comment) that are NOT yet
wired (BakedSpacing unused; SetBakedMode not called from anywhere). These were a first stab at the
SIMPLE freeze before the scope grew to progressive+cascade.

Do:
1. Read the current `Dx12Ddgi.cs` diff. KEEP the freeze plumbing (BakedMode/Rebake/SetBakedMode), it
   is correct and reused. The blocking-128-warmup branch will be SUPERSEDED by progressive in ch2 —
   leave it for now (it's behind BakedMode, default off, so harmless and A/B-safe).
2. Wire `BakedSpacing` so it's not a dead member: in `Update()`, when `BakedMode`, set `Spacing` to
   the baked spacing ONCE (guard so it doesn't thrash). This is a stopgap single-dense-grid; ch3
   replaces it with the cascade. (Keeps the tree compiling with no dead code.)
3. Verify default-OFF byte-identical: `bal render Main.scene` SHA == current golden. Commit baseline.

DoD: builds 0-err; mode-OFF render SHA-identical to golden; `git commit` the freeze plumbing.
Handoff: confirm whether `Main.scene` renders headless on this seat (memory notes SunTemple renders
black headless / RT_GI device-remove at SaveBmp — pick a scene that actually captures).

---

## CHUNK 1 — GPU-driven distance-priority bake QUEUE (single grid first, no cascade yet)

Goal: replace the blind `probe % N == phase` round-robin with a GPU-decided, camera-distance-priority
progressive bake, ALL on the GPU. Still one grid (cascade is ch3) to isolate this mechanism.

Design (keep it GPU-resident per user intent #6):
- Add a per-probe state field "converged frames" (extend `ProbeState` float4: currently
  {offset.xyz, active}; pack a `convergedCount` / `bakeDone` into a spare — or add a parallel
  `RWStructuredBuffer<uint> ProbeBakeState`). GPU-owned; never read back to CPU for the bake decision.
- New tiny compute pass `DdgiBakeSelect` (or fold into the existing classify): per probe, compute
  `distToCamera` (camera pos comes in via the CBV — add `CamPos` to `DdgiConstants` Params), and a
  priority = not-yet-converged ? (1/dist) : 0. Each frame the trace should run the **K highest-priority
  unconverged probes**. Two implementable options — pick the simpler that holds:
  - (a) **Distance-banded phase:** classify each probe into a distance band ring (band 0 = nearest).
    Frame N traces band `min(N, maxBand)` plus keeps refining nearer bands until converged. Pure
    per-probe test in `ProbeActiveThisFrame` (no sort) — cheapest, fully GPU, good enough for
    "near-first then outward". RECOMMENDED.
  - (b) GPU sort / atomic counter to pick exactly top-K. More precise, more code. Only if (a) looks
    wrong by eye.
- `ProbeActiveThisFrame` (DdgiTrace.hlsl) becomes: active if probe in the current bake wave AND not
  yet converged (convergedCount < target). Same test mirrored in DdgiBlend (CSIrradiance/CSDepth) and
  classify — they ALREADY share the test, so update all three identically (the existing contract).
- A probe's `convergedCount` increments each frame it traces; at `>= ConvergeTarget` it's frozen
  (stops being selected). When ALL probes frozen → `IsBakeComplete` (a GPU→CPU flag is acceptable
  here ONLY as a throttled, non-hot-path readback for the editor progress bar / the freeze log; the
  bake DECISION stays GPU-side).
- CPU side (`Dx12GiPass`): when BakedMode, drive `RunDdgiUpdate(full:false)` every frame (it now means
  "one progressive wave") UNTIL the GPU reports complete, then STOP dispatching trace/blend (freeze)
  — only `DispatchGather` runs. This is the same `if (!frozen)` gate the capture path already uses.

SHADER EDIT — clean obj/ + grep-verify embed (memory gotcha). Route any new compute source through
`Dx12ShaderCompiler` the same way the others are.

DoD: with `BALLISTIC_DX12_DDGI_BAKED=1`, the `[DDGI-BAKE] X/Y` log drains near-first to 100% over
frames then prints `frozen`; after frozen, `GI:DDGI` GPU-pass time is ~0 (gather only); the image is
stable (no ghosting) and matches the live-DDGI look once converged (A/B by eye + GI-isolate).
GOTCHA to record: the determinism contract — a PAUSED capture must still converge fully first.

---

## CHUNK 2 — Max-fidelity converge (rays, oct resolution, deeper target)

Now that the bake is amortized (not per-frame forever), raise quality. Each is independent; measure
VRAM + converged-look after each.
- `RaysPerProbe` 144 → higher (e.g. 256/512) ONLY for the bake (frozen field doesn't pay it at
  runtime). Must match in DdgiTrace `RaysPerProbe()` + the C# const + RayData sizing.
- Irradiance oct texels 6 → 8/10 (sharper indirect), depth 16 → keep or raise. Atlas sizes recompute
  from the constants (already derived) — verify the gather UV math still matches (it derives from
  Params0 texel counts, so it should track).
- `ConvergeTarget` deep enough that the EMA fully settles (the 128-equivalent), since it's one-shot.
- Env doors for each so A/B is cheap: `BALLISTIC_DX12_DDGI_RAYS`, `_OCT`, `_CONVERGE`.

DoD: frozen field visibly higher-detail than live DDGI (finer contact bounce, less trilinear blur),
no NaN/Inf in the atlas dump, VRAM within the dev card budget (log `GridVramBytes`). A/B screenshots
near vs current. Mode-OFF still byte-identical.

---

## CHUNK 3 — Cascaded grid (near dense + far sparse)

Replace the single grid with N cascades (start 2: near @ ~1.0–1.2m dense, far @ ~3–4m sparse),
camera-centered, each its own atlas (or one shared atlas partitioned). The gather + field-sample +
multi-bounce + leak gate all pick the FINEST cascade that contains the sample, falling back outward.
- This is the largest chunk: `Dx12Ddgi` grows a cascade array; `DdgiGather.hlsl` /
  `SampleIrradianceField` / `DdgiTrace` select cascade by sample distance; the bake queue (ch1) runs
  per cascade (near cascade converges first — already implied by distance priority).
- Keep it behind the same BakedMode door; `BALLISTIC_DX12_DDGI_CASCADES=N`.
- Cascade transition must not seam (blend across the cascade boundary — standard clipmap blend).

DoD: near geometry shows dense high-detail GI, far geometry still covered (no black falloff at the
old grid edge), no visible cascade seam, frozen + cheap. A/B vs ch2 single-grid.

---

## CHUNK 4 — Reflections feed from the frozen cascade

The reflection path (`Dx12ReflectionsPass.cs`, `DxrReflections.hlsl:273` `SampleDdgiField`) already
reads the DDGI field for reflected-ray ambient. Make sure it reads the SAME frozen cascade (the
grid constants it gets via `GridConstants()` must be the cascade-aware ones) so reflections inherit
the high-fidelity frozen GI for free, and cost nothing extra per frame once frozen.
- If a dedicated reflection cubemap cache is wanted later, hang it on the same bake queue. For now,
  the field-fed reflection is the deliverable (user: "reflection da bundan beslensin").
- SSR stays as-is (user: "ssr sonra belki ekleriz" — out of scope, just don't break it).

DoD: reflections on smooth surfaces show the frozen GI bounce (not a stale/black ambient), reflection
GPU cost unchanged or lower, mode-OFF byte-identical.

---

## CHUNK 5 — Auto re-bake (large move / light change) + manual button

- Track the converged region center; when the camera leaves it (e.g. moved > a cascade extent) OR the
  sun/main light direction/intensity changes beyond a threshold, call `Rebake()` → the GPU queue
  resets and progressively re-converges near-first. Throttle so it can't thrash.
- Editor: a "Rebake GI" button (RemoteHandlers / a debug window) calling `Rebake()`.
- Light-change detection: hash the sun dir/color + main lights; compare per frame (cheap, CPU-side,
  no per-draw reflection).

DoD: walking far triggers a visible near-first re-converge wave then re-freezes; toggling the sun
re-bakes; idle camera never re-bakes (no thrash); manual button works.

---

## CHUNK 6 — Volume front-door + cleanup

- Add the mode to `GlobalIllumination` volume: extend `GiMode` with `Baked`, OR add a `bakedGi` bool
  + cascade/quality knobs under the Advanced foldout (follow the attribute-driven inspector contract —
  NO hand-rolled widgets). Bridge it: `Dx12GiPass` calls `ddgi.SetBakedMode(volume.bakedGi)` from the
  PostFX read, same place `Ddgi`/`SsgiIntensity` are read.
- Remove the env-door-only stopgaps that the volume now supersedes (keep env doors as overrides for
  headless A/B, just don't make them the only path).
- Update memory + freeze the ON-mode golden set.

DoD: a scene with `GlobalIllumination { giMode: Baked }` bakes-progressively-then-freezes with the
cascade + fidelity, reflections fed, auto-rebake working, all from the inspector. Mode-OFF
byte-identical. Final `bal render` golden frozen + committed.

---

## Running notes / handoff log (each worker appends)

- (ch0) …
