# SDF World-Space GI (P6.3–P6.5) — design

Goal: dynamic OFF-SCREEN indirect light — the Lumen differentiator the baked IBL probes
(static) and SSGI (screen-space, can't see off-screen) cannot provide. Mesh-SDF path
(user's explicit choice). Built on the clean `renderer-good-baseline`, **gated behind a
default-OFF flag so the user-approved look never regresses until verified.**

## CPU foundation (DONE, committed)
- `Abstraction/Rendering/GI/MeshSdf.cs` — baked signed-distance grid, mesh-local units,
  trilinear `Sample`. Negative = inside.
- `Abstraction/Rendering/GI/MeshSdfBaker.cs` — `Bake(MeshData, Settings{MaxResolution,
  PaddingFraction})`. BVH-accelerated, parallel; sign = ray-stab parity over 7 generic rays.
- `Abstraction/Rendering/GI/TriangleBvh.cs` — ClosestDistanceSq + IsInside.
- `AssetPipeline/Library/SdfArtifact.cs` — .bsdf codec (magic 'BSDF' v1) + `StampFor(mesh)`.
- `AssetPipeline/Library/SdfCache.cs` — `GetOrBake(bmeshArtifactPath, mesh, settings)`.
All verified 16/16 in `%TEMP%/bal-sdf-test`.

## GPU subsystem (P6.3, this phase) — all in `OpenGL/Rendering/GI/`

### Components
1. **GLSdfAtlas** (`OpenGL/Rendering/GI/GLSdfAtlas.cs`)
   - One R16F 3D texture (raw `GL.TexStorage3D` — NOTE: `GLTexture3D.cs` is a misnamed
     CUBEMAP, do NOT use it). Linear filter, ClampToEdge.
   - Simple shelf/row allocator: pack each distinct mesh's MeshSdf grid as an axis-aligned
     sub-volume. Track per-slot { ivec3 atlasOffset, ivec3 res, vec3 boundsMin, vec3
     boundsMax } in CPU + an SSBO.
   - Upload a MeshSdf via `GL.TexSubImage3D` into its slot. Cap atlas size (e.g. 512^3
     R16F = 256MB; start 256^3 = 32MB) and skip meshes that don't fit (log it — no silent
     truncation).
   - Key meshes by `Mesh.InstanceId` (runtime) for the in-memory slot map; bake via
     `MeshSdfBaker.Bake` directly from `Mesh.Vertices/Indices` (build a MeshData view) OFF
     the GL thread, upload on the GL thread.

2. **GLSdfScene** (`OpenGL/Rendering/GI/GLSdfScene.cs`)
   - Per visible/opaque renderer with a bakeable mesh: an instance record
     `{ mat4 worldToLocal; uint slot; uint _pad0,_pad1,_pad2; }` in an SSBO (binding 8).
   - Build once per frame (or when the renderer set / transforms change) from the renderer
     list `GLHDRenderer` already computes (visibleOpaque + full opaque). Use
     `IDrawable.Transform` world matrix; `worldToLocal = invert(world)`.
   - A small instance-count + atlas-slot-table SSBO (binding 9) the march reads.

3. **SdfTrace_Comp.glsl** (`OpenGL/Shader/Embedded/SdfTrace_Comp.glsl`, #version 460,
   route through `GLSLShaderUtilities.ToAscii`)
   - Reconstruct world pos+normal from the G-buffer depth+normal (same `ViewPosFromDepth`
     math the SSGI/SSR shaders use; transform to world with InvView).
   - For each output pixel (half-res), trace a few cosine-hemisphere rays in WORLD space.
     For each ray, sphere-trace: at each step, for each instance whose local-space AABB the
     point is near, transform the point to local, sample the atlas sub-volume (manual
     trilinear in texel space), take min distance; advance by that distance. Hit when
     distance < epsilon.
   - On hit: return the hit world pos + geometric normal (gradient of the SDF). v1 radiance
     = sample the lit scene's irradiance at the hit via the existing baked IBL irradiance
     map in the hit normal direction (cheap, gives colored off-screen fill); a later phase
     swaps this for the surface cache (P7). Miss = sky irradiance.
   - Output an RGBA16F half-res "off-screen GI" texture: rgb = gathered indirect, a =
     confidence/validity.

4. **GLSdfGiPass** (`OpenGL/Rendering/GI/GLSdfGiPass.cs`)
   - Owns the atlas, scene, compute program, output RT (transient pool for scratch; the
     output is consumed same-frame so no history needed yet).
   - `Render(...)` dispatches the compute, returns the GI texture id.
   - Bindings: SSBO 8 = instances, 9 = slot table; image/sampler units local to the pass.
     Do NOT reuse 2–7 (GpuDriven) or UBO 0 (PassData).

### Integration (P6.5)
- Frame order: run the SDF-GI compute AFTER Opaque + HiZ (depth+normal ready), BEFORE SSGI,
  so SSGI can consume it. `GLHDRenderer.BeginRender` ~ line 726 area.
- Inject into SSGI's sky-open sectors (the gather already leaves open-hemisphere sectors for
  sky — feed SDF off-screen radiance there instead of/in addition to flat sky), OR add a new
  `SdfGiTexture` sampler to `Frag.glsl`'s ambient block (~547–628) added to `ambientDiffuse`
  **purely additively** (`ambientDiffuse += sdfGi * kD * ao` — never darken below no-GI, the
  hard-won lesson). Prefer the SSGI-sector injection (cleaner, reuses denoise/temporal).
- A new `uniform float SdfGiIntensity` set PER-PASS in SetupProgramForPass (the plain-uniform
  gotcha: NOT via GLUniformBlock.b.Set, NOT once-per-program).

### Flag + safety
- `BALLISTIC_SDFGI=1` (default 0) enables the whole subsystem. Also a volume/editor control
  later. With the flag off, the renderer is byte-identical to the committed baseline.
- Auto-disable if compute/bindless unsupported, or atlas alloc fails.

### Verification (P6.5)
- ENCLOSED sun-starved view only (GI invisible in bright exterior — #1 recurring lesson).
  Scenes: SunTemple interior (have it), BistroInterior_Wine.scene.
- `BALLISTIC_SCREENSHOT_PAUSED=1` + `imgdiff.py`: SDFGI on vs off should show indirect fill
  in the recesses/alcoves that SSGI alone misses (off-screen bounce), WITHOUT darkening lit
  surfaces. An SDF-only debug view (BALLISTIC_SDFGI_DEBUG=1) to see the raw gather.
- Adversarial checks: (a) flag OFF ⇒ byte-identical to baseline (meanError 0); (b) no NaN/black
  holes (ternary NaN scrubs, never mix); (c) bake time bounded + cached; (d) atlas overflow logs.

## Gotchas (carry forward)
- Compute source through `GLSLShaderUtilities.ToAscii` (em-dash truncates → "unexpected EOF").
- GLSL NaN scrub = ternary select, NEVER `mix(v,0,flag)` (NaN*0==NaN). Applies to any temporal
  feedback.
- Whole-mesh Bistro = ONE mesh asset → one coarse SDF (v1 ok). Per-submesh SDF is better Lumen,
  later. Bound resolution; bake off the GL thread; cache (.bsdf) makes it one-time.
- MemoryBarrier(ShaderStorageBarrierBit) after the dispatch before the consumer reads.
- Build per-project (MCP exe lock); editor needs RESTART to load engine.dll changes.
