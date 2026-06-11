# Ballistic Engine

Custom C#/.NET 9 game engine — **NOT a Unity project** despite the folder location. Stack:
OpenTK 4.9.4 (OpenGL 3.3+), AssimpNet + StbImageSharp + Magick.NET (import-time only),
YamlDotNet (scenes), ImGui.NET (editor). Idioms deliberately mirror Unity
(Entity/Behaviour, AssetDatabase, meta files, edit/play split).

## Build & run

```
dotnet build BallisticEngine.slnx          # 3 projects; the old .sln is gone
dotnet run --project BallisticEngine.Runtime [projectPath]   # standalone player
dotnet run --project BallisticEngine.Editor  [projectPath]   # ImGui editor
```

Default project path: `<repo>\SampleProject`. Projects:
- `BallisticEngine.csproj` (root) — engine **library**; globs all engine folders, `<Compile Remove>`s the exe subfolders.
- `BallisticEngine.Runtime/` — thin player exe (Program + BEngineEntry over `EngineBootstrap`/`EngineLoop`).
- `BallisticEngine.Editor/` — ImGui editor exe (ImGuiBackend/, EditorApp/, Panels/, EditorCamera/, Gizmo/).

## Layering rules (auditable by grep)

| Layer | May use | Must NOT use |
|---|---|---|
| `Shared/`, `ToolKit/` | BCL only | everything else |
| `Abstraction/` | Shared, OpenTK.Mathematics | GL calls, file formats |
| `Engine/` | Abstraction, Shared | Assimp/Stb/Magick, asset file I/O |
| `OpenGL/` | Abstraction, Engine types, GL | Assimp/Stb/Magick |
| `AssetPipeline/` | everything + Assimp/Stb/Magick/STJ | GL calls (GPU upload goes through `RenderAsset`) |

`AssetPipeline/` is the ONLY place allowed to reference AssimpNet/StbImageSharp/Magick.NET.
CPU data types (`MeshData`, `TextureData`) live in `Abstraction/Rendering/Data/`.

## Asset system (Unity-style)

- Project dir: `project.json` + `Assets/` (sources + `.meta` sidecars with GUIDs) + `Library/`
  (gitignored: binary artifacts `.bmesh`/`.btex`, `ArtifactDB.json`, `Thumbnails/`).
- `AssetDatabase.Initialize/Refresh/Load<T>("Assets/...")` — GUID-cached instances, never
  throws (logs + returns null; materials substitute fallback textures).
- Asset refs in files: `"Assets/...path"` or `"guid:<32hex>"` (`AssetRef`).
- Native text assets read directly: `.mat`, `.shader`, `.cubemap` (JSON), `.glsl`.
- Loading any image asset AS `Texture3D` builds an equirect cubemap (skybox from .hdr/.exr).
- `ModelImporter` (meshIndex -1) merges the whole model with one submesh per source material
  and generates a sibling `<Model>_Materials/` folder of `.mat` assets (rewritten on reimport).
- `.pyscene` (Falcor) imports regex-parse camera/lights/models/envmap → sibling `.scene`.

## Scenes & components

- `.scene` = YAML: `sceneComponents:` (scene-wide `SceneBehaviour`s) + `entities:` with
  components reflected via `ComponentReflection` (public props AND fields; asset members
  serialize as guid refs). `ProjectManifest.StartupScene` loads at launch.
- **Edit/Play split:** `SceneManager.IsPlaying/StartPlay/StopPlay`. In edit mode
  `AddComponent` skips `OnBegin`/`OnEnabled`; StopPlay restores a YAML snapshot.
- **`OnAttach`/`OnDetach` fire in BOTH modes** — render registration must live there,
  never in `OnEnabled` (play-only), or the editor viewport goes black.
- `SceneBehaviour` = scene-wide component (Skybox, PostProcessVolume): lives on the Scene,
  has its own registry (`ComponentRegistry.SceneMenu`) and the editor's "Scene" hierarchy tab.
  Pattern: `static Active` set in OnAttach/OnDetach; the renderer reads it per frame.
- `Input.Enabled` is the master gate — the editor disables engine input outside
  play-with-Game-view-focused, so component debug keys don't leak into editing.

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
- FSQ/post/IBL shaders are **embedded resources** under `OpenGL/Shader/Embedded/`, not assets.

## Repo facts

- `SampleProject/Assets/Default/Bistro_v5_2/` (1.6 GB test content) is **gitignored**
  pending a git-lfs decision; `Main.scene` references it, so it must exist locally.
- Repo already tracks ~460 MB of binaries; git-lfs migration is an agreed follow-up.
- Known half-finished: instanced drawing (disabled in GLHDRenderer), skybox shader as C#
  strings, editor shading-mode dropdown is UI-only (renderer view modes not wired yet),
  synchronous asset refresh blocks the editor window on big imports (no progress UI).
