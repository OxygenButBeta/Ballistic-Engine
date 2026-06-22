# DX12 Aerial Perspective — Complete Rework (Hillaire 3D AP LUT)

**Date:** 2026-06-17 · **Branch:** dx12-renderer

## Problem (user report + diagnosis)

The aerial-perspective haze is badly broken: a flat blue-white veil washes the whole mid/far
scene, killing contrast and colour (see the user's street-vista screenshots). The V3 "near-field
fade" (`b597451d`) only masked it for interiors — an exterior vista is all far-field and gets the
full broken veil.

Root cause — the old [`AerialPerspective.hlsl`](../../BallisticEngine.DX12/Shaders/AerialPerspective.hlsl)
is an ad-hoc analytic hack **not coupled to the sky model**:

1. **Fake optical depth** — `grey = (dist/Distance)*Strength`, a linear ramp, not `exp(-β·d)`.
   At `Distance=1200 m` over a hundreds-of-metres vista every far pixel gets a large uniform `grey`.
2. **Lux-scaled hardcoded tint** — `hazeColor = SkyTint·(...)·2 + SkyTint`, `SkyTint = sunRadiance·(0.10,0.16,0.32)`.
   The sun is ~80000 (lux-scaled), so the additive inscatter is enormous + blue → the veil.
3. **Scalar-transmittance mismatch** — dims the scene by a luma-averaged `avgT` while adding coloured
   inscatter, so dimming and colour disagree → the milky desaturation.

## Approach (decided with user): Hillaire 3D AP LUT + AerialPerspective Volume

Unreal/Hillaire 2020 aerial perspective: a small **froxel volume** (32×32×32 RGBA16F) baked from the
camera each frame, storing per-froxel **accumulated single-scatter inscatter (rgb)** + **mean
transmittance (a)** of a Rayleigh/Mie march out to that froxel's view distance, using the **exact
atmosphere constants the sky uses** (`SkyTransmittance.hlsl`/`ProceduralSky.hlsl`). The AP pass samples
the volume by `(screenUV, linearViewDistance)` and applies `scene·T + inscatter` — distant geometry
fades into the *same* colour as the sky behind it. Physically correct, near-zero per-pixel cost.

Control via a new **`AerialPerspective` Volume component** (engine doctrine: every feature is a Volume
override), wired through `VolumePostProcessing` → `PostProcessSettings` → the pass.

## Pieces

| # | File | Change |
|---|---|---|
| 1 | `Engine/Rendering/Volumes/Components/AerialPerspective.cs` | NEW volume component (enabled/intensity/startDistance/maxDistance/skyAffectsScene). |
| 2 | `Abstraction/Rendering/PostProcessSettings.cs` | NEW `AerialPerspective*` fields (defaults = current shipped look). |
| 3 | `Engine/Rendering/Volumes/VolumePostProcessing.cs` | Map the new component → PostFX. |
| 4 | `BallisticEngine.DX12/Shaders/AerialPerspectiveLut.hlsl` | NEW froxel-volume bake (CS): per-froxel single-scatter march, sky-matched β. |
| 5 | `BallisticEngine.DX12/Shaders/AerialPerspective.hlsl` | REWRITE: sample the 3D LUT by (uv, linear dist), `scene·T + inscatter` via the existing fog-style blend. |
| 6 | `BallisticEngine.DX12/Dx12AerialPerspectiveLut.cs` | NEW: owns the 3D volume + bake PSO, `Bake(...)` per frame. |
| 7 | `BallisticEngine.DX12/Resources/Dx12AerialPerspectivePass.cs` | Rewrite Record: bake the LUT, bind it, read PostFX, blend. |
| 8 | `BallisticEngine.DX12/Resources/Dx12FrameContext.cs` | thread the AP LUT (orchestrator-owned) if needed. |

## Invariants / safety

- **GPU-hang rule:** build once, verify headless (`bal render`), never relaunch a hanging build.
- **Golden set:** AP is part of the default render → re-freeze affected golden frames after the look
  is approved (`Docs/Validation/dx12-golden-set.json`). Do NOT silently break the SHA gate.
- **Doors:** `BALLISTIC_DX12_AP=0` still fully disables. New env knobs documented in the pass.
- Defaults chosen so a scene with NO AerialPerspective volume keeps a tasteful, correct vista haze and
  interiors stay clean (the LUT's physical `exp(-βd)` does this for free — no near-field hack needed).

## Outcome (verified headless, dx12-renderer, build once per config — no GPU hang)

DONE. All 8 pieces implemented + verified deterministic-paused (`bal render` + `bal imgdiff`):

- **BistroInterior_Wine**: AP-on **byte-identical** to AP-off (meanError 0) — the original D2 blue-veil-over-
  interiors bug is fixed at the ROOT (enclosed geometry within StartDistance gets zero haze by construction,
  not by the old near-fade hack).
- **BistroExterior deep vista** (orbit view with receding street/buildings): AP visibly grades the distance —
  meanError 0.0102, maxError 0.27, 6.2% of pixels — smooth, sky-matched cool haze that GROWS with distance.
  No flat blue-white veil. Near café/foreground untouched.
- **BistroExterior near-field** (café-table close-up): ~unchanged (correct — geometry is all close).
- No device-removal across ~12 launches.

**Calibration note:** physical betas (~5e-6/m) are km-scale → invisible over a street. The bake keeps the
per-channel Rayleigh COLOUR ratio but recalibrates MAGNITUDE: extinction ~ `(1/(MaxDistance*0.4)) * DensityScale`
so transmittance hits ~1/e around 40% of the volume depth. Defaults: MaxDistance 2000 m, DensityScale 1,
StartDistance 30 m, Intensity 1. Env A/B knobs: `BALLISTIC_DX12_AP=0` (off), `BALLISTIC_DX12_AP_STRENGTH=<f>`.

**Golden-set:** AP is on the default render path → the exterior golden frames (BistroExterior, SkyTest) now
differ from the pre-rework SHA. Re-freeze `Docs/Validation/dx12-golden-set.json` for the AP-affected views
once the look is signed off (interior frames are byte-identical, unaffected).
