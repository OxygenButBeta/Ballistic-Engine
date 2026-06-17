# Ballistic Engine

Custom C#/.NET 9 game engine — **NOT a Unity project** despite the folder location. Stack:
**DX12 + DXR renderer** (Vortice bindings, `BallisticEngine.DX12/`; the live backend — Windows/PC
only, hardware ray tracing). The OpenGL backend (`OpenGL/`) was **DELETED in the 2026-06 DX12
migration** (C3) — it no longer exists; anything below that still says GL/GLSL is HISTORICAL (see the
Renderer-pipeline section banner). OpenTK 4.9.4 is still referenced but now ONLY for `OpenTK.Mathematics`
(incrementally being migrated to System.Numerics) + the OpenAL audio bindings — NOT for GL. AssimpNet +
StbImageSharp + Magick.NET (import-time only), YamlDotNet (scenes), ImGui.NET (editor, on a DX12 backend).
Idioms deliberately mirror Unity (Entity/Behaviour, AssetDatabase, meta files, edit/play split).

## Build & run

```
dotnet build BallisticEngine.slnx          # 5 projects; the old .sln is gone
dotnet run --project BallisticEngine.Runtime [projectPath]   # standalone player
dotnet run --project BallisticEngine.Editor  [projectPath]   # ImGui editor
```

Default project path: `<repo>\SampleProject`. Projects:
- `BallisticEngine.csproj` (root) — engine **library**; globs all engine folders, `<Compile Remove>`s the exe subfolders.
- `BallisticEngine.Runtime/` — thin player exe (Program + BEngineEntry over `EngineBootstrap`/`EngineLoop`).
- `BallisticEngine.Editor/` — ImGui editor exe (ImGuiBackend/, EditorApp/, Panels/, EditorCamera/, Gizmo/, Remote/).
- `BallisticEngine.Cli/` — `bal`, the headless agent CLI (see below).
- `BallisticEngine.Mcp/` — stdio MCP server bridging to the editor's command port.

## Agent surface (AI-operability layer, 2026-06)

The engine is fully operable headlessly — **prefer these over hand-editing YAML or eyeballing
screenshots** (each verb prints JSON, honest exit codes; `bal --help` lists all):

- `bal map <project>` — orient first: scenes, script components, asset inventory.
- `bal schema [--type X]` — component catalog from reflection (engine + game scripts); never guess members.
- `bal scene get/set/add-entity/add-component/remove-*/find` — typed scene CRUD; one-member edit = one-line diff; ids tool-minted; refs path-form.
- `bal validate` / `bal describe` / `bal import` / `bal assets resolve|refs|list` — checks, summaries, idempotent import, reverse-ref map.
- `bal simulate <scene> --steps N --watch Entity[:Comp.Member] [--snapshot Entity] [--input script.json]` — REAL engine headless (HeadlessRuntime: scripts+physics play, no GL); numeric time series; `--snapshot` = full live component state at the final step (introspection); deterministic scripted input (two runs byte-identical).
- `bal render <scene> [--orbit N] [--idmap]` + `bal imgdiff a b [--out heatmap]` — deterministic captures, multi-view, perceptual diff (mean + 32x32-hotspot budgets).
- `bal query <op> <scene> --points/--pairs` — SPATIAL PERCEPTION (GpuSceneQuery: inline DXR RayQuery over the scene TLAS, headless, deterministic): `occupancy` (inside solid?), `classify` (open/enclosed/solid), `nudge` (occupied→free space), `rooms` (visibility clusters), `visibility` (clear line of sight per A>B pair). The agent asks the 3D world instead of guessing from pixels (`BallisticEngine.DX12/Query/`, proposal `Docs/Plans/gpu-scene-query-api-proposal.md`).
- `bal gbuffer <scene> [--out dir]` — raw G-buffer dump (depth.bin R32F / normal.bin RGBA16F packed N*0.5+0.5 / albedo.bin RGBA8 + manifest.json) so the agent reads geometry directly, not the tonemapped pixel.
- `bal perf <scene>` — render-perf stats JSON (draws/tris/cull/lights/cpuFrameMs; per-pass GPU ms is a DX12 follow-up).
- `bal agents <project>` — regenerates the project's AGENTS.md (never stale: built from reflection + .meta).

Env harness additions: `BALLISTIC_SCENE` (player loads any project-relative scene),
`BALLISTIC_DETERMINISTIC=1` (TAA/SSGI/volumetric off + fixed exposure → frame 60 == frame 240
byte-identical), `BALLISTIC_IDMAP=<path>` (entity-ID map: `<path>.json` per-entity/submesh
screen bboxes + `<path>.bmp` segmentation — occlusion-aware "what is on screen where"),
`BALLISTIC_SCREENSHOT_EXIT=0`. Every screenshot writes a `.stats.json` sidecar (draws/tris/
cull/per-pass GPU ms). Logs mirror to `<project>/Library/Logs/engine.jsonl`.

Live editor control: named pipe `\\.\pipe\BallisticEditor` (newline JSON `{id,method,params}` →
`{id,result|error}`; methods in `BallisticEngine.Editor/Remote/RemoteHandlers.cs`) or the MCP
server (16 tools). Remote mutations push EditorUndo first and mark the viewport dirty — they
behave exactly like human edits. The pipe thread is engine-owned: survives script hot-reload.

## Layering rules (auditable by grep)

| Layer | May use | Must NOT use |
|---|---|---|
| `Shared/`, `ToolKit/` | BCL only | everything else |
| `Abstraction/` | Shared, OpenTK.Mathematics | GL calls, file formats |
| `Engine/` | Abstraction, Shared | Assimp/Stb/Magick, asset file I/O |
| `OpenGL/` | Abstraction, Engine types, GL | Assimp/Stb/Magick |
| `Physics/` | Abstraction, BepuPhysics | GL, Assimp/Stb/Magick, Engine internals |
| `AssetPipeline/` | everything + Assimp/Stb/Magick/STJ | GL calls (GPU upload goes through `RenderAsset`) |

`AssetPipeline/` is the ONLY place allowed to reference AssimpNet/StbImageSharp/Magick.NET;
`Physics/` is the ONLY place allowed to reference BepuPhysics (engine components talk through
`Abstraction/Physics/IPhysicsWorld`, injected in `EngineBootstrap` — same pattern as the renderer).
CPU data types (`MeshData`, `TextureData`) live in `Abstraction/Rendering/Data/`.

`Engine/Jobs/JobSystem.cs` is the ONLY file allowed to reference the ZeroAllocJobScheduler
package (`Schedulers` namespace) — everything else uses the engine-owned `IJob`/
`IJobParallelFor`/`JobHandle`/`JobSystem` facade. Frame-scoped CPU work only (no GL calls
from jobs; not for background I/O like asset imports, not for audio). The scheduler's
workers are FOREGROUND threads: host Mains call `JobSystem.Shutdown()` after their window
loop returns or the process never exits.

## Asset system (Unity-style)

- Project dir: `project.json` + `Assets/` (sources + `.meta` sidecars with GUIDs) + `Library/`
  (gitignored: binary artifacts `.bmesh`/`.btex`, `ArtifactDB.json`, `Thumbnails/`).
- `AssetDatabase.Initialize/Refresh/Load<T>("Assets/...")` — GUID-cached instances, never
  throws (logs + returns null; materials substitute fallback textures).
- Asset refs in files: `"Assets/...path"` or `"guid:<32hex>"` (`AssetRef`).
- Native text assets read directly: `.mat`, `.shader`, `.cubemap`, `.volume` (JSON), `.glsl`.
- Loading any image asset AS `Texture3D` builds an equirect cubemap (skybox from .hdr/.exr).
- `ModelImporter` (meshIndex -1) merges the whole model with one submesh per source material
  and generates a sibling `<Model>_Materials/` folder of `.mat` assets (rewritten on reimport).
  - **Texture auto-bind by filename convention** (v7, `TextureConventionMatcher`): when a source
    material references NO textures of its own (Quixel Megascans / textures.com / Substance ship an
    empty FBX material + sibling `<stem>_4K_Albedo.jpg` etc.), maps are matched by suffix
    (Albedo/Basecolor/Diffuse→Diffuse, Normal, Roughness [preferred over Gloss — no invert path],
    Metalness→Metallic, AO, Emissive; Opacity/Mask→Cutout). Fallback ONLY — authored refs (glTF) win.
  - **FBX unit conversion** (v8, `FbxUnitScaleFactor`): FBX's system unit is cm. cm-authored content
    imported 100x too big; the importer reads `UnitScaleFactor` straight from the FBX (AssimpNet 4.1.0
    has no scene metadata) and bakes a cm→m scale into the root node's local transform (vertices AND
    the split-by-nodes hierarchy). `scaleFactor` setting: 0 = auto from file units, >0 = forced.
    glTF/OBJ/DAE are metric → factor 1 (byte-identical to pre-v8).
- `.pyscene` (Falcor) imports regex-parse camera/lights/models/envmap → sibling `.scene`.
- **Unity package import** (editor: Assets > Import Unity Package): pick a `.unitypackage` (gzip tar:
  `<guid>/{asset,asset.meta,pathname}`) or an unpacked Unity `Assets/` folder. `UnityPackageReader`
  extracts the path tree; `UnityYamlParser` parses `.unity`/`.prefab` (the `--- !u!<classID> &<fileID>`
  format — GameObject/Transform/MeshFilter/MeshRenderer); `UnitySceneConverter` emits a Ballistic
  `.scene` (transform hierarchy + StaticMeshRenderers, **LH→RH coord fix: mirror X**), resolving Unity
  `{guid}` refs via `UnityMetaGuidMap` (Unity .meta guid → on-disk file). This is the path to an
  ASSEMBLED layout: bare prop-pack FBX has no scene data, but a Unity package carries the dressed
  prefab/scene. (`AssetPipeline/Unity/`, `Engine/Serialization/Unity/`, editor `UnityImportWindow`.)

## Game scripting (C#)

- Game code = `.cs` files anywhere under the project's `Assets\` (Unity-style). The engine
  compiles them via `dotnet build` into `Library\ScriptAssemblies\GameScripts.dll` and loads
  them in a **collectible AssemblyLoadContext** (`AssetPipeline/Scripting/GameScripts.cs`) at
  bootstrap, before `ComponentRegistry.Build` — script Behaviours deserialize from scenes and
  appear in the editor's Add Component menu with zero wiring.
- `Scripts.csproj` is **generated at the project ROOT** (so IDEs open game code as a real
  project with engine refs) and stays engine-managed while its "Generated by Ballistic Engine"
  marker comment is present — DELETE the marker to take ownership (NuGet refs / settings);
  the engine then builds it as-is. `obj\` lands at the project root (gitignored); do NOT set
  `BaseIntermediateOutputPath` in the csproj body (MSB3539 — too late for NuGet).
- Editor hot reload: **automatic on window-focus regain** (edit in the IDE, alt-tab back, it
  compiles) plus manual **Ctrl+R** / File > Rebuild Scripts. Compile-FIRST: on errors nothing
  changes (errors in Console as `Assets/...(line,col): error CSxxxx`); on success the scene
  snapshots to YAML → Clear → unload ALC → load new dll → registry rebuild → re-deserialize.
  `ReloadGameScripts` fast-paths to a no-op when sources are unchanged.
- **LIVE reload during play ("unlike Unity")**: reload does NOT stop play — the LIVE scene
  (play-mode spawns + mutated values) round-trips through YAML and lifecycle restarts on the
  new types via FireBegin; runtime-only state (non-serialized fields, velocities) restarts.
  The pre-play snapshot is untouched, so Stop still returns to the edit scene. Mechanics:
  `SceneManager.SuppressPlayLifecycle` (set by SceneSerializer.Deserialize) keeps Attach from
  firing OnBegin before member values are applied — the reload calls FireBegin itself.
- **Script exceptions never crash the engine** (`Engine/Debug/ScriptGuard.cs`): every lifecycle
  dispatch site (Tick/FixedTick/OnBegin/OnEnabled/OnAttach/OnDetach/gizmos/contacts) catches
  per component, logs with `Assets/...cs:line` stacks (portable PDB is loaded with the dll).
  Per-frame callbacks that throw 3× CONSECUTIVELY auto-disable the component — the streak is
  per-callback (`FaultCallback`): a healthy FixedTick must not reset a throwing Tick's streak.
  Spawn/destroy/add/remove from inside Tick is legal (snapshot iteration + `Entity.IsDestroyed`
  skips mid-frame corpses).
- **Compile errors block running, not the editor** (Unity's playmode lock): `GameScripts.
  CompileFailed` → `SceneManager.PlayBlocked` (wired in bootstrap) disables the editor Play
  button (reason in tooltip) and refuses StartPlay; the standalone player exits code 1;
  BuildPipeline already fails the build.
- Asset browser: right-click > New Script creates a template Behaviour and auto-compiles it
  (rebuild runs BEFORE the async refresh — RebuildScripts no-ops while an import is busy);
  renaming a still-pristine template rewrites its class name to match (`ScriptTemplates`);
  double-click / Edit Script opens Scripts.csproj FIRST (project context) then the file.
  Deleting a script does NOT auto-rebuild — the stale type stays loaded until the next Ctrl+R.
- Dragging a `.cs` tile onto a Hierarchy entity row adds its component; onto empty Hierarchy
  space creates an entity with it. Mapping is Unity's rule: file name == class name
  (`HierarchyPanel.ScriptComponentType`); mismatch or not-yet-compiled logs a Ctrl+R hint.
- Focus regain also refreshes the asset DB when files changed EXTERNALLY (IDE rename, Explorer
  copy): `AssetChangeWatch` fingerprints Assets\ (paths+mtimes, re-snapshotted after every
  AsyncAssetImport refresh) so unchanged alt-tabs don't flash the busy overlay. Caveat: renaming
  a file OUTSIDE the editor strands its .meta (new GUID, references break) — same as Unity;
  rename inside the asset browser to keep the GUID.
- **Gotchas:** lifecycle overrides (`Tick`, `OnBegin`, ...) MUST be `protected` (not
  `protected internal`) in game assemblies — C# cross-assembly override rule. The dll is
  byte-loaded so the file never locks. Engine/OpenTK assemblies resolve from the DEFAULT
  load context — never load copies into the script ALC or type identity splits. The root
  `BallisticEngine.csproj` globs `**/*.cs`: game-project folders (SampleProject) must stay
  in its `<Compile Remove>` list or game scripts compile into the engine itself.

## Scenes & components

- `.scene` = YAML: `sceneComponents:` (scene-wide `SceneBehaviour`s) + `entities:` with
  components reflected via `ComponentReflection` (public props AND fields; asset members
  serialize as guid refs). `ProjectManifest.StartupScene` loads at launch.
- **Edit/Play split:** `SceneManager.IsPlaying/StartPlay/StopPlay`. In edit mode
  `AddComponent` skips `OnBegin`/`OnEnabled`; StopPlay restores a YAML snapshot.
- **`OnAttach`/`OnDetach` fire in BOTH modes** — render registration must live there,
  never in `OnEnabled` (play-only), or the editor viewport goes black.
- `SceneBehaviour` = scene-wide component (Skybox, SceneLighting): lives on the Scene,
  has its own registry (`ComponentRegistry.SceneMenu`) and the editor's "Scene" hierarchy tab.
  Pattern: `static Active` set in OnAttach/OnDetach; the renderer reads it per frame.
- **Post-processing = Unity-style volume framework** (`Engine/Rendering/Volumes/`): `Volume`
  (entity Behaviour, global or box-local w/ priority/weight/blend) → `VolumeProfile` (`.volume`
  JSON asset) → `VolumeComponent`s of `VolumeParameter` fields (override flag + clamped ranges,
  discovered by reflection — new components need zero wiring beyond `VolumePostProcessing.Apply`,
  the one stack→`PostFX` bridge). `VolumeManager.Update(cameraPos)` blends per frame; stack
  defaults MUST mirror `PostProcessSettings` defaults (no-volume scene = engine defaults).
  Editor: profile edits write straight to the `.volume` asset (no scene undo), inline under the
  Volume component and in the `.volume` asset view (`VolumeProfileEditor`).
- `Input.Enabled` is the master gate — the editor disables engine input outside
  play-with-Game-view-focused, so component debug keys don't leak into editing.
- **Editor inspector = ONE attribute-driven drawer pipeline** (`BallisticEngine.Editor/Panels/Inspector/`):
  component members AND volume parameters both render through a shared `DrawerRegistry` + `IInspectorGui` +
  decorator chain (this replaced the two old hardcoded type-switches in `InspectorPanel.DrawMember` /
  `VolumeProfileEditor.DrawParameter`, which used to drift). **When designing ANY inspector/editor window,
  author with the attributes — do NOT hand-roll widgets or a new type-switch.** Layout/semantics:
  `[Header]/[Space]/[Tooltip]/[FoldoutGroup]/[Range]/[ColorUsage]/[ReadOnly]/[LabelText]/[PropertyOrder]`;
  conditionals `[ShowIf]/[HideIf]/[EnableIf]/[DisableIf]` (name a sibling member, optional `==` value;
  VolumeParameter siblings auto-unwrap to `.Value`) — e.g. hide a dial unless a mode enum matches. A NEW
  value type = register one `ITypeDrawer` (works in BOTH paths at once); a NEW cross-cutting behaviour = an
  `IPropertyDecorator`. Attributes live in the engine assembly (`Engine/Attributes/`, plain
  `System.Attribute`, zero ImGui); only the editor interprets them. Defaults are byte-identical, so adding
  an attribute is always opt-in.

## Physics (BepuPhysics 2 behind `IPhysicsWorld`)

- Components in `Engine/Physics/`: `Rigidbody` + `BoxCollider`/`SphereCollider`/
  `CapsuleCollider`/`MeshCollider` (entity menu "Physics"), plus the static `Physics` facade
  (`Raycast`, `Gravity`, `FixedTimestep`). Colliders WITHOUT a Rigidbody become standalone
  static bodies (level geometry); colliders on the SAME entity as a Rigidbody compound into
  its body. Child entities do not contribute (v1).
- **Play mode only**, fixed 60 Hz: `SceneManager.Update` → `Physics.Advance` accumulates,
  fires `FixedTick` on behaviours before each step, steps the world, writes poses back.
  Bodies are created in `OnEnabled` / destroyed in `OnDisabled`+`OnDetach` (NOT OnAttach —
  there's nothing to register in edit mode). `StartPlay`/`StopPlay` reset the world wholesale.
- A simulating dynamic body owns its transform, BUT external transform writes between fixed
  steps (gizmo drag, inspector, scripts) are detected by pose diff and TELEPORT the body,
  Unity-style (`Rigidbody.syncedPosition`; physical motion = `Velocity`/`AddForce`). A
  kinematic body is the reverse: it chases the transform with computed velocities so it
  pushes dynamic bodies (never pose-teleport kinematics). Don't parent a dynamic rigidbody
  under a moving transform — the pose diff degenerates into teleport-following.
- Editor: selected colliders draw green wireframes (`MeshCollider` draws the actual collision
  mesh, edge-budgeted to ~4000 lines so Bistro-scale meshes thin out instead of dying);
  `ColliderHandles` (Editor/Gizmo/) adds Unity-style drag-to-resize squares — box face drags
  move that face (Size+Center together), sphere/capsule drags resize symmetrically. Handle
  math is in COLLIDER-LOCAL units (world scale divided out) so values match the inspector;
  hover is mutually exclusive with the transform gizmo via the two `IsInteracting` flags.
- `MeshCollider` is STATIC-ONLY (concave soup; a Rigidbody logs + ignores it). With no
  mesh assigned it uses the entity's `StaticMeshRenderer`, honoring `SubMeshIndex` +
  inverse node transform, so split-by-nodes children collide with just their own part.
- **Mesh collision is ONE-SIDED, solid from the rendered front face** (backfaces pass
  through, like Unity). Bepu's solid side is OPPOSITE the right-handed winding normal, so
  the backend swaps two indices per triangle (`BepuPhysicsWorld.AddMesh`). Do NOT emit both
  windings to fake double-sided: a fast impact penetrates slightly and the flipped triangle
  ejects the body out the back.
- Dynamic/kinematic bodies use `ContinuousDetection.Continuous(1e-3, 1e-3)` + 0.1 max
  speculative margin — speculative contacts alone tunnel through thin meshes above ~6 m/s
  at 60 Hz; the sweep is auto-skipped while slow, so resting bodies pay nothing.
- Primitive colliders AUTO-FIT on add (Unity parity): `Collider.OnAttach` →
  `AutoFitToRenderMesh()` sizes Box/Sphere/Capsule to the render mesh's local bounds —
  but only while the shape still has pristine constructor defaults (deserialization applies
  saved members AFTER OnAttach, so saved/user values always win).
- **Contact events**: `OnCollisionEnter/Stay/Exit(Collision)` + `OnTriggerEnter/Stay/Exit(Collider)`
  on `Behaviour`, fired after each fixed step on every enabled behaviour of BOTH entities
  (exceptions per-behaviour caught + logged). `Collider.IsTrigger` = overlap detection without
  physical response (per-BODY in v1: mixed trigger/solid colliders on one Rigidbody warn + stay
  solid; trigger pairs also match kinematic-vs-static). Bepu has no event system — contacts are
  only visible in the narrowphase callback on WORKER threads: `BepuContactTracker` records
  per-worker (lock-free), then diffs pairs single-threaded after `Timestep`. Touch = depth ≥
  −5mm (absorbs resting jitter). Sleeping pairs go DORMANT (no Stay/Exit; Stay resumes on wake,
  Unity-style); removing a touching body fires a deferred Exit. Don't use the JobSystem here —
  the parallel half already runs on Bepu's dispatcher, the serial half calls user code.
- World scale is baked into shapes at body creation; scale changes during play don't resize.
- `[NotSerialized]` excludes a public r/w property from scene YAML AND the inspector —
  for runtime-only state like `Rigidbody.Velocity` (opposite of `[HideInInspector]`).
- Headless smoke test pattern: a scratch console project can compile
  `Abstraction/Physics/*.cs` + `Physics/Bepu/*.cs` directly (stub `Debugging`) and simulate
  without any window/GL — 37-check suite lives in `%TEMP%\bal-phys-test`.

## Profiling

- **In-editor realtime profiler**: Window > Profiler (or launch with `BALLISTIC_PROFILER=1` to
  auto-open — agents can screenshot it). Frame-time graph + per-zone breakdown from
  `EditorProfilerBackend` (editor-only ring buffer of the last 240 frames, chains to Tracy when
  both are active; Pause freezes history and unlocks frame scrubbing).
- CPU profiler = **Tracy 0.11.1**, opt-in: set `BALLISTIC_TRACY=1`, run either exe, then attach
  `Tools\Tracy\tracy-profiler.exe` (live GUI) or capture headlessly (agent-friendly):
  `Tools\Tracy\tracy-capture.exe -o out.tracy -s 10 -f` then `Tools\Tracy\tracy-csvexport.exe out.tracy`
  (per-zone stats as CSV). `Tools/Tracy/` is gitignored — re-download release v0.11.1; the viewer
  version MUST match the Tracy-CSharp NuGet protocol or it refuses to connect.
- Instrument through the BCL-only facade `Shared/Profiling/Profiler.cs`:
  `using (Profiler.Zone("Name")) { ... }`, `Profiler.FrameMark()`, `Profiler.Plot()`. Every layer
  may call it (no-op without a backend). The Tracy backend is its own project
  (`BallisticEngine.Profiling.Tracy/`, referenced by the exes only) so the engine library never
  takes the native dependency; root csproj `Compile Remove`s the folder like the other exe dirs.
- The opt-in env var matters: without `TRACY_ON_DEMAND` the Tracy client buffers ALL events in RAM
  until a viewer connects — never enable unconditionally in long editor sessions.
- Zones are CPU-side submit cost, not GPU time (GL is async). GPU frame analysis = RenderDoc
  (installed system-wide; in-app capture API via renderdoc.dll is a future hookup).
- No-code-change fallback: `dotnet-trace collect --process-id <pid> -f speedscope` (sampling,
  no frame markers) — view at speedscope.app.

## Renderer pipeline (2026-06 overhaul — invariants that must not regress)

> ⚠ **HISTORICAL (GL-era) from here down.** The OpenGL backend (`OpenGL/`) described below was DELETED
> in the DX12 migration (C3) — these `OpenGL/...` paths, GLSL files, `GL.*`/`glMultiDraw*` calls and OpenTK
> GL types NO LONGER EXIST. The live renderer is **DX12/DXR** (`BallisticEngine.DX12/`, `.hlsl` shaders
> embedded there); it deliberately MIRRORS the frame shape + invariants documented below (z-prepass
> invariance, cull determinism, transient-RT pooling, TAA-is-the-AA, the post chain), so this section is
> kept as the conceptual contract the DX12 passes must still honour — just read every GL/GLSL detail as
> "the DX12 equivalent." DX12-specific notes (pass-graph, Lumen GI, exposure, P0a pipelined frame) live in
> the `Docs/Plans/dx12-*` plans + the agent-memory topic files.

- **GPU-driven path (MDI + compute cull + bindless, 2026-06, `OpenGL/Rendering/GpuDriven/` — GL-era, deleted; DX12 uses ExecuteIndirect)**: the
  WHOLE-MESH renderer (Bistro, ~1600 submeshes, `SubMeshIndex < 0`, non-skinned, single shader) is
  drawn via `glMultiDrawElementsIndirectCount` after a GPU compute frustum cull, collapsing ~1600
  `DrawElements` into a handful of MDI calls (CPU submit was THE bottleneck: 30ms CPU vs 12ms GPU,
  6070 draws). Per-submesh/instanced/skinned/mixed-shader renderers keep the CPU path.
  - `GpuDrivenRenderer` owns the buffers; `GpuCull_Comp.glsl` does the cull (positive-vertex AABB
    test, world AABBs **pre-transformed on the CPU with the same 8-corner loop** as
    `ComputeSubmeshVisibility` so it's bit-identical to `AabbInFrustum`). `GLPersistentBuffer` =
    GL4.6 persistent-mapped triple-buffered fence-synced streaming. `GpuMaterialTable` = bindless
    handles (`GL_ARB_bindless_texture`) so different materials batch into ONE draw; missing maps
    use `DefaultTextures.Neutral` + the global metallic/roughness/normal multipliers, exactly
    like CPU `SetMaterialUniforms`.
  - `GpuDrivenShaderTransform` INJECTS the per-draw model (`PerDrawData[gl_DrawIDARB]`) + bindless
    material reads into each material's OWN vert+frag GLSL by `#define` — shading is bit-identical
    to the uniform path, so z-prepass invariance holds (prepass + opaque share the deterministic
    cull). Bumps `#version` to 460. Prepass frag MUST reuse `SharedDecls` (cross-stage block names
    must match). The transform must NOT touch shading math.
  - **Two batches**: SOLID (backface cull on) + CUTOUT (cull off — single-sided foliage), separate
    cmd/count/perdraw buffers to avoid the write-after-read hazard. The cull binds the COMPUTE
    program, so re-activate the render program AFTER culls, before the MDI draws.
  - `BALLISTIC_GPUDRIVEN=0` → CPU path (byte-identical fallback). Auto-disables without bindless/
    draw-params. Verified byte-identical (meanError 0) deterministic full-FX, draws 420→3.
  - GPU-driven shadows: DEFAULT ON (`BALLISTIC_GPUDRIVEN_SHADOWS=0` to disable). One MDI per cascade
    after a light-space compute cull — collapses the ~2358 (move-time ~11000) CPU shadow depth draws
    to ~10 when cascade caching invalidates on camera motion. Byte-identical to the CPU shadow path
    (the bit-exact world-AABB cull + program state-leak fix). When the whole-mesh renderer is GPU-
    driven for BOTH camera and shadows, the CPU per-submesh cull (`ComputeSubmeshVisibility`) is
    skipped entirely — the GPU cull replaces it.
  - Hi-Z OCCLUSION CULLING: DEFAULT ON (`BALLISTIC_GPUDRIVEN_HIZ=0` to disable). `GLHiZPass` builds
    a MAX-depth mip pyramid (`HiZ_Down.glsl`) from the PREVIOUS frame's depth; the cull
    (`occludedByHiZ`) drops submeshes whose whole AABB is behind a closer occluder, comparing in
    LINEAR view distance (window depth bunches near the far plane — direct compare over-culls) with a
    0.25 m bias. A camera-delta gate disables it one frame after a big jump (stale-depth hole safety);
    shadows never use it. Byte-identical (0% pixel diff): Sun Temple 1000→473 draws, Bistro ~814→719.
    GOTCHA: the pyramid build MUST detach its color attachment + restore unit-0 binding + re-enable
    DepthTest/CullFace, or it corrupts the later passes (sky/SSGI/SSR) even when nothing is culled.
  - Gotcha: route any NEW compute-shader compile through `GLSLShaderUtilities.ToAscii` (an em-dash
    in a comment truncates the source → "unexpected end of file"). Whole-mesh model = plain
    `WorldMatrix` for ALL submeshes (NOT inverse-node — that's per-submesh-renderer only).
- **Frame shape (single path, no MSAA)**: cull → cascaded shadows (cached) → z-prepass →
  SSAO → opaque (LEqual, no depth writes) → sky → transparent → SSGI → SSR → volumetric →
  TAA → exposure meter → bloom → composite. TAA **is** the AA; the MSAA path was deleted
  (the AntiAliasing volume's MSAA setting is inert).
- **Z-prepass contract**: `GLSLShaderUtilities` injects `invariant gl_Position;` into EVERY
  vertex shader; the prepass re-renders visible opaques with each material's OWN vertex
  source (depth-only companion programs, `PrepassShaderFor`) so prepass depth is bit-identical
  and the main pass shades each pixel exactly once (DepthFunc LEqual + DepthMask false).
  Breaking invariance = checkerboard holes. Instanced runs MUST draw instanced in BOTH passes.
- **Culling**: `SplitRenderables` computes per-renderer world AABBs (per-SUBMESH local bounds —
  whole-mesh bounds would make split-by-nodes culling useless) and frustum-culls the main view;
  shadow casters cull per cascade / per punctual face against the light frustums. The FULL
  opaque list still feeds shadows and bakes — an off-screen mesh still casts shadows.
- **Cascade caching**: sun cascades re-render only when the texel-snapped fit matrix OR the
  scene-geometry stamp (hash of all caster AABBs) changes. Static camera = all four free.
  Punctual tiles were already stamp-cached.
- **PassData UBO**: ALL pass constants live in one std140 block (`PassData`, binding 0),
  declared TEXTUALLY IDENTICALLY in Vert.glsl + Frag.glsl, filled once per pass by
  `GLUniformBlock` (offsets are QUERIED from the program, never hand-computed). Member names
  match the old uniforms. Samplers can't live in UBOs — units are assigned once per program.
  Shaders without the block fall back to the legacy per-uniform path (`SetPassUniformsLegacy`).
- **Instancing is ON**: the opaque sort (material → mesh → submesh) makes identical
  (mesh, submesh, material) runs adjacent; runs ≥ 2 stream model matrices into the mesh's
  instance buffer (attribs 4-7) and draw `DrawElementsInstanced`. The instanced matrix is
  built WITHOUT transpose in Vert.glsl — it must equal the uniform-path matrix exactly.
- **Transient RT pool**: post passes `GLRenderTexturePool.Shared.Acquire()` per-frame scratch
  (released wholesale in `EndFrame`); ONLY cross-frame history (TAA/SSGI/volumetric
  accumulation) stays pass-owned. Never pool history.
- **SSR marches at half-res**; SSR_Combine upsamples depth-aware. **SSGI gather = horizon
  slices with sector visibility BITMASKS** (SSILVB-style, `SSGI_Frag.glsl`, `#version 460`):
  per slice a 32-bit mask over the hemisphere arc gives ORDERED occlusion (near occluders
  block far light — no scene-average veil by construction), `Thickness` = assumed occluder
  thickness (thin geometry occludes thin sectors), and sky enters only through the visibly
  OPEN sectors. `rayCount` now means slices (clamped to 8). (The GL-era `SsgiSkyFallback`/
  `SsgiDenoise`/`SsgiMultiBounce` dials were dropped in the DX12 GI-volume consolidation — the
  DX12 SSGI shader has no slot for them; OIDN replaced the a-trous denoise.) Temporal/combine
  chain unchanged.
- **Per-pass GPU timers** (`GLGpuTimers`, timestamp queries, non-blocking ring) publish into
  `RenderStats.Scene/Game` with real draw/triangle/cull counters — the editor Stats overlay
  shows them; `Transform` caches Local/World matrices with version stamps (don't bypass the
  setters).

## Headless verification (agents: use this)

- `BALLISTIC_SCREENSHOT=<path.bmp>` — the player saves frame `BALLISTIC_SCREENSHOT_FRAME`
  (default 180) and exits, printing `[PerfStats]` lines (draw counts + per-pass GPU ms).
- `BALLISTIC_SCREENSHOT_PAUSED=1` — load the scene but never StartPlay: no scripts/physics,
  serialized camera → **bit-exact deterministic frames**, diffable across builds. Play-mode
  frames are NOT diffable (sim time at a fixed frame varies run to run).
- `BALLISTIC_FX_SSGI/SSR/VOLUMETRIC/SSAO/SSGI_DEBUG=0|1` — force post-FX toggles after the
  volume stack applies, for A/B screenshot runs.

## Hard-won gotchas (do not relearn these)

- **ImGui backend MUST `GL.ActiveTexture(Texture0)` before binding** — the engine leaves
  high texture units active; otherwise the entire UI samples a scene texture.
- **Never mix raw-HDR and tonemapped samples in post shaders** (e.g. sharpen blur):
  extrapolation around the EXR sun goes negative → `pow` → NaN black holes. Tonemap first.
- Clamp float cubemap texels below fp16 max (~65504) before RGBA16F upload (sun = Inf → NaN).
- `GL.ShaderSource` truncates multibyte UTF-8 (Turkish comments broke shaders) —
  `GLSLShaderUtilities.ToAscii` sanitizes; keep it in the compile path.
- `GLFrameBuffer.Resize` deletes/recreates the texture — only resize when the size actually
  changed or the viewport flickers.
- Editor frame order is UI build → scene render → present, so gizmo drags don't lag a frame.
- Asset tiles select on click RELEASE (drag must not steal the Inspector selection).
- Editor undo = whole-scene YAML snapshots pushed BEFORE each interaction
  (`EditorUndo.Push()`; `ImGui.IsItemActivated()` for widgets).
- Rider locks folders on Windows — `git mv`/renames of open dirs fail; copy + `git rm --cached`.
- FSQ/post/IBL shaders are **embedded resources** (GL-era: `OpenGL/Shader/Embedded/`, deleted; DX12:
  `.hlsl` embedded under `BallisticEngine.DX12/`), not assets. Incremental DX12 builds do NOT re-embed a
  changed `.hlsl` — clean `obj/` + verify the embed (see memory `dx12-shader-edit-build-gotcha`).
- **GLSL NaN scrubs MUST be a component SELECT (ternary), never `mix(v, 0, flag)`** — float
  `mix` is arithmetic (`v*(1-flag) + 0*flag`) and `NaN*0 == NaN`, `Inf*0 == NaN`: proven leak
  on AMD RX 9070 XT (driver test in `%TEMP%\bal-nan-test`). The broken form turned one Inf
  sun/specular pixel into NaN that the SSGI temporal EMA + multi-bounce + a-trous denoise grew
  into a screen-eating black-noise field a STATIC camera could never flush (fast motion =
  disocclusion reject = flush). Same rule applies in every temporal-feedback shader (SSGI x4, TAA).
- **Bepu has no restitution — bounce = SOFT undamped contact springs** (low frequency,
  damping `1-bounciness`, uncapped recovery velocity). Stiffening the spring makes impacts
  MORE inelastic (speculative contacts absorb the approach velocity). Solver runs
  `SolveDescription(2, substepCount: 4)` so those springs resolve at 60 Hz steps.

## Repo facts

- `SampleProject/Assets/Default/Bistro_v5_2/` (1.6 GB test content) is **gitignored**
  pending a git-lfs decision; `Main.scene` references it, so it must exist locally.
- Repo already tracks ~460 MB of binaries; git-lfs migration is an agreed follow-up.
- Known half-finished: skybox shader as C# strings. (RESOLVED since this note: the editor
  shading-mode dropdown now drives real renderer debug views — Shaded/Wireframe/Normals/Depth;
  asset refresh runs async off the render thread with a determinate progress overlay; SSGI's
  temporal pass now has depth-based disocclusion rejection.)
