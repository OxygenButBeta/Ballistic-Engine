I have everything needed. The reports are accurate, and the codebase is further along than the reports imply (DirectXRenderAsset, DX12HDRenderer, RenderBackendSelector, RenderHandle, Mesh buffer accessors all exist). My plan accounts for the real current state. Here is the synthesized plan.

---

> **EXECUTION NOTE (2026-06-15, autonomous /loop — reordered from this synthesized plan):** Step 4a as
> written ("delete bind methods → GL won't compile → retire GL from the build now") is too destructive to
> run early, because the **EDITOR exe depends on GL for ALL its rendering** (ImGuiGLRenderer,
> ImGuiController, Mesh/MaterialPreviewRenderer, EditorDebugViews, ThumbnailCache — ~7 files). Retiring GL
> from the build now breaks the editor entirely, not just GLHDRenderer. Reordered to keep engine+editor
> BUILDING throughout:
>   1. Keep GL compiling for now (editor's ImGui host + a working renderer); give it ZERO new work.
>   2. DX12 NO-OPS the GL-shaped bind methods (Activate/Deactivate) — cheap, harmless — instead of deleting
>      them yet. The bind-free *deletion* defers to when GL is actually deleted.
>   3. Build the DX12 renderer to ACTUALLY DRAW SunTemple FIRST (the prize): interleaved mesh upload (5) +
>      material descriptor table (8) + opaque/sky pipeline (9). GL untouched meanwhile.
>   4. THEN, once DX12 renders AND the editor's GL dependency is ported (Phase 7), delete GL wholesale —
>      bind methods, OpenGL/, GLStandardShader (10), one clean cut.
> Same destination; no window where engine/editor can't build. Steps 0-3 already done as written.

---

# Ballistic Engine — DX-Native Render Abstraction Redesign

Synthesized from 5 mapping reports, **verified against live source** on branch `dx12-renderer`. Key correction to the reports: the bridge is further along than they assume — `BallisticEngine.DX12/DirectXRenderAsset.cs`, `DX12HDRenderer.cs`, `RenderBackendSelector`, `RenderHandle`, and Mesh's GPU-address accessors (`Mesh.VertexBuffer`...`Mesh.IndexBuffer`) **already exist and compile**. So this is a *finishing + cleanup* plan, not a from-scratch one. One real layering bug found: the `InstancedBuffer` base class lives in `OpenGL/` (`OpenGL/Rendering/Buffers/InstancedBuffer.cs`), not `Abstraction/` — it must move before GL is deleted or the engine won't compile.

---

## 1. THE NEW ABSTRACTION (concrete, per type)

### RenderAsset (`Abstraction/Rendering/RenderAsset.cs`)
The factory survives almost intact — it is already backend-agnostic and `DirectXRenderAsset` already implements every member. Final shape:

- **KEEP**: `Current`, `Renderer`, `Initialize()`, `CreateTexture2D`, `CreateCubemap`, `CreateStandardShader`, `CreateBuffer<T>`, `CreateInstancedBuffer`.
- **DROP**: `InstancedDrawing` (bool) — never read by engine code; GL-only signal. Remove from base, `OpenGLRenderAsset`, `DirectXRenderAsset`, `NullRenderAsset`.
- **DROP** the `RenderContext`-parameterized buffer factories once Mesh stops needing per-stream buffers (`CreateVertexBuffer3/2/UVBuffer/Normal/Tangent/BoneIndex/BoneWeight/IndexBuffer`). They survive *only* until step 5 below; the end state keeps just **`CreateMeshBuffers(MeshData) -> MeshGpu`** (one call that builds the interleaved vertex buffer + index buffer + optional skin buffer) and the generic `CreateBuffer<T>()` (still needed for instance/skin/draw-indirect scratch). Interim: keep them but make `RenderContext` optional (see below).
- **CHANGE**: `CreateRenderContext()` — see RenderContext verdict; it stays as a *device-carrier* only in the interim, then is removed from the public factory surface.
- **CHANGE**: `CreateUVBuffer` / `CreateVertexBuffer2D` are aliases of `CreateVertexBuffer2` — collapse to one (`CreateVertexBuffer2`). They die entirely with the interleaving step.

### RenderContext (`Abstraction/Rendering/Buffers/RenderContext.cs`)
**DOES NOT SURVIVE** as an engine-visible concept. There is no VAO in DX12; the Dx12 impl is already a no-op device-carrier (`Dx12RenderContext`). Path:
- **Interim**: keep the class, keep `Dx12RenderContext` as the device carrier, but the engine stops calling `Activate()/Deactivate()` (no-ops on DX, harmful nowhere).
- **End state**: delete `RenderContext.cs`, `Dx12RenderContext.cs`, `Mesh.renderContext`, `Mesh.Activate()/Deactivate()`, `Renderer.Activate()/Deactivate()`, and all `GraphicAPI.Create*(RenderContext)` overloads. The device reaches buffers via `Dx12Backend.Device` (a static, already used by `Dx12Buffer`/`Dx12Texture2D`) — no context object needed.

### GPUBuffer<T> (`Abstraction/Rendering/Buffers/GPUBuffer.cs`)
Becomes a pure data-holder with GPU-address accessors, no bind state.
- **DROP**: `Activate()`, `Deactivate()` (abstract — remove from base + all impls). Pure VAO semantics.
- **DROP**: the `RenderContext` ctor param (end state). Interim: keep it but allow `null`.
- **CHANGE**: `Create()` stays as a marker (Dx12 already no-ops it), or fold into `SetBufferData`. Prefer **drop `Create()`** and allocate on `SetBufferData` (Dx12 already does this; GL is being deleted so its "gen then fill" ordering no longer matters).
- **ADD** (promote from `Dx12Buffer`): public `ulong GpuAddress`, `int ElementCount`, `int Stride`, `int ByteSize`. These move onto the abstract base so the renderer reads them without casting to `Dx12Buffer<T>`.

### InstancedBuffer (currently `OpenGL/Rendering/Buffers/InstancedBuffer.cs` — **MOVE to Abstraction/**)
- **MOVE FIRST** (layering bug): `InstancedBuffer : GPUBuffer<Matrix4>` is referenced by the abstraction (`RenderAsset.CreateInstancedBuffer`) and Engine (`Mesh.InstanceBuffer`) but defined in `OpenGL/`. Move to `Abstraction/Rendering/Buffers/InstancedBuffer.cs` so deleting `OpenGL/` doesn't break the build.
- **CHANGE**: drop `Create()`/`Activate()`/`Deactivate()`. Keep `SetBufferData(in Matrix4[] data, BufferUsage)` (Dx12 maps an upload heap; signature unchanged so `GLHDRenderer`-era callers in the renderer still compile). Promote `GpuAddress`/`Stride`/`ElementCount` to the base.
- Matrices stay `OpenTK.Mathematics.Matrix4` at the engine boundary; conversion to `System.Numerics.Matrix4x4` happens inside `Dx12InstancedBuffer.SetBufferData` (already implemented).

### Shader / StandardShader (`Engine/Rendering/SData/Shader/`)
The per-name uniform API is the one piece that does **not** map to DX12, and `Dx12StandardShader` already stubs every setter to a no-op. Decision:
- **KEEP** `Shader` and `StandardShader` as types — `Material.Shader` and `SharedResources<Shader>` dedup both depend on them, and they remain a *handle + source carrier*.
- **KEEP** `StandardShader.VertexCode`/`FragmentCode` (the renderer needs source for prepass/GPU-driven variant derivation — DX12 will need the HLSL equivalents at the same seam).
- **DROP** the per-name uniform API from the *abstraction*: `SetBool/Int/Float/Float2/3/4/SetMatrix4`, `Activate()/Deactivate()`. The engine has exactly **one** caller (SkyboxRenderer) — migrate it (below) and delete the methods. After that, `Dx12StandardShader` loses 8 no-op overrides.
- **DELETE** `Engine/Rendering/SData/Shader/GLStandardShader.cs` with GL.
- **How per-frame/per-draw data flows instead**: a typed **constant-buffer struct** the renderer fills directly. The engine never sets a named uniform again. `DX12HDRenderer` owns its CB0 (`PassData` equiv) and per-draw CBV/root-constants — exactly the model the GL `PassData` UBO already foreshadowed. The SkyboxRenderer's 5 values (rotation, exposure, view, projection, cubemap slot) become fields on a `SkyboxConstants` struct the renderer writes; the cubemap "unit 11" becomes a fixed descriptor-table slot.

### Texture / Texture2D / Texture3D (`Engine/Rendering/SData/Texture/`)
- **KEEP** all three types and `Texture.UID` (used for bindless-handle acquisition + editor `MaterialPreviewRenderer`).
- **DROP** `Texture.Activate()`/`Deactivate()` (abstract) — pure unit-binding, already no-ops on Dx12, only caller is `Material.Activate()` which is being deleted.
- **KEEP** `Texture2D.Upload(...)` (the upload contract; `Dx12Texture2D` implements it).
- **KEEP** `DefaultTextures.Neutral()` (pack-time fallback). No change to Texture3D's model — cubemaps are skybox/IBL only.

### Material (`Engine/Rendering/SData/Material.cs`)
Material stops being a *bindable* object and becomes a pure **immutable property bag** the renderer's material table reads.
- **DELETE** `Material.Activate()`, `Material.Deactivate()`, and the `static LastActivatedMaterial` cache (dead once nothing binds units).
- **KEEP** every property exactly as-is (`Diffuse/Normal/Metallic/Roughness/AO/Emissive/Shader` + all scalar/flag PBR factors). They are already read read-only by `GpuMaterialTable.Pack()`. The DX12 equivalent of `GpuMaterialTable` (descriptor table + material SSBO/CBV) reads the same members at table-build time.
- **How the texture set is expressed without Activate/units**: the renderer builds a per-frame material table (descriptor table of the 6 SRVs + a structured buffer of the scalar factors), keyed by material reference, exactly as `GpuMaterialTable` does today. Null slots resolve via `DefaultTextures.Neutral()` at pack time so every descriptor is valid. The shader indexes the table by a per-draw material id (root constant). This is the **single path** — no CPU "activate 6 units" path remains.

### HDRenderer (`Abstraction/Rendering/Renderer/HDRenderer.cs`)
The base contract is already clean and `DX12HDRenderer` already subclasses it. Keep the public surface; tidy the internals.
- **KEEP**: `BeginRender`, `PostRenderCleanUp`, `ResizeSceneTarget/ResizeGameTarget`, `SceneColorHandle/GameColorHandle` (RenderHandle), `ActiveTarget`, `PresentToScreen`, `DebugViewMode`, `ReadSceneDepthGrid()`, `PostFX`, `Initialize`.
- **CHANGE** `RenderOpaque/RenderSkybox/RenderInstancing` from `abstract` to `protected abstract` (or remove from the base and make them private to each backend) — no engine/editor caller exists; they are internal pipeline steps. This lets `DX12HDRenderer` implement them however it likes without leaking into the contract.
- **KEEP** `DebugFrame` for now but its raw GL `int` texture fields are Phase-7 editor-debug only (acknowledged); leave GL-coupled until the editor-debug composite is ported.
- **CHANGE** `RenderMetrics` only if DX12 needs different counters — draw count stays meaningful; no structural change required for SunTemple.

---

## 2. ENGINE MIGRATION CHECKLIST (by file)

### `Engine/Rendering/SData/Mesh.cs`
- **Line 45** `readonly RenderContext renderContext;` → **remove** (end state). Interim: keep, pass `null` once factories drop the param.
- **Lines 61–62** `renderContext = RenderAsset.Current.CreateRenderContext(); renderContext.Activate();` → **remove**.
- **Lines 64–68, 83–84, 100** buffer creation via `GraphicAPI.Create*(renderContext)` → **change** to one call: `var gpu = RenderAsset.Current.CreateMeshBuffers(in data);` returning a struct holding the interleaved vertex buffer + index buffer (+ optional skin buffer + instance buffer). Interim acceptable: keep the 4 separate `Create*()` calls but pass `null` for the context.
- **Line 101** `InstanceBuffer.Create()` → **remove** (`Create()` dropped).
- **Lines 102–104** `FillBuffers(); renderContext.Deactivate();` → **change**: `FillBuffers()` becomes the interleave-and-upload step (or is folded into `CreateMeshBuffers`); drop `Deactivate()`.
- **Lines 171–179** `Mesh.Activate()/Deactivate()` → **DELETE** (no VAO to bind). All callers (`Renderer.Activate/Deactivate`, `SkyboxRenderer`) are also being deleted.
- **Lines 36–44** the 6 separate `GPUBuffer` fields → **change** to a single interleaved `GPUBuffer<Vertex>` + `indexBuffer` (+ skin). The accessors at **lines 51–57** the DX renderer reads stay, repointed at the interleaved buffer (renderer reads `GpuAddress`/`Stride`/`ElementCount`).
- **Lines 183–210** `FillBuffers()` → **change**: interleave `Vertices/Normals/UVs/Tangents` into one struct array, upload once; skin streams stay separate (locations 8/9 → separate SRV/CBV).

### `Engine/Rendering/SData/Material.cs`
- **Lines 105–121** `Activate()` → **DELETE**.
- **Lines 123–136** `Deactivate()` → **DELETE**.
- **Line 138** `static Material LastActivatedMaterial;` → **DELETE**.
- All properties (lines 7–58) → **KEEP unchanged**.

### `Engine/Rendering/Renderer.cs`
- **Lines 107–110** `Activate()` → `MaterialFor(0)?.Activate(); SharedMesh.Activate();` → **DELETE** the method.
- **Lines 112–115** `Deactivate()` → **DELETE** the method.
- `MaterialFor`, `IsRenderable`, `Material` instance logic (lines 29–105) → **KEEP** (precedence rule must be honored by the DX material-table builder).

### `Engine/Rendering/Graphics/GraphicAPI.cs`
- **Lines 19–63** all `Create*(RenderContext)` facade methods (`CreateRenderContext`, `CreateIndexBuffer`, `CreateVertexBuffer3/2/2D`, `CreateUVBuffer`, `CreateNormalBuffer`, `CreateTangentBuffer`, `CreateBoneIndexBuffer`, `CreateBoneWeightBuffer`, `CreateBuffer<T>`, `CreateInstancedBuffer`) → **DELETE** (end state; Mesh uses `CreateMeshBuffers`). Interim: collapse the alias trio (`CreateVertexBuffer2`/`2D`/`CreateUVBuffer`) into one.
- **Lines 7–16** `CreateStandardShader` → **KEEP** (dedup cache by source hash is load-bearing; the DX backend hooks the same cache).
- **Line 17** `Renderer` property → **KEEP**.
- **Lines 65–70** `CreateTexture2D`/`CreateCubemap` → **KEEP**.

### `Engine/Rendering/Renderers/SkyboxRenderer.cs` (the ONLY engine uniform caller)
- **Line 1** `using OpenTK.Graphics.OpenGL4;` and all `GL.*` calls (lines 85, 126–128, 132–134) → **deleted with GL** / **change**: the draw + depth-state become renderer-internal DX12 commands. SkyboxRenderer stops issuing GL directly — it exposes geometry + constants, the `DX12HDRenderer` draws it.
- **Lines 69–70** `CreateRenderContext(); renderContext.Activate();` → **remove**.
- **Lines 75–77** `CreateVertexBuffer3 + Create + SetBufferData` → **change** to a single static vertex buffer (no context).
- **Lines 78–80** `CreateStandardShader(Skybox_Vert.glsl, Skybox_Frag.glsl)` → **change**: the DX12 renderer uses embedded `Skybox.hlsl`; the engine-side `CreateStandardShader` becomes a handle (already a no-op compile in `Dx12StandardShader`). Keep the call only if the cache key is still wanted; otherwise the renderer owns the skybox PSO.
- **Line 84** `cubemapVertexBuffer.Activate()` → **remove**.
- **Lines 106–108** `renderContext.Activate(); cubemapTexture.Activate(); skyboxShader.Activate();` → **remove**.
- **Lines 119–125** `SetMatrix4("rotation"/"view"/"projection")`, `SetFloat("exposure")`, `SetInt("skybox", 11)` → **change**: write a `SkyboxConstants` struct (rotation, view 3×3, projection, exposure×preExposure) the renderer uploads to a CBV; "skybox unit 11" → fixed descriptor slot. **GOTCHA**: the HLSL `cbuffer` layout must match this struct's field order/alignment exactly.

### `Engine/Rendering/Camera/HDCamera.cs`
- **No change** required. Lines 16/76 `RenderAsset.Current.Renderer` and lines 77–78 `BeginRender/PostRenderCleanUp` are backend-agnostic and `DX12HDRenderer` satisfies them.

### Editor render path — `BallisticEngine.Editor/EditorApp/EditorApplication.cs`
- **No change** required for the runtime display contract. Lines 150 (`PresentToScreen=false`), 522/525/526/530/531 (Scene), 560/563/564/565 (Game), 1502/1800 (`ImGui.Image(Tex(SceneColorHandle/GameColorHandle))`), 72 (`Renderer` getter) all go through the abstract `HDRenderer` + `RenderHandle` and are satisfied by DX12.
- **Phase-7 only**: `EditorDebugViews.Install()` (line 138) and the `DebugFrame` raw-GL-id composite — leave GL-coupled; port when the editor-debug composite moves to DX12.

### `Engine/Headless/HeadlessRuntime.cs`
- `NullRenderContext`/`NullBuffer`/`NullInstancedBuffer`/`NullRenderAsset` (lines 70–129) → **change** in lockstep: drop `Activate/Deactivate/Create`, add dummy `GpuAddress=0`/`Stride`/`ElementCount=0` accessors, drop `InstancedDrawing`. Implement `CreateMeshBuffers` returning empty/no-op buffers.

### `Engine/Bootstrap/EngineBootstrap.cs`
- **Line 128** `runtime.RenderAsset.Initialize()` → **no change**. Confirm the host wires `DirectXRenderAsset` via `RenderBackendSelector.Selected == Dx12` (the seam exists; verify the host actually constructs `DirectXRenderAsset` for Dx12 — currently only `RenderBackendSelector` exists, the host must branch on it).

### Abstraction moves (build-breakers if missed)
- `OpenGL/Rendering/Buffers/InstancedBuffer.cs` → **MOVE** to `Abstraction/Rendering/Buffers/InstancedBuffer.cs` (it has no GL dependency; it's misfiled).

### GL-only call-sites — **deleted with GL, no action**
- All of `OpenGL/GLHDRenderer.cs` uniform/source-read sites (1916, 1933–1934, 2238/2295/1864 cutout `GL.BindTexture`, 3809/3850 `material.Activate()`, 2049–2052 material-table, 3890/4426/4435 internal `RenderOpaque/Skybox/Instancing`).
- `Engine/Rendering/SData/Shader/GLStandardShader.cs`, `OpenGL/Rendering/Buffers/GLInstancedBuffer.cs`, `OpenGL/OpenGLRenderAsset.cs`, `OpenGL/Rendering/GpuDriven/*`.

---

## 3. ORDERED EXECUTION PLAN (smallest committable steps)

Each step builds and commits. GL stays the default backend until step 8; DX12 is exercised via `BALLISTIC_BACKEND=dx12` the whole way.

**Step 0 — Layering fix (GL still works).** Move `InstancedBuffer` base from `OpenGL/` to `Abstraction/Rendering/Buffers/`. Build both backends. Commit: "DX12 prep: move InstancedBuffer base into Abstraction (GL-free layering)."

**Step 1 — Promote GPU-address accessors to the base (GL still works).** Add `GpuAddress/ElementCount/Stride/ByteSize` to `GPUBuffer<T>` and `InstancedBuffer` base as virtual (GL returns 0/throws — never called by GL path). `Dx12Buffer`/`Dx12InstancedBuffer` override. Removes the `Dx12Buffer<T>` casts in the renderer. Commit.

**Step 2 — Drop the dead `InstancedDrawing` property (GL still works).** Remove from base + all 4 impls (no engine reads it). Commit.

**Step 3 — Host backend selection wired.** Make the host (`BEngineEntry`/`EngineBootstrap`) construct `DirectXRenderAsset` when `RenderBackendSelector.Selected == Dx12`, else `OpenGLRenderAsset`. Now `BALLISTIC_BACKEND=dx12` actually brings up DX12. GL is still default. Commit.

**Step 4 — Material becomes a property bag (GL TEMPORARILY BREAKS).** Delete `Material.Activate/Deactivate` + `LastActivatedMaterial`, `Renderer.Activate/Deactivate`, `Mesh.Activate/Deactivate`, `Texture.Activate/Deactivate`, `Shader.Activate/Deactivate`, `GPUBuffer.Activate/Deactivate`, `RenderContext.Activate/Deactivate`. This **breaks GLHDRenderer compile** (it calls `material.Activate()` at 3809/3850, etc.). Two sub-options:
  - **4a (recommended)**: gate GL out of the build now — drop `OpenGL/` and `GLStandardShader.cs` from the engine `.csproj` compile set and switch default backend to Dx12. GL is effectively retired here.
  - **4b**: keep GL compiling by inlining its activate logic into `GLHDRenderer` private helpers (throwaway work). Skip unless you want a working GL fallback during the transition.
  Take 4a. Commit: "DX-native: Material/Texture/Mesh become bind-free property bags; retire GL from build."

**Step 5 — Interleave the mesh vertex buffer.** Add `RenderAsset.CreateMeshBuffers(in MeshData)`; rewrite `Mesh.ctor`/`FillBuffers` to build one interleaved `GPUBuffer<Vertex>` (pos/normal/uv/tangent) + index buffer + skin buffer. Update `Dx12*` to consume it; update the DX12 input layout. Delete the per-stream `Create*` factories from `RenderAsset`/`GraphicAPI`/`DirectXRenderAsset`/`NullRenderAsset`. Commit.

**Step 6 — Remove RenderContext entirely.** Delete `RenderContext.cs`, `Dx12RenderContext.cs` (fold device-carry into `Dx12Backend.Device`), `Mesh.renderContext`, the `RenderContext` ctor param on `GPUBuffer<T>`/`InstancedBuffer`. Commit.

**Step 7 — Migrate SkyboxRenderer to constants + renderer-owned draw.** Replace the `SetMatrix4/SetFloat/SetInt` + GL state with a `SkyboxConstants` struct the `DX12HDRenderer` uploads; renderer owns the skybox PSO/draw + depth state. Delete the per-name uniform API from `Shader`/`StandardShader` and the 8 no-op overrides in `Dx12StandardShader`. Commit.

**Step 8 — Material table + descriptor binding in DX12HDRenderer.** Port `GpuMaterialTable.Pack()` semantics (6 SRVs + scalar factor buffer, null→Neutral, precedence from `MaterialFor`) into the DX12 renderer's per-frame descriptor table + material structured buffer. Wire opaque pass to index by per-draw material id. Commit.

**Step 9 — SunTemple renders.** Bring the DX12 opaque pipeline up to a lit SunTemple: depth prepass + opaque + skybox. Verify with `BALLISTIC_BACKEND=dx12 BALLISTIC_SCREENSHOT_PAUSED=1` against a SunTemple scene, eyeball + `[PerfStats]`. Commit: "DX12: SunTemple opaque + sky rendering."

**Step 10 — Delete GL for good.** Remove `OpenGL/` project/folder, `OpenGLRenderAsset`, `GLStandardShader`, `RenderBackend` enum/selector (DX12 is the only backend), `BALLISTIC_GPUDRIVEN*` GL env flags, OpenTK GL package refs. Keep `OpenTK.Mathematics` (math lib). Commit: "Delete OpenGL backend."

**GL breaks at step 4, is retired from the build at 4a, and is deleted at step 10.**

---

## 4. RISKS

**Build-breakers**
- `InstancedBuffer` in `OpenGL/` (step 0) — if GL is deleted before this moves, `RenderAsset.CreateInstancedBuffer` and `Mesh.InstanceBuffer` lose their base type and the whole engine fails to compile. **Do step 0 first.**
- Deleting `Activate/Deactivate` (step 4) cascades: `GLHDRenderer` won't compile. Don't attempt a "keep both backends" build past step 4 unless you take 4b. The clean path is 4a (retire GL from compile immediately).
- `NullRenderAsset`/`NullRenderContext`/`NullBuffer` in `HeadlessRuntime.cs` must change in lockstep with every abstract-signature change, or the headless harness (`bal simulate`/`bal render`, `HeadlessRuntime`) breaks — and that's the agent verification surface.

**Editor risks**
- The display contract (`SceneColorHandle`/`GameColorHandle` → `RenderHandle` → `ImGui.Image`) is already backend-agnostic — low risk. But the **editor-debug composite** (`DebugFrame` with raw GL `int` ids, `EditorDebugViews.Install`) is GL-coupled by design (Phase 7). Until ported, the editor's Normals/Depth/AO/SSGI debug views and gizmo depth-occlusion (`ReadSceneDepthGrid`) will be broken under DX12. Acceptable per the migration plan, but it *will* look like a regression — flag it.
- Editor must explicitly resize both offscreen targets (`ResizeSceneTarget/ResizeGameTarget`); DX12 RT recreation must tolerate per-panel resizes without flicker (mirror the GL "only resize when size changed" rule, or the viewport flickers).

**SkyboxRenderer (highest-detail risk)**
- It is the **only** engine-layer uniform caller, and it sets matrices that feed a hand-written shader. The HLSL `cbuffer` for `SkyboxConstants` must match the C# struct's field order, alignment, and matrix major-ness exactly — **misalignment silently corrupts the sky geometry** (no error, just garbage). The view matrix is intentionally the 3×3 rotation lifted into a Matrix4 (lines 121–123) — preserve that. `ProjectionOverride` (TAA jitter) and `PreExposure` must still apply, or the sky jitters differently from geometry at silhouettes (TAA artifact) and the fp16 buffer over/underexposes.
- "skybox unit 11" (`SetInt("skybox", 11)`) → a fixed descriptor slot; if the table layout disagrees with the HLSL register, the sky samples the wrong texture.

**Anything that assumed VAO / unit binding**
- `Material.LastActivatedMaterial` skip-redundant-bind optimization vanishes — the DX path must not regress perf without it; the per-frame material table (built once, indexed per draw) replaces it and is strictly better.
- **Cutout in prepass/shadow** bypassed `Material.Activate` and bound `Diffuse.UID` directly via `GL.BindTexture` (1864/2238/2295). The DX12 prepass shader must still reach the cutout material's diffuse SRV through the per-draw material id — easy to forget since it never went through the normal material path. If missed, alpha-tested foliage/fences render as solid quads in depth (broken silhouettes + shadows).
- Skinned meshes create bone buffers conditionally (locations 8/9). In the interleaved model these become a separate skin SRV/CBV; the input layout / root signature must branch on `IsSkinned`, or skinned draws read garbage bone data.
- `GpuMaterialTable.Pack()` folds global debug multipliers (metallic/roughness/normal) at table-build time → changing a debug slider forces a table rebuild. The DX port must keep that rebuild trigger or the editor sliders appear dead.

**Files this plan touches (engine layer):** `Abstraction/Rendering/RenderAsset.cs`, `Abstraction/Rendering/Buffers/{GPUBuffer,RenderContext,InstancedBuffer}.cs` (InstancedBuffer moved in), `Engine/Rendering/SData/{Mesh,Material,Renderer}.cs`, `Engine/Rendering/SData/Shader/{Shader,StandardShader}.cs`, `Engine/Rendering/SData/Texture/{Texture,Texture2D}.cs`, `Engine/Rendering/Graphics/GraphicAPI.cs`, `Engine/Rendering/Renderers/SkyboxRenderer.cs`, `Engine/Headless/HeadlessRuntime.cs`, `Abstraction/Rendering/Renderer/HDRenderer.cs`. **DX12 backend:** all of `BallisticEngine.DX12/` (already scaffolded). **Deleted:** `OpenGL/` (entire), `Engine/Rendering/SData/Shader/GLStandardShader.cs`.