# DirectX 12 + DXR Migration Plan (2026-06-15)

Branch: `dx12-renderer` (forked from `635584b2` on `renderer-good-baseline`; nothing deleted —
the full GL renderer + the abandoned Lumen work stay on `renderer-good-baseline` as a reference
archive and a fallback).

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
