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
