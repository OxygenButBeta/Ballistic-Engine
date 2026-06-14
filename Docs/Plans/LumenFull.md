# Full Lumen Implementation Plan — Ballistic Engine

Goal (user mandate): **replicate Lumen — same quality, same behaviour**, not an approximation.
Implemented from the PUBLISHED techniques (SIGGRAPH 2021 "Radiance Caching for Real-Time GI",
SIGGRAPH 2022 Lumen course, Epic's public docs/talks). We do NOT copy Unreal source (UE EULA forbids
it); we reimplement the algorithms. Built in verified phases, each committed, each screenshot-checked.

## What Lumen actually is (UE's pipeline, mapped to our engine)

Lumen is not one thing — it's a layered radiance-caching GI system. The layers, in trace order:

| # | UE Lumen subsystem | What it does | Our current state |
|---|---|---|---|
| A | **Mesh Distance Fields (MDF)** | per-mesh signed distance field, baked offline | HAVE: `MeshSdfBaker` + per-submesh bricks in `GLSdfAtlas` |
| B | **Global Distance Field (GDF)** | clipmap of merged scene SDF (4 cascades around camera), composited from MDFs | PARTIAL: started `BakeWorldTriangles`; no clipmap, no GPU compose |
| C | **Surface Cache** | per-mesh "cards" (orthographic captures of albedo/normal/depth from ~6 directions) packed in atlases; lit each frame → radiance atlas | HAVE (simplified): per-mesh radiance atlas filled by `RadianceInject_Comp`, NO cards (uses brick voxels + screen fallback) |
| D | **Voxel Lighting** | a coarse voxelization of the surface-cache radiance into a clipmap, for cone-traced far-field + multi-bounce | MISSING |
| E | **Screen Tracing** | per-probe: first trace the HZB depth (screen-space) for near-field detail before going to SDF | MISSING (our gather goes straight to SDF) |
| F | **Screen-space Radiance Probes** | sparse screen grid (~16×16 px) of probes; each traces N rays (screen → mesh-SDF → global-SDF), with importance sampling guided by prev-frame + BRDF | MISSING (we trace per-half-res-pixel, not probes) |
| G | **World-space Radiance Probes** | sparse 3D probe grid for distant/indirect radiance + multi-bounce feed | PARTIAL: our IrradianceVolume probes are diffuse-only, not Lumen radiance probes |
| H | **Final Gather + Integration** | interpolate screen probes onto full-res pixels (BRDF-weighted), then temporal + spatial denoise | PARTIAL: per-pixel gather + SSGI-style temporal + a-trous |
| I | **Reflections** | Lumen reflections reuse the same traces at mirror dirs + the surface cache | PARTIAL: glossy reflection ray in the march |

So we have rough A, C(lite), H(lite). The gap to "real Lumen" is **B (global SDF clipmap), D (voxel
lighting), E (screen traces), F (screen radiance probes w/ importance sampling), G (world radiance
probes), proper H (probe interpolation + denoise)**.

## Phased plan (each phase: implement → verify on BistroInterior+SunTemple+CornellBox → commit)

### Phase 1 — Global Distance Field (GDF) clipmap   [fixes per-object coverage NOW]
- `MeshSdfBaker.BakeWorldTriangles` (DONE) → bake one field over a world box.
- `GLGlobalSdf`: 4 clipmap cascades centered on the camera (e.g. 64³ each, doubling world extent:
  ~12m / 24m / 48m / 96m). Bake on a BACKGROUND thread; upload R16F 3D textures; re-bake a cascade
  only when it scrolls (camera moved > ½ cell) or geometry stamp changes. Amortize (one cascade/frame).
- `SdfTrace_Comp`: `SceneSdf` samples the GDF clipmap (pick finest cascade containing the point;
  hardware trilinear) instead of the instance grid. Gate `BALLISTIC_LUMEN_GDF=1` to A/B vs per-mesh.
- VERIFY: BistroInterior raw gather (`BALLISTIC_SDFGI_DEBUG=1`) goes from ~0 to a real value;
  SunTemple stays ≥ its current 101; no first-frame stall (background bake).
- DELIVERABLE: Lumen works on per-object scenes. This is the single biggest unblock.

### Phase 2 — Surface Cache via mesh cards
- `MeshCardBaker`: for each mesh, capture ~6 orthographic views (albedo, normal, depth) into card
  atlases (this is what makes Lumen's radiance stable + colored, off-screen). Pack into a card atlas.
- Light the cards each frame (direct sun + shadow + sky) → a radiance atlas, like our RadianceInject
  but card-based (correct per-texel material/albedo, not one-color-per-brick).
- VERIFY: colored bounce (red wall tints the room) on CornellBox; stable under camera motion.

### Phase 3 — Voxel Lighting clipmap (far-field + multi-bounce)
- Voxelize the surface-cache radiance into a camera-centered clipmap (coarse, ~64³×4). Cone-trace it
  for far-field radiance + as the multi-bounce feedback into the surface cache (bounce N from bounce N-1).
- VERIFY: multi-bounce visibly brightens enclosed interiors; far geometry contributes.

### Phase 4b DETAILED DESIGN — Octahedral screen-space radiance probes (the real Lumen final gather)

NOT irradiance probes (one value/probe) — DIRECTIONAL OCTAHEDRAL radiance probes, so the full-res
integration is BRDF-weighted (correct diffuse + the basis for Lumen reflections). Concretely:

- PROBE ATLAS: a 2D RGBA16F texture; each screen probe owns an OCT x OCT tile (OCT=8). Probe grid =
  ceil(halfW/STEP) x ceil(halfH/STEP), STEP=8 half-res px/probe. Atlas = (gridX*OCT) x (gridY*OCT).
- PROBE TRACE (extend SdfTrace_Comp with a ProbeOctMode): dispatch over the ATLAS texels. Each texel
  -> (probe = texel/OCT, octUV = fract within tile). Reconstruct the surface at the probe's
  representative pixel (probe*STEP + STEP/2, snapped to the nearest VALID G-buffer pixel — Lumen jitters
  probe placement per frame + reuses the trace functions). Decode octUV -> a world direction over the
  HEMISPHERE around the probe normal (oct-encode the hemisphere, not the full sphere — diffuse only
  needs the hemisphere). Trace THAT ONE direction (the existing ScreenTrace->GDF->voxel-light path),
  write incoming radiance to the atlas texel. Importance sampling: bias the oct directions toward
  last-frame's bright texels (a later refinement; uniform-hemisphere first).
- TEMPORAL on the probe atlas: reproject probes by their world pos, EMA-accumulate the octahedral
  radiance (disocclusion reject). This is where probe radiance converges (few rays/frame, stable octmap).
- INTEGRATE (new frag -> half-res output): per half-res pixel, find the 2x2 (bilinear) surrounding
  probes; for each, INTEGRATE its octahedral map against the surface's cosine lobe (sum oct texels *
  max(0,dot(N,dir)) * solidAngle), bilateral-weight across probes by depth+normal; that's the diffuse
  GI. (Reflections: sample the octmap in the mirror lobe — Phase: Lumen reflections.) Then the existing
  TAA + a-trous finish it.
- Octahedral encode/decode: the standard equal-area oct map (Cigolle et al.) restricted to the upper
  hemisphere in the probe's tangent frame.
- Gate BALLISTIC_LUMEN_PROBES (default on once proven) — it REPLACES the per-half-res-pixel gather.
- VERIFY: CornellBox noise drops dramatically + color bleed appears (the probe octmap + integrate is
  the noise fix); SunTemple/BistroInterior stay clean; trace count ~STEP^2 lower (perf).
- RISK: this is a large shared-shader restructure of SdfTrace_Comp.main + 1 new integrate shader + the
  probe atlas/temporal in GLSdfGiPass. Do it as ONE focused push; keep the per-pixel path as fallback
  until verified. The working GDF+voxel+albedo+screen-trace state (committed) must not regress.

### Phase 4 — Screen tracing + screen-space radiance probes
- HZB screen trace first (near-field detail, cheap) before falling to SDF traces.
- Replace per-pixel gather with a sparse **screen radiance probe** grid (~16×16px): each probe traces
  N rays (screen → mesh-SDF → GDF → voxel-lighting fallback), **importance-sampled** from prev-frame
  radiance + the BRDF. Octahedral radiance per probe.
- VERIFY: noise drops at equal cost vs per-pixel; detail at contacts via screen traces.

### Phase 5 — World-space radiance probes (Lumen "radiance cache")
- Sparse 3D probe grid storing world radiance (octahedral), filled by the traces above; used for
  ray endpoints beyond screen-probe reach + as a multi-bounce/distant cache. (Reuse/upgrade the
  IrradianceVolume infra into a radiance cache.)
- VERIFY: distant/large rooms get stable indirect; no light leaking (probe occlusion).

### Phase 6 — Final gather + denoise + integration
- Interpolate screen probes onto full-res pixels with BRDF weighting + depth/normal-aware bilateral.
- Temporal accumulation (reproject, disocclusion-reject) + spatial filter, tuned to match Lumen's
  stability. Integrate diffuse + Lumen reflections from the same traces.
- VERIFY: clean, stable, Lumen-like image on all test scenes; perf budget acceptable.

## Constraints / honesty
- This is genuinely weeks of work; do it phase-by-phase, never one giant unverified leap.
- No UE source copying (EULA). Reimplement from the public SIGGRAPH/Epic technical talks.
- Each phase gated behind an env/flag for A/B against the current path until it's proven better, so we
  never ship a regression. The current per-mesh SDF path stays as fallback until full Lumen replaces it.
- Performance is a first-class constraint (Lumen targets ~consoles; we target a high-end PC GPU — the
  user said GPU-heavy is fine but keep CPU headroom).

## Status
- DONE pre-plan: SDF bake-budget fixes (cap-on-baked-slots, size-priority), override-toggle plumbing,
  Lumen lazy-build, `MeshSdfBaker.BakeWorldTriangles`.
- NEXT: Phase 1 (Global Distance Field clipmap).
