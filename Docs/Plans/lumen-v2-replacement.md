# Lumen V2 Replacement

## Decision

The current DX12 GI stack is legacy. It remains compiled only for explicit A/B diagnostics and must not be a
shipping path. A proper Lumen-like replacement needs a new scene-lighting architecture instead of tuning the
existing SSGI/DDGI/OIDN chain.

## Non-goals

- Do not keep DDGI as the main diffuse GI answer.
- Do not run the baked-probe path through the SSGI temporal/OIDN resolve.
- Do not silently fall back from ray-traced GI to screen-space GI.
- Do not build a software-SDF path before the hardware-RT path works.
- Do not expose a wall of cvars as the product UI.

## Target Shape

Lumen V2 has one product-facing mode:

- `Off`: direct lighting, IBL, AO, shadows.
- `Lumen`: screen traces first, hardware RT for off-screen hits, surface/radiance cache for stable indirect.

Debug-only modes may exist for isolating a stage, but the product path is one path.

## Render Architecture

1. `LumenScene`

   Owns the scene representation used by GI and reflections:

   - existing BLAS/TLAS from `Dx12SceneAS`
   - existing material/instance buffers from `Dx12RtGeometry`
   - new surface-card allocation table
   - card atlases for albedo, normal, emissive, depth, and radiance
   - dirty flags from transforms, materials, lights, and camera coverage

2. `LumenCardCapturePass`

   Captures visible/static mesh surfaces into card atlases. This is the replacement for pretending probes are a
   surface cache. Cards are coarse, stable surface records; they are not camera pixels and not world probes.

3. `LumenDirectOnCardsPass`

   Lights card texels from sun, punctual lights, emissive, sky visibility, and shadows. Writes first-bounce
   radiance into the card radiance atlas.

4. `LumenScreenTracePass`

   Traces short rays in screen depth first. This catches near-field contact bounce and avoids expensive RT when the
   answer is already visible.

5. `LumenHwTracePass`

   Uses DXR for screen-trace misses and off-screen geometry. The hit shader returns material/card hit data, not a
   fully denoised final color. No silent SSGI fallback.

6. `LumenRadianceCachePass`

   Filters and temporally stabilizes radiance in cache space, not in final screen-space color. This is where
   low-frequency multi-bounce lives.

7. `LumenFinalGatherPass`

   Combines screen hits, RT hits, radiance cache, sky fallback, and albedo into diffuse GI. It writes one clean
   indirect buffer that the deferred/compose path can add without double-counting IBL.

8. Reflections

   Sharp reflections re-shade hardware RT hits. Rough reflections may sample the same radiance cache. IBL is only a
   miss/far fallback, not the near-field answer.

## Milestones

### P0 Legacy Quarantine

Done first: legacy GI is off unless explicitly armed by debug env doors. Volumes no longer resurrect old SSGI/DDGI
by accident.

### P1 Lumen Scene Substrate

Create `Dx12LumenScene` and wire it to existing TLAS/material infrastructure. No image change yet. The debug log must
report object count, card count, atlas size, and dirty updates.

### P2 Minimal Truthful GI

Implement screen trace plus hardware RT miss path into a raw indirect buffer. No surface cache yet, no temporal
history yet. It should be noisy but truthful. Verification: sealed black room stays black; color-bleed box bleeds;
thin wall does not leak.

### P3 Surface Cards

Add surface-card capture and sample cards on RT hits. This is the first Lumen-like quality jump: off-screen surfaces
can contribute real albedo/emissive radiance instead of IBL/probe mush.

### P4 Radiance Cache

Add filtered cache-space radiance and conservative temporal update. The cache stabilizes lighting; final screen
pixels do not carry the old OIDN/SSGI history burden.

### P5 Reflections

Feed rough reflections from the same radiance cache and keep sharp reflections on the hardware RT re-shade path.

### P6 Remove Legacy GI

Delete `Dx12GiPass`, `Dx12Ddgi`, `Dx12ScreenProbe`, legacy SSGI shaders, and obsolete baked-GI volume fields after
Lumen V2 owns their debug seams.

## Gates

- GI-isolate is the primary visual oracle.
- Thin-wall leak scene must remain dark on the far side.
- Sealed interior without light must not become sky-lit.
- Color-only/material-id content must contribute correctly.
- Moving light latency is measured separately for on-screen and off-screen paths.
- Hardware RT unavailable means `Lumen` is unavailable or reduced explicitly; no hidden fallback to SSGI.

## Performance Overhaul (P7 — Unreal-Lumen-aligned, no quality loss)

The P2–P6 stack is conceptually Lumen but the *naïve* variant: the cache scales with triangle count, every
triangle is re-lit every frame, and the final gather is a flat full-res per-pixel hemisphere trace + à-trous
blur. Unreal Lumen's real cost wins are amortized/budgeted updates, a bounded surface cache, and a
screen-probe final gather with importance/temporal reuse. P7 closes those gaps while keeping the two correctness
properties (no thin-wall leak, sealed interiors stay dark).

### Baseline (measured `bal perf`, 2026-06-19, RX 9070 XT, Lumen ON, default dials)

| Scene | Triangles | Lumen GI ms | Next-biggest pass |
|---|---|---|---|
| CornellBox | 86 | **1.24** | Deferred 0.008 |
| MultiLightInterior | 1,546 | **1.69** | Deferred 0.009 |
| BistroInterior_Wine | 797,113 | **2.00** | Reflections 0.021 |
| BistroExterior | millions | **2.07** | Transparents 0.036 |

**Key finding that reorders the plan:** Lumen is by far the most expensive render pass (10–100× the next
pass), AND ~1.2 ms of it is a geometry-INDEPENDENT floor (full-res per-pixel trace + denoise) — an 86-tri Cornell
box already costs 1.24 ms; 797 k tris adds only ~0.8 ms. So the constant floor (trace+denoise) is the bigger
prize than card lighting; card lighting is the geometry-scaling ~0.8 ms term.

### P7 milestones (order = risk-ascending, each measured before/after with `bal perf`)

- **#1 Cache update budget** (~2-3 d, low risk, fixes the geometry-scaling term). Persistent per-record
  `lastUpdatedFrame`; light a fixed budget of highest-priority records/frame (round-robin stride + priority);
  EMA absorbs the staleness. Light/sun/transform dirty → force a full relight that frame (latency guard).
  Stays unit-agnostic so #2A inherits it.
- **#1b Half-res trace + denoise** (cheap follow-on, fixes most of the constant floor). Diffuse indirect is
  low-frequency; trace/denoise at half-res, depth-aware upsample in combine (reuse the SSR upsample pattern).
- **#2A Cluster radiance cache** (~4-6 d). Per-triangle → per-meshlet (cluster by normal+material). FIRST DAY:
  put cache access behind a unit-agnostic `RadianceCache` interface (Sample/Write over a "surface record") so a
  later upgrade to real mesh cards (#2B) is ~2-2.5 weeks, not a rewrite. 30-50× smaller cache; scalable.
- **#3 Screen radiance probes** (~1-2 wk). Per-pixel flat gather → quarter/eighth-res probe trace + depth/
  normal-weighted interpolation; à-trous denoise largely retired. Lower variance AND lower cost.
- **#4 Importance + spatial/temporal reuse** (~3-5 d, on top of #3). Importance-sample toward carrying
  directions; reuse neighbour probes; disocclusion rejection (anti-ghosting).

### Deferred (consciously last)

- **#2B Real mesh-card + atlas** (~3-4 wk; ~2-2.5 wk if #2A used the interface). Only if `imgdiff` proves
  "patchy" indirect on large flat surfaces. Gives card-interior gradient + true surface-area scaling.
- **#5 SDF / software trace** — last. For HWRT-PC target this is portability, not quality (HWRT TLAS already
  covers far-field). Do distant-merged-cards before a full SDF stack.

### Expected cumulative result

~−65-70% Lumen GI cost with equal-or-better quality (#3/#4 reduce variance). Only #2A carries a small,
controllable quality trade (cluster-interior averaging — mitigated by normal+material-aware clustering).

### Restore point

`30fc5071` "[gi] Safe point before Lumen perf overhaul".
