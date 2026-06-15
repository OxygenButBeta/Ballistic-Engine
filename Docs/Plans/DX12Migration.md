# DirectX 12 + DXR Migration Plan (2026-06-15)

Branch: `dx12-renderer` (forked from `635584b2` on `renderer-good-baseline`; nothing deleted —
the full GL renderer + the abandoned Lumen work stay on `renderer-good-baseline` as a reference
archive and a fallback).

---

## 🔻 STRATEGY CHANGE #2 (2026-06-15, user) — REDESIGN THE ABSTRACTION DX-NATIVE FIRST

User: "şu anki soyutlamalar pek iyi değil, çok amatörce yazıldı; bizi zor patikalara sokuyorsa
değiştirmekten çekinme çünkü zaten DX'e geçiyoruz, GL iptal." + "full kontrol sende, ben AFK, /loop."

So: do NOT bridge the DX12 backend onto the GL-shaped abstraction. REDESIGN the render abstraction to
be DX-native FIRST, then build the renderer on it. GL is cancelled — zero back-compat obligation. The
GL-isms to remove (each forced ugly no-op/wrapper code in the first bridge attempt, now reverted):
  - `RenderContext` == a VAO. DX12 has no VAO → the concept should not exist (or become a trivial device
    carrier with no Activate/Deactivate).
  - `GPUBuffer<T>.Activate/Deactivate` (VAO-bind model) → no-ops in DX12. Drop the bind model; buffers
    expose GPU address + size, the renderer binds per-draw.
  - `Shader` per-name uniform API (SetMatrix4/SetFloat3/ActivateShader...) → constant buffers +
    descriptor tables in DX12. The DX12 shader was a 100% no-op stub faking this. Replace the model.
  - Four SEPARATE vertex buffers (pos/normal/uv/tangent) → DX12 wants ONE interleaved vertex buffer +
    one upload. Cleaner and faster.
  - `Texture.Activate/Deactivate` (texture-unit binding) + `protected internal Upload` (cross-assembly
    wrapper hack) → descriptor-based binding; a clean public upload entry.
  - `Material.Activate/Deactivate` (binds units + a `LastActivatedMaterial` static) → a material is a
    descriptor table the renderer points at.
Approach: map the FULL abstraction surface + every engine call-site (Mesh/Material/GraphicAPI/
SkyboxRenderer + others) → design the DX-native seam → migrate the engine types onto it → build the DX12
backend on it. Working autonomously in /loop: commit each increment. (First-bridge files Dx12*.cs are
being reworked onto the new seam, not kept as-is.)

## 🔻 STRATEGY CHANGE (2026-06-15, user directive) — FULL DX12, NOT side-by-side

The user decided: **GL will be deleted at the end anyway, and maintaining two backends in parallel is
not worth it — think full-DX-focused.** This SUPERSEDES the "side-by-side / GL parity oracle" strategy
below (kept for history). Concretely:

- **No two-backend maintenance.** We do NOT keep GL byte-identical, do NOT `bal imgdiff` every DX phase
  against a live GL backend, and do NOT preserve the abstraction seam *for GL's sake*.
- **GL is dead-man-walking, not the oracle.** GL code stays in the repo only so the engine keeps running
  while DX12 is incomplete; it gets ZERO new work and is deleted once DX12 reaches parity. The user chose
  "delete GL now, full DX" — so the GL *host/runtime path* is being retired; the GL renderer files come
  out as DX12 replaces each piece (not one big-bang delete that bricks the engine).
- **Parity oracle is now a frozen IMAGE, not a backend.** A GL SunTemple capture is saved as a permanent
  PNG reference (`e:/tmp/gl_suntemple_baseline.png`, mean RGB (96.7, 81.9, 65.6), 1920x1080, frame 120,
  deterministic paused). DX12 output is judged by EYE + plausibility against that image (color balance,
  geometry, shadow direction), not a numeric cross-backend diff.
- **The abstraction seam (RenderAsset/HDRenderer) is kept where it's load-bearing for ENGINE types**
  (Mesh/Material call `RenderAsset.Current.Create*`; HDCamera calls `renderer.BeginRender`). It is NOT
  kept pristine to host a second backend. Flatten it opportunistically as GL leaves (Phase 7 work that
  can now start earlier). Don't over-invest in keeping it backend-neutral.
- Phases below still hold in ORDER (mesh→material→shadows→post→GPU-driven→FSR→DXR-GI); only the
  per-phase *verification* changes from "imgdiff vs GL" to "looks right + .stats.json sane". SSGI still
  last.

## ⏩ HANDOFF / RESUME POINT (read this first — UPDATED 2026-06-15, autonomous /loop)

**Active work: the DX-NATIVE ABSTRACTION REDESIGN** — full plan + the authoritative 10-step execution
sequence is in `Docs/Plans/dx-native-abstraction-redesign.md` (source-verified, from a 5-agent call-site
map). GL is being deleted; build DX-native, no back-compat. Working autonomously in /loop, commit each step.

**Steps DONE (committed on `dx12-renderer`):**
- Step 0 — moved `InstancedBuffer` base OpenGL/ → Abstraction/ (layering fix so deleting GL won't break the build).
- Step 1 — promoted `GpuAddress/ElementCount/Stride/ByteSize` onto the `GPUBuffer<T>` base (renderer reads addresses without casting).
- Step 2 — dropped dead `RenderAsset.InstancedDrawing`.
- Step 3 — **DX12 headless host works**: `BALLISTIC_BACKEND=dx12` → `Dx12HeadlessRuntime` (real DirectXRenderAsset device + offscreen target, windowless) drives the engine loop, captures at `BALLISTIC_SCREENSHOT_FRAME`, reads the DX12 target back to BMP. VERIFIED: SunTemple brings up the device, loads the scene, runs BeginRender, writes a valid 1920x1080 BMP (clear color, draws=0 — renderer is still a shell). Made `IEngineTimer.Update` public for the out-of-assembly host. Runtime now refs BallisticEngine.DX12.

**🟢 DX12 FIRST LIGHT ACHIEVED (2026-06-15):** `BALLISTIC_BACKEND=dx12` renders SunTemple end-to-end —
1056 submesh draws, 606k tris, per-material diffuse + directional N·L + ambient, ACES-tonemapped, depth-
tested, BMP readback. Visually correct + brightness-matched to the GL baseline (mean ~95.5 vs 96.7).
Refs: `Docs/Plans/dx12-refs/dx12_suntemple_firstlight.png` vs `gl_suntemple_baseline.png`. The DX-native
abstraction (Steps 0-3) carries it: DX12HDRenderer.BeginRender iterates RuntimeSet<IStaticMeshRenderer>
(no per-frame reflection), per-draw CBV + per-material SRV descriptor table, root sig CBV(b0)+SRV(t0)+
sampler, HLSL StandardOpaque. **THE hard bug:** texture CopyTextureRegion E_FAILed in-engine only —
asset uploads shared ONE command list with BeginRender; fix = dedicated upload allocator/list/fence
(Dx12Device.ExecuteUpload). Debug layer off by default (not installed here; unsafe under concurrent create).

**Reordered from the synthesized 10-step plan** (editor depends on GL for ImGui, so retiring GL from the
build early would break the editor exe — see Docs/Plans/dx-native-abstraction-redesign.md execution note).
Current order keeps engine+editor BUILDING: DX12 no-ops the GL-shaped bind methods instead of deleting
them; GL stays compiling (editor host) with zero new work; build DX12 up to parity FIRST, delete GL last.

**DONE since first light (committed):** full PBR (Cook-Torrance GGX direct sun + 6 material maps
diffuse/normal/metallic/roughness/AO/emissive, glTF factors, normal mapping, ORM, cutout — mirrors GL
Frag.glsl; ref dx12_suntemple_pbr.png); full pre-baked mip-chain texture upload (the earlier multi-mip
E_FAIL was the shared command list, not the mip math — dedicated upload queue fixed it). SunTemple renders
with proper specular + crisp mipped textures, brightness near baseline.

**DONE since PBR (committed):** skybox background pass — DX12 owns its sky PSO + cube draw + a typed
SkyboxConstants CBV (NOT the GL per-name uniform API), LEqual/no-depth-write at the far plane. Correct by
construction; invisible in the enclosed SunTemple interior view (no far-plane pixels), so a sky-visible
scene is needed to eyeball it.

**Both backends build + run; GL has ZERO regressions** (SunTemple GL draws=3 unchanged). DX12 via
BALLISTIC_BACKEND=dx12; GL is still default + the editor's renderer.

**🟢 DX12 PROCEDURAL SKY (2026-06-15):** ported Sky_Procedural.glsl clean-sky path — Rayleigh+Mie+ozone
atmosphere + sun disk + ground, marched PER-PIXEL in the far-plane skybox pass (pure ALU, CBV-only — no
cubemap bake, unlike GL). Driven by the scene DirectionalLight + ProceduralSky params. VERIFIED on
BistroExterior (1591 draws / 2.8M tris — the engine's big exterior): atmosphere visible, composition
matches GL. Refs dx12_bistro_proceduralsky.png vs gl_bistro_baseline.png. ProceduralSky.Active wins over
a cubemap Skybox (GL parity). DX12 now renders TWO real scenes (SunTemple interior + Bistro exterior).

**USER DIRECTIVE (standing): port ALL the GL renderer features to DX12** over time — procedural sky (done),
then volumetric fog (explicitly named), and the rest. Feature-by-feature, commit each.

**🟢 DX12 IBL AMBIENT (2026-06-15):** full split-sum image-based lighting — Dx12IblBaker bakes the
procedural sky → env cube → 32³ cosine irradiance + 128³/5-mip GGX prefilter + 256² BRDF LUT; opaque
shader does ambientDiffuse(irradiance) + ambientSpecular(prefilter×BRDF). New infra: Dx12CubeTarget
(render-to-cube, per-face/mip RTVs). Re-bakes only on sun/atmosphere change. Material + IBL SRV tables
share one shader-visible heap (IBL takes the first 3 slots/frame). VERIFIED on BistroExterior — dramatic
correct lift (facades/cobbles/foliage lit, not flat-dark; mean 25→32; ref dx12_bistro_ibl.png). SunTemple
(no ProceduralSky) uses the flat-ambient fallback, unchanged. DX12 now: PBR + procedural sky + split-sum
IBL + mipped textures, on 2 real scenes.

**🟢 DX12 CASCADED SHADOWS (2026-06-15):** sun shadow maps — Dx12ShadowMath (4-cascade frustum-slab fit
+ texel snap, DX ortho z[0,1]) + Dx12ShadowMap (D32 depth array, DSV/layer, R32 array SRV) + depth-only
PSO (ShadowDepth.hlsl, slope-scaled bias) rendered per cascade on the upload list; opaque SunShadow() =
first-cascade select + 3×3 PCF, multiplies the direct sun. Per-frame FrameConstants CBV (b1) = cascade
matrices + bias; IBL/shadow SRV table now t6..t9 (shadow array t9). VERIFIED on SunTemple (clear
directional shadows, mean 87→66, no acne; ref dx12_suntemple_shadows.png) + Bistro. 4 cascades re-render
every frame (caching = later perf). **DX12 lighting model now complete: PBR + sky + IBL + shadows.**

**🟢 DX12 VOLUMETRIC FOG (2026-06-15, user-requested):** full-screen height-fog post pass
(VolumetricFog.hlsl) — reconstruct world pos from scene depth, raymarch toward camera through exponential
height fog, in-scatter shadowed sun (HG phase via the cascade array) + sky ambient, analytic tail past the
march, sun-disk glow; output (scatter, transmittance) blended over color (dest*transmittance + scatter).
Infra: Dx12OffscreenTarget depth made TYPELESS (D32 DSV + R32 SRV) so post reads depth; DepthTo*/
RenderColorOnly transitions. BALLISTIC_FX_VOLUMETRIC=1 forces on (GL harness parity). VERIFIED on
BistroExterior — aerial perspective, distant buildings haze out (mean 26→38; ref dx12_bistro_fog.png).
Single-pass at physical default density; half-res+temporal + sky-ambient readback are follow-ups.

**🟢 DX12 HDR PIPELINE (2026-06-15):** scene renders RAW HDR into an R16F target (material/sky/fog no
longer tonemap inline); a single Composite.hlsl pass does exposure→ACES→sRGB→LDR (the readback/display
target). Fog blends in HDR now. Byte-equivalent output (SunTemple unchanged) — the foundation that lets
auto-exposure + bloom exist. Infra: Dx12OffscreenTarget color format parameterized (HdrFormat) + color
SRV + Color/Depth transitions; HDR scene target + LDR composite target; SaveFrame reads LDR.

**DX12 stack now: PBR + procedural sky + IBL + cascaded shadows + volumetric fog + HDR/tonemap composite.**

**NEXT (DX12, each committable; user directive = port ALL GL features):** auto-exposure (luminance
reduction → drives the composite Exposure, replace the fixed 1e-5 stand-in — the Composite pass already
has the Exposure slot wired); bloom (bright-pass + blur, add into the composite's BloomTex/BloomIntensity
— already wired); TAA; then SSAO + SSR (SSGI LAST); sky clouds/cirrus/stars; alpha-cutout caster shadows;
cascade caching + interleave mesh verts (perf); finally editor→DX12 + delete GL wholesale (incl. the
GL-shaped bind methods + RenderContext the DX12 path no-ops). DON'T break the editor — it renders on GL;
delete GL only
at the very end.

The frozen GL parity image: `Docs/Plans/dx12-refs/gl_suntemple_baseline.png` (mean RGB 96.7,81.9,65.6).

--- (historical resume point below — superseded by the steps above) ---

**Where we are:** Phase 0 (abstraction prep) + Phase 1 core (DX12 device, raster pipeline, screenshot
readback) + Phase 2 partial (3D lit cube: mesh buffers, depth, MVP CBV, N·L lighting) are DONE,
COMMITTED, and VERIFIED on the `dx12-renderer` branch (12 commits since baseline). All 3 projects build
clean (`BallisticEngine.csproj`, `BallisticEngine.Runtime`, `BallisticEngine.DX12`). GL path untouched.

**The DX12 backend already works** (offscreen, no window yet): device + command queue/list/fence,
HLSL→DXIL compile (Vortice.Dxc), root sig + PSO + draw, depth buffer, vertex/index buffers, MVP
constant buffer, and byte-exact GPU→CPU BMP readback. Verified visually: an RGB triangle and a lit
3D cube (e:/tmp/dx12_cube_cull.png). DXR Tier1_1 confirmed on the RX 9070 XT.

**Files in BallisticEngine.DX12/:** Dx12Probe (device/DXR check), Dx12Device (device+queue+fence+
ExecuteSync), Dx12OffscreenTarget (RTV + optional DSV depth + Clear + SaveBmp readback),
Dx12ShaderCompiler (HLSL→DXIL), EmbeddedShaderSource (reads embedded .hlsl), Dx12TriangleTest,
Dx12LitCubeTest, Shaders/{Triangle,LitCube}.hlsl.

**Smoke-test harness:** a throwaway exe at `%TEMP%/bal-dx12-test/` (test.csproj + Program.cs) that
ProjectReferences BallisticEngine.DX12 and runs a render → BMP. Recreate it if gone (see any commit
message for the pattern). The real verification is BMP → PNG via PIL + `e:/tmp/rgbstat.py`.

**⭐ NEXT STEP (start Phase 2d here):** build `DirectXRenderAsset : RenderAsset` + `DX12HDRenderer :
HDRenderer` as the real second backend (the cube test proved the pieces; now wire them behind the
abstraction). Then port the engine's real mesh upload + the minimal material to feed a real `.scene`.
The seam is ready (Phase 0 cleaned it: RenderAsset.Current factory, RenderHandle, BufferUsage). Goal:
render an actual engine scene (SunTemple) on DX12 and `bal imgdiff` vs GL (perceptual budget, not
byte — see the verification note at the bottom). After that: shadows + post (Phase 3, SSGI LAST),
GPU-driven (Phase 4), then THE PAYOFF — FSR upscaling (Phase 5) and DXR GI + NRD (Phase 6), editor
to DX12 + retire GL (Phase 7).

**Carry-overs / gotchas locked in:** System.Numerics for DX12 (SIMD + DX z∈[0,1]); TRANSPOSE matrices
on CBV upload (HLSL float4x4 is column-major); Vortice quirks — CreateDXGIFactory1 (not Factory2+debug),
generic CreateCommandList<T>, GetCopyableFootprints (plural, array overload), Map<T>(0,len)→Span,
RasterizerDescription presets are CullNone/CullClockwise/CullCounterClockwise (NO CullBack), ID3D12Debug
in Vortice.Direct3D12.Debug. Lumen NOT carried; SSGI LAST. Editor build fails while the editor is OPEN
(DLL lock) — build it with `-p:BaseOutputPath=obj/compilecheck/` to compile-check.

---

## Decision & motive (settled with the user)

Migrate the RENDERER from OpenGL 4.6 (OpenTK) to **DirectX 12 + DXR** via **Vortice.Windows**.
The non-renderer engine is untouched.

**Why (the real, settled motive):** *upscaling performance + an industry-standard DXR foundation to
keep building on* (RT reflections, ReSTIR, ray reconstruction). NOT "just better GI than the SDF
path" — that payoff would be weak (DDGI+NRD is the same probe+denoise family already hand-rolled).
OpenGL's dead ecosystem genuinely blocks FSR/DLSS/XeSS upscaling, NRD denoisers, and RTXGI — DX12
reopens that path. Verified: Vortice.Direct3D12 3.8.3 fully exposes DX12 + DXR (AS, RT PSO,
DispatchRays, shader tables); Vortice.Dxc compiles HLSL in-process. C# DXR reference to skeleton
from: Jorgemagic/CSharpDirectXRaytracing (23 tutorials).

**Realistic size:** 6-12 months part-time, solo + AI. A second backend, not a port. The first
several months produce a picture identical to GL; the payoff (FSR, then DXR GI) is the back half.

## User directives (standing, this migration)

- Autonomous `/loop`: work → commit → work → commit while the user is away.
- **Be clean:** remove dead/needless code along the way (the migration is a chance to tidy).
- **Do NOT carry Lumen** into the DX12 renderer (start fresh; Lumen stays archived on the old branch).
- **SSGI is last** — post-FX after everything else; SSGI specifically deferred to the very end.
- Make the transition as efficient as possible.

## Strategy: side-by-side incremental (decisive)

`DX12HDRenderer : HDRenderer` + `DirectXRenderAsset : RenderAsset` as a SECOND backend, selected by
`BALLISTIC_BACKEND=dx12` (mirror the existing `BALLISTIC_GPUDRIVEN=0` env pattern), GL default.
Reasons: (1) GL stays the ground-truth **parity oracle** — render the same scene on both, imgdiff
each phase; (2) the engine is never broken — GL runs if DX12 stalls; (3) it forces the abstraction
seams clean. The architecture already supports this: `RenderAsset.Current` is the single host-injected
backend seam; `HDRenderer` is an abstract base with `GLHDRenderer` as one subclass; CPU data types
(MeshData/TextureData/Skeleton/AnimationClip/MeshNode/Terrain) are API-agnostic.

Cross-API frames will NOT be byte-identical to GL (different rasterizer rules). The parity oracle for
cross-backend diffs is a SMALL PERCEPTUAL BUDGET (mean + 32x32 hotspot, like `bal imgdiff`), while
byte-exactness is kept WITHIN a single backend.

## Math library (decided 2026-06-15)

The DX12 backend uses **System.Numerics** (NOT OpenTK.Mathematics): SIMD-accelerated, and DX-convention
(`Matrix4x4.CreatePerspectiveFieldOfView`/`CreateLookAt` are right-handed with NDC z ∈ [0,1], exactly
what DX12 wants — OpenTK's are GL z ∈ [-1,1]). The engine CORE stays on OpenTK.Mathematics; mesh/
transform data is converted at the backend boundary. HARD-WON convention: HLSL `float4x4` constant
buffers are COLUMN-major by default but System.Numerics is row-major in memory — **`Matrix4x4.Transpose()`
on upload**, then `mul(float4(pos,1), MVP)` in HLSL matches the CPU math. (Skipping the transpose was
the "cube fills the whole frame" bug.)

## Phases (each independently verifiable via the deterministic screenshot harness)

- **Phase 0 — Abstraction prep (GL still the only backend). ~1-2 wk. [DONE 2026-06-15]**
  Commits 3fdb446b (shader factory→RenderAsset), Leak3 (BufferUsage enum + drop OpenTK from the buffer
  abstraction + delete dead Engine/Rendering/SData/Buffer.cs), 4e817aa5 (RenderHandle for Scene/Game
  display textures), 9a5d808a (BALLISTIC_BACKEND selector seam). GL path unchanged (SunTemple 160.4,
  Bistro full draws=796/tris=521738). DebugFrame's int G-buffer texture ids left GL-coupled on purpose
  (editor-debug, Phase 7). Original (now-historical) leak list below:
  Make the backend seam clean with ZERO behavior change. Fix three confirmed leaks:
  1. `GraphicAPI.CreateStandardShader` hardcodes `new GLStandardShader()` → route through
     `RenderAsset.Current`.
  2. `HDRenderer.SceneColorTextureId`/`GameColorTextureId` + `DebugFrame`'s `int` texture fields are
     raw GL handles leaking into the editor contract → opaque backend-handle type (GL stores its int inside).
  3. `GPUBuffer<T>.Target` leaks OpenTK `BufferTarget` into the abstraction → backend-agnostic enum.
  Plus: delete dead code found along the way (e.g. `Engine/Rendering/SData/Buffer.cs` if dead); add a
  `BALLISTIC_BACKEND` env switch (stubbed to GL only).
  **Verify:** every deterministic paused screenshot BYTE-IDENTICAL before/after (meanError 0).
  Baselines captured pre-Phase0: SunTemple mean (160.5,146.6,131.4); Bistro (46.0,32.3,25.9), frame 300.

- **Phase 1 — DX12 device + clear + triangle + readback. CORE DONE 2026-06-15 (offscreen-first).**
  Commits: 1a (BallisticEngine.DX12 project + Vortice 3.8.3 + Dx12Probe — VERIFIED "DX12 OK: AMD Radeon
  RX 9070 XT | DXR Tier1_1"), 1c+1e (Dx12Device: device/queue/allocator/list4/fence + ExecuteSync;
  Dx12OffscreenTarget: RTV + Clear + GetCopyableFootprints→CopyTextureRegion→readback heap→Map→BMP,
  VERIFIED byte-exact 204,51,102), 1d (Dx12ShaderCompiler HLSL→DXIL via Vortice.Dxc SM6.6; Triangle.hlsl
  SV_VertexID; root sig + PSO + draw — VERIFIED RGB triangle e:/tmp/dx12_triangle.png). Vortice API
  notes: CreateDXGIFactory1 (not Factory2+debug), generic CreateCommandList<T>, GetCopyableFootprints
  (plural array overload), Map<byte>, ID3D12Debug in Vortice.Direct3D12.Debug.
  STILL TODO this phase: windowed swapchain + present (1f — deferred, offscreen covers the harness);
  Windows input provider to replace GLInput (when the windowed host arrives).
  ORIGINAL phase scope below:
  **Verify:** `BALLISTIC_SCREENSHOT` on the dx12 backend produces a BMP + .stats.json + exit code,
  identical pipeline to GL.

- **Phase 2 — Forward/deferred parity: mesh, material, PBR, tonemap. ~4-8 wk.**
  Port the core material shader(s) + tonemap/composite to HLSL. Mesh upload via DirectXRenderAsset.
  PBR math is API-agnostic (copy GLSL math into HLSL). Z-prepass with HLSL `invariant` on SV_Position
  to preserve the prepass contract. PassData UBO → D3D12 constant buffer.
  **Verify:** `bal render` same scene on GL + DX12, `bal imgdiff` small perceptual budget.

- **Phase 3 — Shadows + post-FX suite. ~2-4 mo (the long grind; most of the 68-shader HLSL port).**
  Cascaded shadow maps (HLSL depth-only; cascade-caching logic is API-agnostic). Port post: SSAO,
  bloom, TAA, SSR, volumetric, auto-exposure. **SSGI LAST (user directive).** Re-plumb TAA jitter
  (reused by the FSR upscaler in Phase 5).
  **Verify:** per-pass A/B via `BALLISTIC_FX_*` toggles on both backends + imgdiff.

- **Phase 4 — GPU-driven path (ExecuteIndirect + bindless). ~4-8 wk.**
  DX12 ExecuteIndirect (= glMultiDrawElementsIndirectCount), compute frustum cull (HLSL compute),
  bindless via SM6.6 ResourceDescriptorHeap, Hi-Z occlusion pyramid. Re-implement the per-draw
  shader-injection transform.
  **Verify:** draw-count + per-pass GPU ms in .stats.json match GL GPU-driven (Bistro ~3 MDI draws).

- **Phase 5 — PAYOFF 1: FSR upscaling. ~3-5 wk.** ← the "cost is knocking" fix.
  P/Invoke AMD FidelityFX FSR's flat C ABI (~5 functions; vendor-agnostic, runs on the RX 9070 XT,
  MIT). Feed pre-upscale HDR color + depth + motion vectors + per-frame jitter (from the TAA jitter,
  applied to projection, NOT double-applied). Handle resource-state across the managed/native boundary.
  **Verify:** 1080p internal → 4K out; imgdiff sanity vs native 4K; .stats.json shows res + perf gain.

- **Phase 6 — PAYOFF 2: DXR ray-traced GI + NRD denoiser. ~3-6 mo (largest, riskiest).**
  Full DXR acceleration structures (BLAS/TLAS), RT PSO (CreateStateObject, DXIL lib_6_x, hit groups,
  root sigs), shader binding table, DispatchRays for the GI signal. Then RTXGI/DDGI or own ray-traced
  irradiance. Emit the per-ray hit-dist/view-Z/normal-roughness/motion G-buffer NRD needs, integrate
  NRD (ReBLUR/ReLAX) via P/Invoke or a thin native glue DLL. (NRD is NOT a drop-in for SVGF — it wants
  a ray-traced signal, so GI must move to DXR first.)
  **Verify:** GI-isolate debug view on DX12; judge the ISOLATED bounce; denoiser convergence on a
  paused deterministic frame.

- **Phase 7 — Editor to DX12 + retire GL. ~2-4 wk.**
  Port the 7 GL-touching editor files (DX12 ImGui backend = vertex/index stream + font texture +
  scissor; the two preview renderers; ThumbnailCache; EditorDebugViews; texture uploads). The runtime
  produces DX12 scene textures so ImGui::Image samples them natively (no GL/DX interop — that path was
  rejected as fragile). Once parity holds, delete `OpenGL/` + the OpenTK dependency.
  **Verify:** editor screenshots match GL editor; zero OpenTK references (grep-verifiable).

## Reusable as-is (NOT touched by the migration)

Entity/Behaviour ECS, scenes+YAML, ComponentReflection/Registry, edit/play split, ScriptGuard;
BepuPhysics behind IPhysicsWorld; Roslyn scripting hot-reload; asset pipeline (AssetDatabase,
ModelImporter, TextureImporter + BCn compression — BC blocks upload to DX12 fine, Unity import);
all CPU data types; the abstract HDRenderer/RenderAsset/RenderContext seam; the bal CLI + MCP +
named-pipe RPC; HeadlessRuntime + determinism; audio (OpenAL); input ABSTRACTION (only GLInput is
replaced); the volume/post-process framework (data-driven); 66 of 73 editor files (panel logic);
the screenshot-harness scaffolding (only the GL readback primitive is replaced). Renderer
*methodology* (z-prepass invariance, cascade caching, per-submesh culling, GPU-driven design,
instancing sort, transient RT pool) ports conceptually — only the API calls change.

## Risks (front-loaded)

1. DX12 verbosity is real & front-loaded — months 1-3 reproduce the GL picture. Morale dip before
   the FSR/DXR payoff. Skeleton from the C# DXR tutorials; treat FSR (Phase 5) as the first reward.
2. 68 GLSL → HLSL by hand. PBR math is mechanical; resource binding, the GPU-driven #define injection,
   compute shaders, and the `invariant` prepass contract need care. PIX for debugging.
3. The screenshot harness has no `glReadPixels` in DX12 — reimplement readback (CopyResource → readback
   heap → Map → memcpy) + the ID-map readback. Get this rock-solid in Phase 1 or lose the parity oracle.
4. DXR/NRD/RTXGI have ZERO managed bindings — hand-written P/Invoke or native glue per SDK; marshalling
   native D3D12 handles + barriers across the managed boundary is a crash source (run the debug layer).
5. NRD is not a drop-in — it implies moving GI to DXR first.
6. Windows-only + RT-class GPU permanent constraint (consistent with the existing GL-only stance).
7. Scope/time is the dominant risk — side-by-side keeps the engine shippable so a stall never bricks it.
8. Two-backend maintenance until GL retires (Phase 7) — don't let DX12-only concepts leak into the
   shared abstraction prematurely (corrupts the parity oracle).

## Verification harness (unchanged scaffolding)

Deterministic paused capture: `BALLISTIC_SCREENSHOT=<bmp> BALLISTIC_SCREENSHOT_PAUSED=1
BALLISTIC_DETERMINISTIC=1 BALLISTIC_SCREENSHOT_FRAME=N`, scene via `BALLISTIC_SCENE`. BMP→PNG via
PIL; stats via `e:/tmp/rgbstat.py`. Test scenes: SunTemple, BistroInterior_Wine. (CornellBox is a
broken GI fixture — don't judge on it.) Build: `dotnet build BallisticEngine.Runtime -c Debug`.
Editor build fails while the editor is OPEN (DLL lock) — that's not a code error.

**REALITY CHECK on "byte-identical" (measured 2026-06-15):** the engine is NOT byte-deterministic at a
fixed frame even on an UNCHANGED build, GI off — two runs differ by sub-0.1 mean RGB (async asset-upload
timing, TAA settling, float order). BistroInterior at frame 300 varies several units run-to-run (its GI
is still mid-warm-up + assets still streaming). So `cmp`/meanError-0 is the WRONG oracle. Use a
PERCEPTUAL budget: a pure-refactor phase passes if mean RGB matches within the SAME scene's run-to-run
noise (SunTemple ~±0.1; Bistro is too noisy at f300 — prefer SunTemple as the refactor oracle, or a
GI-off later frame). This is the same perceptual-budget bar the cross-API phases need anyway.
