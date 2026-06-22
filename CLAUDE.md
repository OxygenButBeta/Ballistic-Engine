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
| `BallisticEngine.DX12/` | Abstraction, Engine types, Vortice/DX12 | Assimp/Stb/Magick |
| `Physics/` | Abstraction, BepuPhysics | Assimp/Stb/Magick, Engine internals |
| `AssetPipeline/` | everything + Assimp/Stb/Magick/STJ | GPU upload goes through `RenderAsset` |

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

## Material / shader system (shader-declared property bag + custom surface shaders)

- **Material = property bag, NOT fixed slots.** A `.shader` DECLARES its properties (Unity ShaderLab
  style — `Shader.Properties`, an optional `properties:[...]` block in the `.shader` JSON; the built-in
  Standard declares 20 in `StandardShaderProperties`). A `Material` holds only OVERRIDE values in a
  `MaterialSemantic`-keyed bag. The renderer packs them into the FIXED `DrawConstants`/`GpuMaterial`
  layout via each property's `MaterialSemantic` (the bridge) — so the embedded G-buffer HLSL is
  unchanged and Standard materials render byte-identical. `MaterialLoader.ApplyScalars` is the SOLE
  default authority (the metallic-map-conditional default, PackedOrm/Cutout filename auto-detect); the
  bag is DERIVED from its output (`SyncBagFromTypedFields`, called inside ApplyScalars), never resolves
  defaults itself. The editor material inspector is GENERATED from the declared property list (no
  hardcoded slot UI); `MaterialPropertyBinding` joins each semantic to its `MaterialDefinition` field,
  preserving the `.mat` null-means-default elision.
- **Custom surface shaders (Unity-style).** A `.shader` may add `"surface": "Assets/.../X.surface"` — an
  HLSL file with `SurfaceOutput Surface(SurfaceInput i)` (albedo/normal/metallic/roughness/ao/emissive/
  alpha). The engine OWNS the rest: `SurfaceSkeleton.hlsl` wraps the body (injected at the
  `//USER_SURFACE_MARKER`, before PSMain — HLSL has no forward decl) with the engine's VSMain (z-prepass
  position invariance stays bit-identical), motion, 5-MRT packing, and the b0/t0-t5/b1/s0 ABI. The user
  shader gets a per-material PSO (`Dx12SurfaceShaderCache`, cloned from the Standard PSO state, only the
  PS differs). It CANNOT write a custom vertex stage (VSMain is engine-owned, for prepass invariance).
- **Custom properties (the surface body's own uniforms/textures).** A `.shader`'s `properties:[]` entries
  with `semantic: "None"` are custom — the material sets their values in the `.mat` (`customFloats`/
  `customVectors`/`customTextures`, keyed by property name). The skeleton auto-generates a `cbuffer
  CustomProps : register(b2)` + `Texture2D _X : register(t6..)` from the declared None-props (`Dx12Surface
  ShaderCache.GenerateCustomDecls`), so the body reads them by name. **Straddle-safe layout**: every scalar
  gets its OWN 16-byte cbuffer slot (`float`+`float3` pad), so the C# pack offset is `16*index` and can't
  misalign. The renderer packs `b2` + binds `t6..` per custom draw (`BindCustomProps`, declared order ==
  the codegen order). The root sig gained TRAILING `b2`/`t6` params the Standard shader never reads →
  byte-identical. **GOTCHA — shader-instance identity**: a custom `.shader` reuses the Standard `Vert/
  Frag.glsl`, so `StandardShader.Identity` (was `Combine(vertex, fragment)`) collided with the plain
  Standard shader in `SharedResources` — the loader's `SetProperties`/`SurfaceSource` then leaked onto
  EVERY Standard material. Fixed: `CreateStandardShader(…, identityExtra)` (the `.shader` path) for custom
  shaders only; plain Standard passes null → unchanged key. The editor inspector edits custom props via
  `MaterialPropertyBinding.ForCustom` (name-keyed into the `Custom*` dicts, same null-elision).
- **Compile-fail = magenta checker, never a crash.** A bad surface shader (load or hot-reload) draws the
  `SurfaceFallback.hlsl` black/magenta world-space checker (emissive, visible unlit) + logs the DXC
  error; the failed key caches the fallback so it doesn't recompile every frame.
- **Live hot-reload.** `Dx12SurfaceWatcher` (FileSystemWatcher on Assets\) flags `.surface`/`.hlsl`
  edits; the watcher thread only enqueues. The renderer drains + recompiles in `BeginRender` (main
  thread, BETWEEN frames — PSO creation is main-thread-safe, never mid-draw-list). The old PSO is
  DEFERRED-disposed past `FramesInFlight` (freeing it while the GPU reads it = use-after-free crash). The
  editor renders on-demand, so `HDRenderer.PollSurfaceReload()` → `MarkSceneDirty()` wakes the viewport.
- **Custom-shader materials are DEMOTED to the legacy CPU path** (per-draw `SetPipelineState`, root sig
  unchanged → safe). They CANNOT ride GPU-driven ExecuteIndirect (one PSO per indirect draw) or the CPU
  bindless path (one PSO/frame, swap-forbidden — the TDR rule). The SAME `RendererHasCustomSurface`
  predicate excludes them from the GPU-driven sets AND gates the CPU-loop skips (drawn exactly once);
  `cpuBindless` turns off whole-frame when any custom surface is present. Standard whole-mesh renderers
  stay GPU-driven (a custom-shadered Bistro-scale whole mesh is a perf cliff — opt-in, rare).
- Doors: `BALLISTIC_DX12_SURFACE_SKELETON=1` (compile the skeleton in place of GBuffer.hlsl — byte-id
  proof), `_SURFACE_SELFTEST=1` (compile fallback + ok + broken bodies at init, log results),
  `_SURFACE_HRDEBUG=1` (log watcher init + drained-file counts).

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

## Renderer pipeline (DX12/DXR — invariants that must not regress)

> Live backend = **DX12/DXR** (`BallisticEngine.DX12/`, `.hlsl` embedded there). The GL backend is gone;
> the OLD GL-era detail (GLSL paths, `GL.*`/MDI, GLHiZPass, GLSLShaderUtilities) was removed from this
> doc — it no longer existed. DX12 mirrors the SAME frame shape + invariants below. Deep DX12-specific
> notes (pass-graph, Lumen GI, exposure, P0a pipelined frame, GPU-driven ExecuteIndirect) live in the
> `Docs/Plans/dx12-*` plans + the agent-memory topic files — read those before touching a DX12 pass.

Invariants the DX12 passes still honour (the conceptual contract — names below are the DX12 equivalents):

- **Frame shape (single path, no MSAA, TAA is the AA)**: cull → cascaded shadows (cached) → z-prepass →
  AO (GTAO) → opaque (LEqual, no depth writes) → sky → transparent → **Lumen GI (event 500)** → SSR/RT
  reflections → volumetric/fog → TAA → exposure meter → bloom → composite. Event-sorted pass list (see
  pass-graph plan); the MSAA path was deleted (the AntiAliasing volume's MSAA setting is inert).
- **GPU-driven (ExecuteIndirect)**: the whole-mesh renderer (Bistro, ~1600 submeshes, `SubMeshIndex < 0`,
  non-skinned) is drawn indirect after a GPU compute frustum cull + bindless material table — CPU submit
  was the bottleneck. CPU world-AABBs are bit-identical to the GPU cull (same 8-corner loop). GPU-driven
  shadows + Hi-Z occlusion cull DEFAULT ON. Per-submesh/instanced/skinned/mixed-shader keep the CPU path.
  Material table is stamped by renderer+submesh count → scene swaps must clear it (`RenderSetsCleared`).
- **Z-prepass contract**: every vertex shader is position-invariant so the prepass depth is bit-identical;
  the main opaque pass shades each pixel once (LEqual + depth-write off). Breaking invariance = checkerboard
  holes. Instanced runs MUST draw instanced in BOTH passes.
- **Culling**: per-SUBMESH world AABBs (whole-mesh bounds would kill split-by-nodes culling); frustum-cull
  the main view, cull shadow casters per cascade / punctual face. The FULL opaque list still feeds shadows
  + bakes — an off-screen mesh still casts shadows.
- **Cascade caching**: sun cascades re-render only when the texel-snapped fit matrix OR the caster-AABB
  geometry stamp changes. Static camera = all four free.
- **Transient RT pool**: post passes acquire per-frame scratch (released wholesale at EndFrame); ONLY
  cross-frame history (TAA/volumetric, the Lumen radiance cache) is pass-owned. NEVER pool history.
- **GI = Lumen V2** (`BallisticEngine.DX12/Lumen/`, the legacy SSGI/DDGI/screen-probe/OIDN stack was DELETED).
  HW-RT diffuse GI: per-pixel screen trace → inline-RayQuery TLAS on a screen miss → sky on an RT miss; RT
  hits SAMPLE a per-triangle surface-card radiance cache (`Dx12LumenScene`) that a card-light compute fills
  with lit first-bounce + multi-bounce radiance (the cache is double-buffered for a cache-space temporal EMA —
  no screen-space history). An à-trous spatial denoise cleans the per-pixel indirect. The diffuse indirect is
  added into the HDR color (deferred suppresses its IBL diffuse ambient when Lumen is active → no double-
  count). HW-RT ONLY — no hidden screen-space fallback (no HW RT = GI off). Driven by the `GlobalIllumination`
  VOLUME (default ON); the `BALLISTIC_DX12_LUMEN[...]` env doors override for A/B. Plan: `Docs/Plans/
  lumen-v2-replacement.md`; memory `lumen-v2-replacement-progress`.
- **SSR half-res** (depth-aware upsample); **RT reflections** re-shade hits — when Lumen is on they sample the
  SAME radiance cache the diffuse uses (rough + sharp), IBL only the miss/far fallback.
- **Per-pass GPU timers** publish into `RenderStats.Scene/Game` (real draw/triangle/cull counters; editor
  Stats overlay). `Transform` caches Local/World with version stamps — don't bypass the setters.

## Headless verification (agents: use this)

- `BALLISTIC_SCREENSHOT=<path.bmp>` — the player saves frame `BALLISTIC_SCREENSHOT_FRAME`
  (default 180) and exits, printing `[PerfStats]` lines (draw counts + per-pass GPU ms).
- `BALLISTIC_SCREENSHOT_PAUSED=1` — load the scene but never StartPlay: no scripts/physics,
  serialized camera → **bit-exact deterministic frames**, diffable across builds. Play-mode
  frames are NOT diffable (sim time at a fixed frame varies run to run).
- `BALLISTIC_FX_SSR/VOLUMETRIC/SSAO=0|1` — force post-FX toggles after the volume stack applies, for A/B runs.
- **Lumen GI** (default ON, HW-RT-gated): `BALLISTIC_DX12_LUMEN=0` off / `=1` force-on; `_LUMEN_DEBUG=1` shows
  the raw indirect irradiance E; `_LUMEN_RAYS` / `_LUMEN_DENOISE_PASSES` / `_LUMEN_INTENSITY` / `_LUMEN_SKY` /
  `_LUMEN_EMA` tune; `_LUMEN_NOCARDS` / `_LUMEN_NOBOUNCE` / `_LUMEN_NODENOISE` / `_REFL_NOCARDS` A/B isolate.
  The env doors OVERRIDE the `GlobalIllumination` volume.

## Hard-won gotchas (do not relearn these)

- **Never mix raw-HDR and tonemapped samples in post shaders** (e.g. sharpen blur):
  extrapolation around the EXR sun goes negative → `pow` → NaN black holes. Tonemap first.
- Clamp float cubemap texels below fp16 max (~65504) before RGBA16F upload (sun = Inf → NaN).
- Editor frame order is UI build → scene render → present, so gizmo drags don't lag a frame.
- Asset tiles select on click RELEASE (drag must not steal the Inspector selection).
- Editor undo = whole-scene YAML snapshots pushed BEFORE each interaction
  (`EditorUndo.Push()`; `ImGui.IsItemActivated()` for widgets).
- Rider locks folders on Windows — `git mv`/renames of open dirs fail; copy + `git rm --cached`.
- FSQ/post/IBL shaders are **embedded resources** (`.hlsl` embedded under `BallisticEngine.DX12/`),
  not assets. Incremental DX12 builds do NOT re-embed a changed `.hlsl` — clean `obj/` + verify the
  embed (see memory `dx12-shader-edit-build-gotcha`).
- **HLSL NaN scrubs MUST be a component SELECT (ternary), never `lerp(v, 0, flag)`** — float
  `lerp`/`mix` is arithmetic (`v*(1-flag) + 0*flag`) and `NaN*0 == NaN`, `Inf*0 == NaN`: proven leak
  on AMD RX 9070 XT (driver test in `%TEMP%\bal-nan-test`). The broken form turned one Inf
  sun/specular pixel into NaN that the SSGI temporal EMA + multi-bounce + OIDN grew into a
  screen-eating black-noise field a STATIC camera could never flush (fast motion = disocclusion
  reject = flush). Same rule applies in every temporal-feedback shader (SSGI, TAA).
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
