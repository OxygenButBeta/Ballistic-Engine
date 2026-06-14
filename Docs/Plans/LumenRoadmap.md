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
- GDF WARM-UP is SLOW on large scenes: cascade-0 background bake takes SECONDS (BistroExterior gdfActive
  false ~frame400 -> true ~2500). During warm-up Lumen silently contributes nothing. Per-cascade full-res
  BVH bake over the cascade triangles is the cost. Diagnostics: GLSdfGiPass.GdfActive + GLGlobalSdf
  FAULTED log under BALLISTIC_LUMEN_DIAG. (Note: a Task that FAULTS would retry forever, Available stuck
  false — watch for that.)
- Energy-bounded multi-bounce is mandatory (clamp bounce albedo 0.9 + hard cap) or white walls explode.
- NaN scrub must be a component SELECT, never mix(v,0,flag) (Inf/NaN*0 = NaN; AMD-proven).
- Route compute compiles through GLSLShaderUtilities.ToAscii (em-dash truncates the source).
- REBUILD THE EXE PROJECT (.Runtime/.Editor) after an engine change — shaders are embedded in the dll;
  building only BallisticEngine.csproj leaves the exe with a stale dll (wasted hours twice).
- Interior scenes (BistroInterior) pin Fixed EV15.5 (daylight) -> render BLACK. Exposure, not GI.

## WHAT WE WANT — the gap to real Lumen, as phases
Each phase: implement -> verify on the ISOLATED bounce on SunTemple+BistroInterior+BistroExterior+
CornellBox -> commit. Keep a working fallback until each is proven.

### NOW (user's explicit immediate ask)
- **DISABLE / REPLACE the legacy GI volumes that don't fit the Lumen goal:**
  - IrradianceVolume (diffuse light probes) and ReflectionVolume (specular cubemaps) are a SEPARATE GI
    model that conflicts/double-counts with Lumen and adds the slow probe bakes + the auto-fit volume
    machinery the user dislikes. For now: make them INERT when Lumen is active (don't bake, don't
    contribute, don't drive ambient), behind the Lumen-on gate. Keep the classes (back-compat) but stop
    the renderer from running their bakes / sampling them when Lumen owns the GI. Later: delete or
    fully fold their role into Lumen (diffuse = Lumen GI; specular = Lumen reflections).

### Phase A — GDF warm-up + always-on (make Lumen actually run, fast)
- Coarser-first cascade bake (bake 32^3 instantly, refine to 96^3 over frames) OR cap triangles per
  cascade + a faster BVH, so cascade 0 is up in 1-2 frames on ANY scene.
- Bake more cascades during the first warm-up frames; amortize after.
- Verify gdfActive=True within ~2 frames on BistroExterior; GI contributes immediately.

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
