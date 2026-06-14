# Lumen — Master Roadmap (the full RT GI goal)

USER MANDATE (verbatim intent): **full real-time Lumen, no matter what.** Same quality + behaviour as
Unreal Lumen. Implemented from PUBLISHED techniques (SIGGRAPH 2021/2022 radiance-caching talks), NOT
UE source (EULA). Lumen is THE GI solution — it REPLACES the old GI systems, doesn't coexist with them.

## WHAT WE HAVE (committed, working)
The **per-pixel SDF gather** path — the current DEFAULT, genuinely good (verified on the ISOLATED
bounce, not composite mean — that's the rule now):
- **Global Distance Field (GDF)** clipmap: `GLGlobalSdf` (OpenGL/Rendering/GI/), 4 camera-centered
  cascades 96^3, cascade N = 2^N x 12m, background-baked (BVH closest-tri + 6-ray parity sign via
  `MeshSdfBaker.BakeWorldTriangles`), clipmap scroll + geometry-stamp rebake, cutout excluded.
- **Voxel lighting** (the GDF surface cache): parallel RGBA16F radiance clipmap, per-cascade ping-pong,
  `GlobalRadianceInject_Comp` lights each near-surface voxel (sun+shadow+sky+energy-bounded one-bounce),
  PER-VOXEL ALBEDO (RGBA8 clipmap, nearest-tri BaseColorFactor — red wall bounces red).
- **Screen tracing**: `SdfTrace_Comp.ScreenTrace` — depth-buffer march first (sharp near field), then
  the GDF march. Shared `TraceRay` (per-pixel + probe share it).
- **Per-pixel gather**: `SdfTrace_Comp.main` traces RAY_COUNT cosine rays/half-res-pixel -> temporal
  (SSGI_Temporal) -> a-trous denoise -> additive composite. Looks smooth/clean.
- Gated: BALLISTIC_SDFGI=1 + BALLISTIC_LUMEN_GDF=1 + a GI/Lumen volume override (or BALLISTIC_GI_LUMEN=1).
- **Octahedral screen probes** (`ProbeOctMode`, LumenProbeIntegrate/Temporal): BUILT but DEFAULT OFF —
  produces a blocky LATTICE (too coarse: 8px blocks, 2x2 bilinear). The right architecture eventually
  but a quality regression now. BALLISTIC_LUMEN_PROBES=1 to opt in.

Also present (pre-Lumen, being superseded): per-mesh SDF bricks (`GLSdfAtlas`/`GLSdfScene` + per-mesh
RadianceInject) — the path used when BALLISTIC_LUMEN_GDF is off. Plus the legacy IrradianceVolume
(diffuse light probes) + ReflectionVolume (specular cubemaps) — see "REPLACE" below.

## WHAT WE KNOW (hard-won, do not relearn)
- **Judge GI by the ISOLATED bounce image (BALLISTIC_SDFGI_DEBUG), NEVER the composite mean.** A bright
  frame hid a fully-broken (lattice) GI for many commits.
- The octahedral SCREEN-PROBE path's lattice is in the TRACE/coarseness, not temporal/integrate/scene
  (bisected by A/B: scene clean with GI off; grid identical with temporal off; 2 integrate rewrites = 0).
- GDF WARM-UP — FIXED (Phase A, commit 35e6e02c). Was SECONDS (cascade-0 full 96^3 BVH bake). Now
  COARSE-FIRST: bake every cascade at 32^3 first (CPU-upsample into the full-res texture, march/placement
  unchanged), mark available, then refine to 96^3 in the background. Cascade-0 coarse up ~1-3 frames after
  geometry loads (BistroExterior ~frame101). Plus: narrow-band sign (only ray-cast the inside-test near
  surfaces), sub-cell triangle cull (drop tris < half a voxel — shrinks the BVH build, the dominant cost;
  SunTemple cascade3 342K->94K tris), row-parallel grid query, 3-ray coarse sign. Diagnostics:
  GLSdfGiPass.GdfActive / GdfWarmupState + per-bake BVH/grid timing under BALLISTIC_LUMEN_DIAG. The full
  96^3 REFINE is still ~1-6s/cascade (dense world-triangle bake is the architectural ceiling — the real
  Lumen answer is composing per-mesh MDFs, Phase B+); but it's background and the coarse field renders
  meanwhile. (Note: a Task that FAULTS would retry forever, Available stuck false — watch for that.)
- GI ENERGY — the additive SDF-GI is irradiance and MUST be x receiver albedo (rho). No albedo G-buffer
  (forward renderer), so rho was implicitly 1 -> ~3x too bright; tolerable on a DIM interior, SATURATED a
  bright exterior red. FIXED (df8ac32e) with rho=0.3 (radiosity avg-albedo convention) in SdfGi_Combine.
  BistroExterior fixed, SunTemple preserved. Proper per-pixel albedo = a deferred-G-buffer change (later).
- Energy-bounded multi-bounce is mandatory or white/coloured walls explode: the voxel cache is a geometric
  series in bounce albedo a; enclosed (no sky escape) it sums to direct*a/(1-a). CLAMP NOW 0.55 (was 0.9 =
  10x runaway), commit a8b25524. Hard cap 32 as final safety.
- PHANTOM SUN — FIXED (48400850): SampleSunVisibility returned 1.0 outside the shadow cascades (phantom
  sun on interior surfaces beyond cascade range). Now the outside-cascades fallback is a GDF SUN TRACE
  (march the global field toward the sun: hit -> shadowed, escape -> lit) in BOTH SdfTrace_Comp and
  GlobalRadianceInject_Comp. Real Lumen far-shadow technique; verified regression-free on SunTemple +
  BistroExterior. (Was NOT the cause of BistroInterior's red — see below.)
- BISTROINTERIOR railed-red GI — FIXED (commit 1373e9d0) via the DEFERRED-ALBEDO G-BUFFER. Root cause: the
  GI composited irradiance with NO per-pixel receiver albedo (flat rho=0.3 stand-in). In this dim, high-EV
  interior (preExposure ~0.0023 -> raw HDR ~435x display) every gather ray hit a red wall and returned its
  huge red lit radiance, and the additive GI dwarfed the dim receiver -> pure-red rail (the WALLS dominate,
  not the sky; it has NO directional light so it was never phantom sun). FIX: 3rd opaque MRT (ColorAttachment2
  = linear diffuse albedo = albedo*(1-metallic)) in GLFrameBuffer + PBR Frag.glsl; SdfGi_Combine multiplies
  the added GI by that per-pixel albedo (HasAlbedo; falls back to 0.3 when absent). Default intensity
  RECALIBRATED 0.4 -> 0.18 (the old 0.4 baked in the flat 0.3; real albedos average higher — SunTemple's red
  sandstone ~0.7 over-bounced once true albedo applied). GPU-driven path transparent to fragment outputs
  (injects only vertex+material), verified byte-clean; sky pass masks attach 2; prepass depth-only (ColorMask).
  Result: BistroInterior (151,9,7) railed -> (30,9,4) clean moody bar; SunTemple cream columns un-washed +
  natural apse bounce (best yet); BistroExterior natural terracotta. No-regression with Lumen OFF confirmed.
  (Dead ends, don't repeat: sky-removal from HitDirect/GatherBounce hurt BistroExterior; relative clamp scene*2
  too loose.) This scene is ALSO a known exposure case (Fixed EV15.5 -> black; tested under auto-exposure).
- NaN scrub must be a component SELECT, never mix(v,0,flag) (Inf/NaN*0 = NaN; AMD-proven).
- Route compute compiles through GLSLShaderUtilities.ToAscii (em-dash truncates the source).
- REBUILD THE EXE PROJECT (.Runtime/.Editor) after an engine change — shaders are embedded in the dll;
  building only BallisticEngine.csproj leaves the exe with a stale dll (wasted hours twice).
- Interior scenes (BistroInterior) pin Fixed EV15.5 (daylight) -> render BLACK. Exposure, not GI.

## WHAT WE WANT — the gap to real Lumen, as phases
Each phase: implement -> verify on the ISOLATED bounce on SunTemple+BistroInterior+BistroExterior+
CornellBox -> commit. Keep a working fallback until each is proven.

### DONE so far (committed)
- Legacy IrradianceVolume + ReflectionVolume INERT when Lumen active (64027bda); separate SSGI
  disabled when Lumen active (b672c976) — pure-Lumen isolation for testing.
- Phase A GDF warm-up — DONE (35e6e02c): coarse-first clipmap, see WHAT WE KNOW. Cascade-0 coarse up
  ~1-3 frames after geometry loads.
- GI energy: receiver-reflectance rho=0.3 (df8ac32e) — exterior fixed, interior preserved.
- Multi-bounce clamp 0.55 + radiance-cache diagnostic gate (a8b25524).
- Phantom-sun GDF sun trace (48400850) — outside-cascades sun shadowing via the GDF.
- DEFERRED-ALBEDO G-BUFFER (1373e9d0) — per-pixel GI receiver reflectance (3rd opaque MRT). FIXED
  BistroInterior over-bright + improved all 3 scenes. Intensity recalibrated 0.4 -> 0.18. (SSGI_Combine
  still uses its own flat energy gate, not the albedo G-buffer — SSGI is off under Lumen; wire it later.)
- **SAMPLER-BINDING BUG FIX (6daf7c46)** — THE root cause of "GI is very red / thin / no real bounce".
  GLSdfGiPass.BindCombineSampler hardcoded combineShader.SetInt, but temporal/denoise/probe passes
  Activate their OWN shader -> their samplers stayed at default unit 0, so the DENOISE read GI-colour as
  normals/depths -> garbage edge-stops crushed the bounce to red sludge (green/blue~0). Fix: BindShaderSampler
  passes the ACTIVE shader. GI is now FULL-COLOUR + smooth. (SSGI was always correct — uses BindTex.)
- **SURFACE-CACHE READ FIX (3aec9a16)** — multi-bounce was absent because the gather sampled the voxel
  cache EXACTLY at the surface (d~0) where trilinear blended occupancy alpha to ~0, failing the gate ->
  fell back to flat HitDirect everywhere -> cache (and its multi-bounce) unused. Fix: sample 1.5 cells
  INTO the surface. Also reworked GatherBounce (was step-starved: 24 steps @ ~6cm died after 1-2m).
  Multi-bounce now scales correctly (gain 1->(9,6,3), 2->(12,7,4), 3->(48,..) runaway). Default gain 2 (d42fc365).
- **PUNCTUAL-LIGHT GI (c7415d89)** — the GDF voxel cache lit from sun+sky only, so point-lit interiors
  got ZERO Lumen bounce (BistroInterior iso GI was (0.2,0,0)). Now point lights inject into the cache
  (inv-sq + range cutout + NdotL, up to 8). BistroInterior iso GI -> (1.3,0.4,0.1), warm lamp bounce
  visible. Point-light SDF SHADOWING in the cache is a TODO (currently unshadowed). Spot lights TODO too.

### STATE: all 3 reference scenes have genuine colored multi-bounce GI (verified isolated bounce).
SunTemple + BistroExterior are Lumen-class; BistroInterior now has (subtle, physically-correct) lamp
bounce. An adversarial multi-lens review workflow (wf_fc2bace5-cec) is judging quality vs real Lumen.

### NEXT — pick up the phase ladder / review-driven fixes:
- Act on the review's confirmed defects first.
- Recheck intensity 0.18 (tuned vs the BROKEN red GI; the bounce is now genuine/richer -> may want higher).
- Point-light + spot-light SHADOWING in the GI cache (currently unshadowed punctual bounce).
- Phase B (make Lumen the no-flag default); then reflections, world-space radiance probes, denoise polish.

### Phase B — make GDF+per-pixel the DEFAULT GI (no flag)
- Once warm-up is fast + quality confirmed, drop BALLISTIC_SDFGI/LUMEN_GDF gating so Lumen is the
  engine's GI by default; remove/retire the per-mesh brick path. (User: "no flag.")

### Phase C — proper final gather (fix the probe lattice -> the real perf+quality structure)
- Rework screen probes: finer placement, a real spatial filter (Lumen's), correct octmap interpolation;
  prove it MATCHES then BEATS per-pixel on the isolated bounce. Until then per-pixel stays default.

### Phase D — world-space radiance probes (Lumen radiance cache)
- Sparse 3D probe grid storing world radiance (octahedral), fed by the traces, for distant/multi-bounce
  + ray endpoints beyond screen reach. Replaces the legacy IrradianceVolume's role entirely.

### Phase E — Lumen reflections (proper)
- Reuse the traces at the mirror lobe + the surface cache for glossy; denoise. Replaces ReflectionVolume.

### Phase F — final-gather denoise + integration polish
- Proper temporal + spatial denoise tuned to Lumen stability; integrate diffuse + reflections cleanly;
  handle thin geometry / leaks / disocclusion under motion.

### Phase G — exposure
- Interiors auto-expose (or sane EV) so the GI is actually visible. (Separate from GI; do alongside.)

## VERIFY HARNESS
Headless: BALLISTIC_SDFGI=1 BALLISTIC_LUMEN_GDF=1 BALLISTIC_GI_LUMEN=1 [BALLISTIC_SDFGI_DEBUG=1 for the
raw bounce] BALLISTIC_SCENE=... BALLISTIC_SCREENSHOT=... ; bmp2png + view. BALLISTIC_LUMEN_PROBES=1 for
the probe path, BALLISTIC_LUMEN_DIAG=1 for GDF state, BALLISTIC_EXPOSURE_MODE=auto for interiors.
Editor on monitor 2 (BALLISTIC_MONITOR=1). Commit each step file-based msg + explicit pathspecs.
