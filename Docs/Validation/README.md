# DX12 validation baseline (W2 of the pass-graph migration)

`dx12-gbv-baseline.json` is the **GPU-Based-Validation / debug-layer message allowlist** the DX12
pass-graph migration gates against. The migration drops the byte-identical oracle; the D3D12 debug
layer + GBV replaces it for the **barrier/state** bug class. The gate is **"zero NEW validation errors
vs this baseline,"** not "zero errors" — the un-refactored renderer already emits known messages, so
those are captured here and the gate fails only on signatures NOT in this set.

See `C:\Users\suley\.claude\plans\silly-leaping-fog.md` § "Validation oracle" (W1–W4) and the memory
`dx12-passgraph-plan-2026-06-17`.

## How it works

- Each D3D12 message is normalized to a **signature** = `Category|Id|normalized-text`, with addresses,
  handles, VAs (`0x…`), and long bare numbers (alloc-order/sizes) stripped — so the same logical
  message matches run-to-run despite embedded pointers. The resource ROLE is kept (e.g.
  `SunShadowCascades`), so a different resource tripping the same id stays a distinct signature.
  (`BallisticEngine.DX12/Dx12ValidationBaseline.cs`.)
- At end-of-headless-render, `Dx12ValidationBaseline.DrainReportAndGate(device)` drains the info queue,
  partitions messages into known/NEW against this file, prints a report to **stderr** (`bal render`
  forwards the player's stderr), and — when `BALLISTIC_DX12_BREAK_ON_ERROR=1` — exits **2** on any NEW
  error-class (Corruption/Error) message. With no debug layer / no GBV the drain is a silent no-op, so
  a normal `bal render` is byte-identical and unchanged.

## Env doors

| var | effect |
|---|---|
| `BALLISTIC_DX12_DEBUG=1` | enable the D3D12 debug layer (info queue) |
| `BALLISTIC_DX12_GBV=1` | enable GPU-Based Validation (forces the debug layer on); slow, opt-in |
| `BALLISTIC_DX12_BREAK_ON_ERROR=1` | fail the run (exit 2) on NEW error-class messages (baseline-aware) |
| `BALLISTIC_DX12_GBV_BASELINE=<path>` | override the baseline path (default: this file, resolved via `BALLISTIC_ENGINE_ROOT` or the `BallisticEngine.slnx` walk-up) |
| `BALLISTIC_DX12_GBV_CAPTURE_BASELINE=<path>` | capture mode: **merge** the run's messages into the baseline at `<path>` (re-run across scenes to accumulate) |
| `BALLISTIC_DX12_GBV_BASELINE_COMMIT=<hash>` | stamp the `Substrate.Commit` field when capturing (the running build can't read its own git hash) |
| `BALLISTIC_DX12_HDR_DUMP=<file>` | (W3 noise-floor) write the HDR scene-color target back as raw R32F-triple `.bin` (+ `.manifest.json`) so the determinism floor can be measured in LINEAR/HDR space, not just the tonemapped LDR PNG. Measurement-only; render path unchanged. |

## Noise floor (W3) — `dx12-noise-floor.json`

`dx12-noise-floor.json` records the phase-2 `imgdiff` tolerances measured in chunk 2. **Headline result:
the deterministic floor is EXACTLY ZERO** — all 4 coverage scenes (SkyTest, CornellBox,
BistroInterior_Wine, BistroExterior) render byte-identical (SHA-256) across repeated deterministic
renders in BOTH LDR (the composite BMP) and HDR/linear (the `BALLISTIC_DX12_HDR_DUMP` R32F readback). So
the phase-2 deterministic gate is literal byte-identity vs the frozen golden set — no tolerance epsilon
needed on this substrate.

The **regime-(b) boiling band** (temporal-active motion-dump, the second oracle the deterministic gate
can't exercise) is near-deterministic too: BistroInterior is bit-exact run-to-run (σ=0), BistroExterior
has σ≈3e-6. Recommended operational regime-(b) gate = boiling within 0.5% (or mean+3σ, whichever looser)
of the frozen phase-1 value. Pinned to the same substrate as the GBV baseline; regenerate on a driver/GPU
bump. Boiling helper: `e:/tmp/chunk2/boil.py <runDir>...`.

## Substrate pin (R-NEW-6)

The baseline is valid **only** against the GPU + driver that produced it. The `Substrate` block records
the GPU, driver version, D3D12SDKLayers (Graphics Tools) version, OS, and commit. **A driver bump / GPU
swap invalidates the allowlist** — HDR float output and the GBV message set both shift. Regenerate then:

```bash
# from BallisticEngine.Runtime/bin/.../net9.0, with the Runtime exe fresh:
rm -f <baseline>.json
for scene in SkyTest/SkyTest CornellBox/CornellBox Bistro_v5_2/BistroInterior_Wine Bistro_v5_2/BistroExterior; do
  BALLISTIC_DX12_DEBUG=1 BALLISTIC_DX12_GBV=1 \
  BALLISTIC_SCENE="Assets/$scene.scene" BALLISTIC_SCREENSHOT=out.bmp \
  BALLISTIC_SCREENSHOT_PAUSED=1 BALLISTIC_DETERMINISTIC=1 \
  BALLISTIC_DX12_GBV_CAPTURE_BASELINE=<baseline>.json \
  BALLISTIC_DX12_GBV_BASELINE_COMMIT=<hash> \
  ./BallisticEngine.Runtime.exe <project>
done
```

## What's in the current baseline

Captured on **AMD Radeon RX 9070 XT / driver 32.0.31019.2002 / Graphics Tools present
(D3D12SDKLayers 10.0.26100.8521) / Win 10.0.26200.0**, from the un-refactored renderer (commit
`9912b749`, the chunk-0 tree — chunk 1 adds only env-gated tooling, the renderer is unchanged).

11 unique signatures, the union across SkyTest + CornellBox + BistroInterior_Wine + BistroExterior
(SunTemple's set is a subset; it renders black headless — pre-existing). The notable pre-existing
**state defects** the baseline records (these are real, to be fixed during the migration, not now):

- `InvalidSubresourceState` on `SetGraphicsRootDescriptorTable` / `DrawInstanced` / `ExecuteCommandLists`:
  resources bound as `PIXEL_SHADER_RESOURCE` while still in `RENDER_TARGET` or `DEPTH_WRITE`
  (incl. `SunShadowCascades` subresources 0–3) — the descriptor is `DATA_STATIC_WHILE_SET_AT_EXECUTE`.
- `ResourceBarrierBeforeAfterMismatch`: a transition's before-state doesn't match the assumed-current state.
- `CreateResourceStateIgnored` (benign): buffers created with a non-COMMON InitialState.
- `CreateDeviceDebugLayerStartupOptions` (info): the GBV-enabled startup banner (only present under GBV).

These are exactly what W2 exists to record: the migration gate flags only messages BEYOND this set.
