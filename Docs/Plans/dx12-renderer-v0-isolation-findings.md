# DX12 Renderer — V0 Empirical Symptom Isolation (Findings)

**Date:** 2026-06-16
**Branch:** dx12-renderer
**Phase:** V0 of [dx12-renderer-perf-execution-plan.md](dx12-renderer-perf-execution-plan.md) — empirical isolation, **no code changed**.
**Method:** deterministic headless captures (`bal render`, `BALLISTIC_SCREENSHOT_PAUSED=1` +
`BALLISTIC_DETERMINISTIC=1`) of the test-matrix scenes under the engine's real A/B doors, perceptual
diff (`bal imgdiff`) + luma histograms, every visual claim eyeballed and every code claim read at
file:line. All captures + PNGs in `e:/tmp/v0/`.

> **GPU-hang safety:** every build launched **once** per configuration; no relaunch loops. No
> device-removal or hang occurred in any of the ~20 captures. The build runs; the harness works.

---

## TL;DR — three independent, compounding defects own the regression

The "noisy / crawling sparkles / shiny spots in dim areas / raw-ugly" report is **not one bug** — it
is **three independent defects that stack**, plus the report's other suspects are **refuted**:

| Rank | Symptom | Owner pass | File:line | Verdict |
|---|---|---|---|---|
| **D1** | Over-bright / milky white-out / washed-out low contrast (the dominant "raw-ugly") | **Auto-exposure meter** overshoots on the DX12 raw-radiance scale | `LumAverage.hlsl:53-54` + `Composite.hlsl:111-117` + `DX12HDRenderer.cs:3569` | **CONFIRMED** |
| **D2** | Pink/blue **colour haze veil** over the whole frame (incl. interiors) | **Aerial perspective** runs at `Strength=1` over any scene with a ProceduralSky, no indoor/distance gate | `DX12HDRenderer.cs:3660` + `AerialPerspective.hlsl:79-83,52` | **CONFIRMED** |
| **D3** | **Crawling sparkles / fireflies**, densest *at the point-light fixtures* | **Punctual near-field spike**: inverse-square clamped at `1e-4` ⇒ up to **10 000× radiance** sub-metre from a light | `DeferredLighting.hlsl:134` | **CONFIRMED** |

These three explain **all four** reported symptoms. D1 is the biggest contributor and also *amplifies*
D3 (over-exposure turns sub-visible fireflies into a screen-eating speckle field). The original plan's
remaining suspects are **refuted by experiment** (below).

---

## The proof (per defect)

### D1 — Auto-exposure overshoot owns the white-out  ★ biggest

**Experiment (BistroInterior_Wine, AP forced off to remove the D2 confound):**

| Capture | meanLuma | blackCrush | highlightClip | reading |
|---|---|---|---|---|
| default (no exposure override) | **198.9** | 0.03% | 0.35% | blown white-out |
| `BALLISTIC_DX12_AUTOEXP=1` (force Automatic) | **198.9** | 0.03% | 0.35% | **byte-identical to default** |
| `BALLISTIC_DX12_EXPOSURE=1e-5` (manual) | **75.0** | 0.66% | 0.00% | correct dim restaurant |
| `BALLISTIC_DX12_EXPOSURE=3e-6` | 46.2 | 6.8% | 0% | a touch dark |
| `BALLISTIC_DX12_VOLUMES=0` (PostFX defaults) | 131.8 | 0% | 0% | balanced (different default path) |

- **default == forced-Automatic, byte-for-byte** ⇒ the scene's default exposure mode **is Automatic**,
  and the Automatic meter is resolving to a hugely over-bright exposure. A manual `1e-5` lands the
  scene correctly (meanLuma 75) — the meter overshoots correct exposure by ~2.5× brightness.
- **Root cause** — `LumAverage.hlsl:53`:
  ```hlsl
  float meteredEv = log2(max(avgLum, 1e-6)) + LuminanceToEV - PleasingBias;
  meteredEv = clamp(meteredEv, LimitMin, LimitMax);   // :54
  ```
  The meter assumes `avgLum` is **true-lux radiance**, but the DX12 HDR target is on an arbitrary
  `~1e-5` prescale (documented in [[dx12-autoexposure-runaway-fix]], "NOT true lux"). So `avgLum` is
  tiny, `log2(tiny)` is strongly negative, `meteredEv` slams into `LimitMin`, and the composite turns
  that floor EV into a *large* multiplier (`Composite.hlsl:114`,
  `LegacyMul / (1.2 * exp2(ev - Compensation))`) → blow-out. The meter is ~16-30 stops miscalibrated
  against the scene's radiance units. The header even flags the missing piece: "Eye-adaptation EMA +
  metering-weight modes / histogram are a follow-up."
- **Why some scenes look OK:** CornellBox (73) and TransparentTest (126) are *also* over-exposed but
  less catastrophically (their radiance scale happens to land the meter closer); SkyTest and both
  Bistro scenes blow out. The defect is in the **meter↔radiance-unit calibration**, surfacing
  scene-dependently — exactly V1's territory.

This is the highest-leverage fix and the V1 entry point.

### D2 — Aerial perspective owns the colour haze veil

**Experiment (BistroInterior_Wine):** `BALLISTIC_DX12_AP=0` removes the **pink/blue veil completely**
— colours snap back (red brick, wood, the green chair) — while every other symptom (white-out,
sparkles) **remains**. So AP owns the colour veil and *only* the colour veil. Confirmed visually on
the interior, and the same pale veil is visible on BistroExterior and SkyTest.

- **Root cause** — `DX12HDRenderer.cs:3660`: `float strength = 1f;` — AP is **ON at full strength**
  whenever `ProceduralSky.Active is not null` (`:2058`), with **no interior/short-distance gate**. The
  BistroInterior is a tens-of-metres room but still gets the full atmospheric-haze pass.
- The veil colour is the blue-biased sky tint `sunRadiance * (0.10, 0.16, 0.32)` (`:3658`), added as
  `inscatter = hazeColor * (1 - transmittance)` (`AerialPerspective.hlsl:83`) with a `sunHaze * 2.0`
  boost (`:79`) over **every opaque pixel**.
- The suspected **dead `Exposure` constant is real**: passed as `Exposure = 1f` (`DX12HDRenderer.cs:3674`),
  declared at `AerialPerspective.hlsl:19`, **never referenced** in the shader body. Intent is
  ambiguous — V3 should wire or delete it.
- Pass is correctly `discard`-gated on `Strength <= 0` and `depth >= 1` (`AerialPerspective.hlsl:52`),
  so `Strength=0` is a clean off — but the **default is 1**, so it's on everywhere.

V3 target: gate AP off for interiors / by physical view distance, and/or drop the default strength.

### D3 — Punctual near-field spike owns the fireflies

**Experiment:** the sparkle speckle is **densest right at the hanging-lamp fixtures** (zoom crop
`e:/tmp/v0/sparkle_crop.png`), thinning with distance from each lamp. It **survives GI off**
(`BALLISTIC_DX12_SSGI=0`), **survives SSAO off**, and **survives reflection-mode swap** — but it
**fades out as exposure drops** (`exp_dark` is clean) and is **reduced-but-still-present at correct
manual exposure** (`exp_1e-5` — genuine fireflies, not just an exposure artifact).

- A pure-punctual scene with lights at *normal* distance from surfaces (**LightTest**) is **completely
  clean** — smooth coloured falloff, zero fireflies. So punctual lighting is fine in general; the spike
  is a **near-field** phenomenon.
- The Bistro `Point Light` is `lumens: 1500, range: 10, sourceRadius: 0` — a bright **zero-radius**
  bulb whose lamp-shade geometry sits centimetres away.
- **Root cause** — `DeferredLighting.hlsl:132-136`:
  ```hlsl
  float d2  = dist * dist;
  float inv = 1.0 / max(d2, 1e-4);   // :134  → at dist≈1 cm, d2≈1e-4 ⇒ inv≈10000
  ```
  At sub-centimetre range the inverse-square term is clamped to **10 000×**, so the shade interior
  receives a ~10 000× radiance pop → fireflies clustered at the fixtures. The comment says "GL parity",
  so the clamp is inherited, **but** the merge's exposure blow-out (D1) amplified previously-tolerable
  near-field pops into the visible crawling speckle. V2 target: a physical near-field softening
  (e.g. windowed `1/(d²+r²)` with a source radius) instead of the hard `1e-4` floor.

---

## Refuted suspects (proven NOT the cause)

| # | Suspect (from the plan) | How refuted |
|---|---|---|
| **S2** | IBL prefilter clamp (16384) ⇒ sun-disk specular leaks as bright spots | **SkyTest** (procedural sky + IBL bake, no point lights) and **BistroExterior** (sky-lit, few/no point lights) are **sparkle-free**. An IBL-specular defect would show on those; it doesn't. The dim-area spots track **point lights** (D3), not IBL. The asymmetric clamp may exist in code but does **not** produce the visible symptom. |
| **S4** | DxrReflections.hlsl:175 unsanitized DDGI divide ⇒ reflection sparkles | The default reflection path in these scenes is **SSR, not RT**: `exp_1e-5` (default) == `refl_ssr` (forced SSR) **byte-identically** (meanError 0.00000, 0% diff). `DxrReflections.hlsl` **does not execute** in the default render. Forcing RT changes only ~10% of pixels (floor reflection) and adds **no** sparkles. |
| **S3** | SSGI temporal final-Sanitize missing ⇒ crawling sparkles | Sparkles **persist with `BALLISTIC_DX12_SSGI=0`** (GI off). The GI-isolate view is smooth (no speckle). The sparkle source is the **direct punctual pass** (D3), not the SSGI temporal resolve. (A final `Sanitize` remains cheap insurance for V4 but is not the cause.) |
| **S6** | OIDN fail-fallback feeds undenoised history (readback path) | Log line confirms **`[OIDN] denoise avg 9.04ms/frame over 30 (ZERO-COPY)`** on the RX 9070 XT — the zero-copy GPU path is active, not the readback fallback. No noise *or* perf cliff from OIDN. |
| — | Motion vectors / TAA / jitter / NaN-ternary | Not implicated by any capture; left untouched per the plan. |

---

## What this means for V1→V4 (priority order)

1. **V1 (exposure) — do first, biggest win.** Fix the auto-exposure meter↔radiance calibration so
   Automatic lands near the manual `~1e-5` that's empirically correct (add the eye-adaptation EMA the
   LumAverage header defers; reconcile the `1e-5` prescale or the `LuminanceToEV`/limit anchoring).
   This *also* shrinks D3's visibility dramatically. Manual≈Auto is the gate.
2. **V2 (lighting) — D3.** Replace the hard `1e-4` inverse-square floor with physical near-field
   softening (source-radius / windowed falloff) so close-range punctuals can't spike. Keep LightTest
   byte-identical (its lights are far — unaffected).
3. **V3 (IBL/sky/AP) — D2.** Gate aerial perspective off for interiors / short view distances (or
   drop the default `Strength`); wire-or-delete the dead `Exposure` constant. **S2's clamp is not a
   visible defect** — spot-check only, don't churn.
4. **V4 (temporal/denoise) — defensive only.** S3/S4/S6 are **not** active causes; the cheap final
   `Sanitize` guards remain reasonable insurance but are not the fix. OIDN is healthy.

## Scene-matrix summary (all captured, deterministic, paused)

| Scene | meanLuma | State observed |
|---|---|---|
| BistroInterior_Wine | 201 | D1 white-out + D2 veil + D3 fireflies (all three) |
| BistroExterior | 193 | D1 + D2 (no D3 — no close point lights) |
| SkyTest | — | D1 + D2 (pale ground/sky, no D3) |
| LightTest | dark | **clean** — controlled punctuals, no fireflies (D3 needs near-field) |
| CornellBox | 73 | mild D1 (walls blown), no D2/D3 |
| TransparentTest | 126 | **healthiest** — forward/transparent path correct, no symptoms |

**Reproduce any row:**
```
bal render <scene> --out e:/tmp/v0                       # default (shows the regression)
BALLISTIC_DX12_AP=0  bal render <scene>                  # D2 off → veil gone
BALLISTIC_DX12_EXPOSURE=0.00001 BALLISTIC_DX12_AP=0  bal render <scene>   # D1+D2 off → correct, D3 remains
```
(PNG conversions for eyeballing: `System.Drawing` BMP→PNG; histograms: `python e:/tmp/imgstat.py hist <bmp>`.)

---

## Addendum — the "white fog filter" on BistroExterior is D2+D1 stacked (no 3rd source)

User screenshot (close-up exterior) showed a flat white/blue **fog filter** over the whole frame.
Bisected on BistroExterior:

| Capture | meanLuma | veil state |
|---|---|---|
| default | 192.9 | heavy blue/pink fog filter (the screenshot) |
| `AP=0` | 174.3 | **colour veil gone** — real brick/shutters/signs return, but still pale/low-contrast |
| `AP=0` + `EXPOSURE=4e-6` | 75.9 | **fully clean** — dark awning shadows, saturated colour, proper daytime depth |

- The **coloured** part of the filter = **D2 (aerial perspective)** — `AP=0` removes it cleanly.
- The remaining **milky low-contrast wash** = **D1 (auto-exposure overshoot)** — correct exposure
  removes it; the scene gains real shadows and contrast.
- **No third veil source.** BistroExterior has **no volumetric fog** configured, and the clean frame
  is **byte-identical** with `BALLISTIC_FX_VOLUMETRIC=0` (meanError 0.00000). So the "fog filter" is
  fully explained by D2 + D1 — the same two defects, confirmed on the exterior as well as the interior.

Net: the user's "white fog filter" complaint is fixed by **V1 (exposure) + V3 (gate AP for these
scenes)** — already the top two V-phase priorities. No re-ranking needed.

---

## Resolution — all three defects fixed (V1 / V2 / V3 committed)

| Defect | Phase | Commit | Fix |
|---|---|---|---|
| **D1** exposure blow-out | V1a | `9f7f4741` | Re-anchor the auto-exposure meter to the **lux-scaled** DX12 radiance (`LumAverage` EV +8, measured) + auto EV limits `8..17→13..19`. BistroInterior auto `75` == manual-`1e-5` reference (Manual≈Auto). Fixed/manual path byte-identical. |
| **D2** aerial-perspective veil | V3 | `b597451d` | **Near-field fade**: fade haze in over `[NearFade, 2·NearFade]` m (default 25 m). Interior byte-equals AP-off (veil gone); exterior keeps its vista haze. Wires the dead `Exposure` AP constant as `NearFade`. |
| **D3** normal-map sparkle | V2 | `055c08e9` | **Root cause: RGBA8 textures uploaded with a single mip** (322/400 Bistro textures) — the DX12 port never did GL's GenerateMipmap, so no LOD filtering → normal aliasing → diffuse-NdotL sparkle. **Fix: `Dx12Texture2D.UploadCore` box-filters a CPU mip chain for single-level RGBA8 at upload** (fixes already-imported content, no re-import). Sparkle gone. + defense-in-depth (NormalLodBias, spec-AA, spec clamp, spherical-source attenuation). |

**Two refuted D3 hypotheses (recorded so they're not re-tried):** the near-field distance-attenuation
window and the specular firefly clamp each changed `<0.5%` of pixels — the sparkle is **normal-map
aliasing from missing mips**, not a lighting spike. The decisive diagnosis was a `bal gbuffer` dump
(noisy normals, std 0.14) followed by a hardcoded `SampleLevel(uv, 5)` showing **zero change** =
the textures have no mip chain. Dump the G-buffer + test mip existence before blaming lighting.

**Final state (deterministic paused, default settings):** BistroInterior and BistroExterior both render
clean — correctly exposed, no colour veil, no sparkle; the exterior keeps tasteful distance haze.
LightTest byte-identical through all three phases; CornellBox / TransparentTest / SkyTest unbroken;
Lumen GI-on stable (it reads the pre-exposure HDR, untouched). The merge regression is resolved.
