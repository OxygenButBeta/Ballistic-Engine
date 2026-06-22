# Plan — Realism Post-FX Suite (4 batches)

Status: **PLAN ONLY — do not implement until the user gives the command.**
Scope locked with the user: all 4 batches, plan-first, **camera-only** motion blur.

Goal: add the missing realism post effects (color grading suite incl. "mid highlights/gamma",
lens flare + dirt, AgX + LUT tonemapping/grading, camera motion blur) to the existing Unity-style
Volume framework. Every effect: OFF/neutral by default → the current image stays **byte-identical**.

---

## Architecture facts confirmed (file:line grounded)

- **Volume framework is fully reflection-driven.** A new `VolumeComponent` subclass with the
  existing parameter types needs ZERO extra wiring for serialization
  ([VolumeProfileLoader.cs](../../AssetPipeline/Loaders/VolumeProfileLoader.cs) iterates
  `component.Parameters`) and ZERO editor wiring
  ([VolumeProfileEditor.cs](../../BallisticEngine.Editor/Panels/VolumeProfileEditor.cs)
  `DrawParameter` switches on parameter type; `ColorParameter` already supported, incl. HDR).
  New component classes auto-appear in the "Add Override" menu via `ComponentRegistry.Build`.
- **The ONLY seam** between the blended stack and the GL pipeline is
  [VolumePostProcessing.Apply](../../Engine/Rendering/Volumes/VolumePostProcessing.cs) → fills the
  flat [PostProcessSettings](../../Abstraction/Rendering/PostProcessSettings.cs) (`fx`) object.
  Add fields to `PostProcessSettings`, map them in `Apply`.
- **Composite (final HDR→display) shader** =
  [FSQ_Frag.glsl](../../OpenGL/Shader/Embedded/FSQ_Frag.glsl), driven by
  [GLCompositePass.cs](../../OpenGL/Rendering/PostFX/GLCompositePass.cs). Current chain in
  `GradeAt()`: ACES tonemap → unsharp → contrast → saturation → (vignette, CA, distortion in
  `main`) → `LinearToSrgb` → film grain. **All color-grading math (Batch 1) and tonemapper/LUT
  selection (Batch 2) slot into this shader.**
- **Post-FX chain orchestration** = `GLHDRenderer.BeginRender`
  ([GLHDRenderer.cs:798-950](../../OpenGL/GLHDRenderer.cs#L798)):
  `litColor = target.colorBuffer` → SDF-GI → SSGI → SSR → volumetric → **TAA** (L880) →
  (meterSource captured L890) → **DoF** (L898) → auto-exposure measure (L907) →
  **bloom** (L912) → **composite** (L946/L948).
  New passes (lens flare, motion blur) insert here as `litColor = pass.Render(...)`.
- **Pass field/ctor pattern**: fields declared ~`GLHDRenderer.cs:35-61`, constructed in
  `Initialize()` ~L204-220. Canonical simple pass to copy =
  [GLSSRPass.cs](../../OpenGL/Rendering/PostFX/GLSSRPass.cs) (ctor reads embedded GLSL via
  `EmbeddedShaderSource.Read`, `Render` acquires pool RTs, binds units, draws FSQ, returns texture).
- **Depth/normal available to post passes**: `target.DepthTextureId`, `target.NormalTextureId`.
- **Transient scratch RTs**: `GLRenderTexturePool.Shared.Acquire(w,h)` → `GLRenderTexture`
  (`.Texture`, `.BindAsTarget()`); released wholesale in `EndFrame`. NEVER pool cross-frame history.
- **Camera reprojection data for motion blur ALREADY EXISTS** (no new geometry pass needed):
  - `prevViewProjection[targetIndex]` (unjittered prev VP), `prevViewProjectionValid[targetIndex]`
    ([GLHDRenderer.cs:884-885](../../OpenGL/GLHDRenderer.cs#L884)).
  - Current unjittered `viewProjection` (L797) and `Matrix4.Invert(viewProjection)` (L876).
  - TAA already reconstructs world-pos from depth + inv-VP and reprojects with prev-VP
    ([TAA_Frag.glsl](../../OpenGL/Shader/Embedded/TAA_Frag.glsl)) — motion blur reuses this math.
- **LUT caveat**: the engine's `Texture3D`/`GLTexture3D` is actually a **cubemap**, NOT a GL 3D
  texture. The LUT needs a real `GL_TEXTURE_3D` (via `GL.TexImage3D`, as used in the SDF/GI code).
  The LUT pass owns its own small `GL_TEXTURE_3D`; no reuse of `GLTexture3D`.

### Gotchas to honor (from CLAUDE.md / memory)
- **NaN scrubs = ternary SELECT, never `mix(v,0,flag)`** (proven leak on AMD RX 9070 XT).
- **Never mix raw-HDR with tonemapped samples** in any blur (tonemap first — `GradeAt` already does).
- **Run new GLSL through `GLSLShaderUtilities.ToAscii`** (em-dash truncation) — actually
  `EmbeddedShaderSource.Read` + the compile path already sanitize; keep comments ASCII to be safe.
- **`GL.ActiveTexture(Texture0)`** discipline after binds; restore at pass end.
- **Rebuild the Editor EXE** after engine changes (stale dll lesson).
- Byte-identical-neutral is the gate before each commit (`BALLISTIC_SCREENSHOT_PAUSED=1`
  deterministic captures + `bal imgdiff` / `e:/tmp/imgstat.py`).

---

## BATCH 1 — Color Grading Suite  (shader-only; no new pass)  ← do first

The biggest realism/effort win. All math in `GradeAt()` in `FSQ_Frag.glsl`, after `Tonemap()`,
operating in display-linear (pre-sRGB), before the existing contrast/saturation.

### New/extended VolumeComponents (`Engine/Rendering/Volumes/Components/ColorGrading.cs`)
Extend the existing `ColorAdjustments` and add two siblings (Unity HDRP split):

1. **`WhiteBalance`** (NEW component)
   - `temperature` ClampedFloat (−100..100, 0=neutral) — warm/cool. Maps to a CCT shift.
   - `tint` ClampedFloat (−100..100, 0=neutral) — green/magenta.
   - *Math*: convert temperature/tint → an RGB white-point scale via the standard
     Unity `ColorBalanceToLMSCoeffs` (CIExyY of target CCT → LMS von Kries → RGB ratio).
     Bake the 3 coefficients CPU-side (cheap, no per-pixel cost), pass as `vec3 WhiteBalance`.
2. **`ColorAdjustments`** (EXTEND existing — currently only contrast+saturation)
   - keep `contrast`, `saturation`
   - add `postExposure` Float (stops, applied pre-tonemap actually — see note) — optional, low risk
   - add `hueShift` ClampedFloat (−180..180)
   - add `colorFilter` ColorParameter(white, hdr) — multiply tint
3. **`ColorGradingWheels`** (NEW component) — the "mid highlights" request, two forms:
   - **Lift / Gamma / Gain** (ASC-CDL-ish): three ColorParameters (default neutral) +
     three master Floats. Lift = shadows offset, Gamma = **midtones** power, Gain = highlights mult.
     This is literally the "mid highlights / gamma" the user named.
   - **Shadows / Midtones / Highlights** color wheels: three ColorParameters + luminance-windowed
     weights (smoothstep split at ~0.33 / 0.66). (Could ship LGG first, SMH as a fast follow.)

### PostProcessSettings fields
`WhiteBalanceTemp`, `WhiteBalanceTint` (or precomputed `Vector3 WhiteBalanceCoeffs`),
`HueShift`, `Vector3 ColorFilter`,
`Vector3 Lift`, `Vector3 Gamma`, `Vector3 Gain` (defaults: Lift=0, Gamma=1, Gain=1),
`Vector3 ShadowsColor`/`MidtonesColor`/`HighlightsColor` (defaults neutral).
All default to identity → neutral = byte-identical.

### VolumePostProcessing.Apply
One mapping block per new component (copy the `ColorAdjustments` block pattern).
White-balance temp/tint → compute coeffs here (CPU) or in the renderer; store in `fx`.

### Shader (`FSQ_Frag.glsl` `GradeAt`, after `Tonemap`)
Order (Unity-consistent): white balance → color filter (multiply) → hue shift →
lift/gamma/gain → shadows/mid/highlights → (existing) contrast → saturation.
- Each guarded by an `if (param != identity)` so a neutral grade is a no-op (byte-identical).
- Hue shift via YIQ rotation or RGB→HSV→shift→RGB (YIQ cheaper, no branch hazards).
- All NaN-safe (these are bounded display-linear values; no Inf — but keep `max(c,0)`).

### GLCompositePass.cs
Add `SetFloat3`/`SetFloat` uniform sets for the new params (copy the existing block L53-62).

### Verify
- Neutral profile → `imgdiff` vs pre-change baseline = **0 mean error** (the gate).
- `WhiteBalance temp=-40` visibly warms Bistro; LGG gamma>1 lifts midtones; hue shift rotates.
- Screenshot SunTemple + Bistro paused-deterministic.

### Commit 1: "PostFX: color grading suite (white balance, lift/gamma/gain, SMH wheels, hue/filter)"

---

## BATCH 2 — AgX tonemapper + LUT grading  (shader + .cube loader)

### Selectable tonemapper
- New `Tonemapping` VolumeComponent: `EnumParameter<TonemapMode> mode` =
  `{ ACES, AgX, KhronosPbrNeutral, Reinhard, None }`. Default **ACES** (byte-identical fallback).
- `PostProcessSettings.TonemapMode` (enum), default ACES.
- `FSQ_Frag.glsl`: replace the hardcoded `ACESFilm` call in `Tonemap()` with a `switch(TonemapMode)`.
  - Add **AgX** (Troy Sobotka / Filament minimal AgX fit — the realism win: bright saturated
    lights desaturate to white cleanly, no ACES hue skew). Use the well-known polynomial-approx
    AgX (input matrix → log2 encode → 6th-order poly → output matrix → optional "look").
  - Add Khronos PBR Neutral (official GLSL), Reinhard (extended), None (clamp).
- Pass `TonemapMode` as `int` uniform from `GLCompositePass`.

### LUT (`.cube`) grading
- **Loader**: `AssetPipeline/Loaders/CubeLutLoader.cs` — parse standard `.cube` ASCII
  (`LUT_3D_SIZE n`, `DOMAIN_MIN/MAX`, then n³ RGB rows). Produce a `float[]` + size.
  Register `.cube` as a native text asset (CLAUDE.md lists native text assets; add `.cube`).
- **GPU**: composite pass owns a `GL_TEXTURE_3D` (RGB16F, trilinear, clamp). Built from the
  loaded LUT when the `LutGrading` component references one; rebuilt on change (guid compare).
  *NOT* the cubemap `GLTexture3D`.
- **Component** `LutGrading`: `enabled` Bool, `lut` (AssetRef/Texture handle to a `.cube`),
  `contribution` ClampedFloat(0..1). Applied in `GradeAt` AFTER tonemap+grade, BEFORE sRGB
  (LUT operates on display-linear; or after sRGB if the LUT is log/display-encoded — expose a
  `mode` enum: Linear vs sRGB-domain). Identity/no-LUT/contribution 0 = byte-identical.
- `PostProcessSettings`: `LutEnabled`, `LutContribution`, plus a renderer-side handle for the
  3D texture id (the bridge passes the resolved asset, renderer uploads — mirror how DoF/SSR read
  `fx`; the texture id is renderer-owned, set from the component's asset ref each frame).

### Verify
- AgX vs ACES A/B on bright Bistro (AgX tames the clipped sun) + SunTemple.
- Identity LUT (generated) → byte-identical; a warm film LUT visibly grades.
- ACES mode + no LUT → byte-identical to current baseline (the gate).

### Commit 2: "PostFX: selectable tonemapper (AgX/Neutral/Reinhard) + .cube LUT grading"

---

## BATCH 3 — Lens Flare + Lens Dirt  (new GLLensFlarePass)

### New pass `GLLensFlarePass` (`OpenGL/Rendering/PostFX/`)
Copy the GLSSRPass/GLBloomPass structure. Shader `LensFlare_Frag.glsl` (+ reuse `FSQ_Vert.glsl`).
- **Input**: the HDR `litColor` (post-TAA) OR the bloom chain's bright/thresholded buffer
  (cheaper, already bright-isolated). Plan: threshold-downsample `litColor` to half/quarter res
  (own small chain, or reuse bloom level0 if exposed) → "features" buffer.
- **Ghosts**: sample the features buffer at UVs mirrored through screen center, scaled by
  `ghostSpacing` for N `ghostCount` ghosts, weighted by a radial falloff. Per-ghost chromatic
  offset (small RGB UV split) for the dispersion look.
- **Halo**: radial sample at fixed distance toward center, ring-weighted.
- **Anamorphic streak** (optional): separable horizontal blur of the features buffer → blue-tinted
  horizontal streak (the cinematic sun-streak tell).
- **Output**: additive flare buffer. Two integration options (pick the simpler that stays
  byte-identical when off):
  - (A) Add flare into the **bloom texture** before composite (composite already adds bloom) — no
    composite signature change. ← preferred.
  - (B) Separate composite uniform. Only if (A) muddies dirt-masking.

### Lens Dirt
- A dirt texture modulates the bloom+flare contribution. Ship a default procedural/asset dirt;
  user-overridable via the component (`Texture2D dirt`). New `FSQ_Frag.glsl` uniforms
  `dirtTexture`, `DirtIntensity`; multiply the `bloomTexture` term in `SceneHDR`/composite by
  `(1 + dirt*DirtIntensity)` so dirt only shows where bloom/flare is bright (real lens dirt is
  invisible until backlit). DirtIntensity 0 / no texture = byte-identical.

### Component `LensFlare` (`Engine/Rendering/Volumes/Components/LensFlare.cs`)
`enabled` (false), `intensity`, `ghostCount` ClampedInt(0..8), `ghostSpacing`, `haloWidth`,
`chromaticShift`, `streakIntensity`, `threshold` (bright cutoff), `dirtTexture` (ref),
`dirtIntensity`. All off/zero by default.

### PostProcessSettings + Apply
Fields mirroring the component; map in `Apply`. Renderer resolves the dirt/texture refs to GL ids.

### Insert in chain
After `bloom.Render` (L912), before `composite.Render` (L946): run `GLLensFlarePass`, add its
result into `bloomTexture` (option A). Skipped entirely when `!enabled` → byte-identical.

### Verify
- Sun in frame → ghosts march source→center, halo + streak appear; dirt glints where bloom is hot.
- `enabled=false` → byte-identical to Batch-2 baseline (the gate).

### Commit 3: "PostFX: screen-space lens flare (ghosts/halo/anamorphic) + lens dirt mask"

---

## BATCH 4 — Camera Motion Blur  (new GLMotionBlurPass; camera-only)

No velocity MRT, no per-material injection (per-object deferred per user). Reconstructs per-pixel
**camera** velocity from depth + prev/cur unjittered VP (TAA's exact reprojection math).

### New pass `GLMotionBlurPass` (`OpenGL/Rendering/PostFX/`)
Shader `MotionBlur_Frag.glsl` (+ `FSQ_Vert.glsl`).
- Inputs: `colorTexture` (post-TAA litColor), `depthTexture` (`target.DepthTextureId`),
  `InvViewProjCurrent` (unjittered), `PrevViewProj` (unjittered, from `prevViewProjection[idx]`).
- Per pixel: reconstruct world pos from depth + inv-VP → project with prev-VP → `prevUV` →
  `velocity = (uv - prevUV)`. Clamp to `MaxVelocity` (fraction of screen). Gather `SampleCount`
  taps along the velocity vector (jittered start to hide banding), average. Skip when
  `length(velocity) < epsilon` (static pixel = passthrough → static frame byte-identical).
- Allocate one scratch RT from the pool; return it.
- NaN-safe: guard the reconstruction; ternary selects only.

### Component `MotionBlur` (`Engine/Rendering/Volumes/Components/MotionBlur.cs`)
`enabled` (false), `intensity` ClampedFloat(0..1), `sampleCount` ClampedInt(4..32),
`maxVelocity` ClampedFloat (fraction of screen, e.g. 0.05..0.2).

### PostProcessSettings + Apply
`MotionBlurEnabled`, `MotionBlurIntensity`, `MotionBlurSamples`, `MotionBlurMaxVelocity`. Map in `Apply`.

### Insert in chain
After **TAA** (L882), BEFORE `meterSource` capture (L890) — actually AFTER meterSource so the
auto-exposure meters the sharp frame (consistent with DoF's reasoning). Place: right after
`meterSource = litColor;` and before DoF, OR after DoF. **Decision: after TAA, before DoF**, using
`prevViewProjection`/current VP already in scope. Guarded by `PostFX.MotionBlurEnabled` and
`prevViewProjectionValid[targetIndex]` (first frame has no history → passthrough).

### Verify
- Velocity reconstruction matches TAA (shared math) — sanity check a known camera pan.
- Static camera frame → zero velocity → **byte-identical** (the gate; paused screenshots are static
  so this is directly diffable).
- In-editor fly-cam: panning smears motion correctly, no edge artifacts, no NaN holes.

### Commit 4: "PostFX: camera motion blur (depth + reprojection, camera-only)"

---

## Cross-cutting

- **Build/verify loop per batch**: `dotnet build BallisticEngine.slnx`; rebuild Editor exe;
  deterministic paused screenshots before/after; `imgdiff` gate (neutral = 0); visual confirm.
- **Editor**: all new components auto-appear in Add Override + auto-render their widgets
  (reflection). LUT `.cube` and dirt-texture pickers may want a custom asset-ref widget — check
  whether `VolumeProfileEditor` already handles asset-ref params; if not, that's a small editor add
  (NOT blocking the render work; can ship the param as a path string first).
- **Memory**: after completion, add ONE memory file summarizing the suite + the byte-identical-gate
  workflow; one-line MEMORY.md pointer (index is already over budget — keep it short).
- **CLAUDE.md**: add `.cube` to the native-text-assets list; note the new post passes in the
  renderer-pipeline frame shape.

## Open decisions deferred to implementation (low-risk, will pick sane default)
- LUT domain (linear vs sRGB) — expose enum, default Linear.
- Lens-flare features source (own threshold chain vs reuse bloom level0) — start own chain for
  isolation, optimize later.
- LGG vs SMH — ship Lift/Gamma/Gain first (the named request), SMH wheels same component if cheap.
