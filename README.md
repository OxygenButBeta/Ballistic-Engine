# Ballistic Engine

**A from-scratch C# / .NET 9 game engine with a DirectX 12 + hardware ray tracing renderer, a full ImGui editor, and a headless, AI-operable agent surface.**

![.NET](https://img.shields.io/badge/.NET-9.0-512BD4?logo=dotnet&logoColor=white)
![Platform](https://img.shields.io/badge/platform-Windows-0078D6?logo=windows&logoColor=white)
![Renderer](https://img.shields.io/badge/renderer-DX12%20%2B%20DXR-76B900)
![License](https://img.shields.io/badge/license-MIT-green)

Ballistic Engine is a solo project exploring how far a modern, real-time engine can be pushed in pure C#: a real-time-ray-traced global illumination pipeline, a GPU-driven renderer, a Unity-style editor and asset pipeline, C# hot-reload scripting, and a physics system — all written from the ground up. Its idioms deliberately mirror Unity (Entity/Behaviour, `AssetDatabase`, `.meta` files, an edit/play split) so the workflow feels familiar.

> **Not a Unity project** despite what the folder name might suggest — it's a standalone engine.

---

## 🎥 Media

<img src="https://github.com/user-attachments/assets/b0a6f0ff-ceb6-424b-92cb-0abdaebef505" width="49%" />
<img src="https://github.com/user-attachments/assets/ee589419-5371-4b64-a08b-a24ffeb6cb74" width="49%" />

![Screenshot](https://github.com/user-attachments/assets/816fb1af-331c-4cb8-96e8-f26593459c94)

🎬 **[Watch on YouTube](https://www.youtube.com/watch?v=6uzjT07534k)**

---

## ✨ Highlights

### Rendering — DX12 + DXR
- **Hardware ray-traced global illumination** ("Lumen"-style): per-pixel screen traces fall back to an inline `RayQuery` over the scene TLAS, sampling a **per-triangle surface-cache radiance cache** filled by a compute pass with direct lighting, multi-bounce radiosity, and emissive-triangle area-light NEE. Screen-probe gather + a sparse world-space radiance clipmap for the far field.
- **GPU-driven pipeline** — whole-mesh geometry is drawn via `ExecuteIndirect` after a GPU compute frustum cull, Hi-Z occlusion cull, and a bindless material table (CPU submit was the bottleneck on Bistro-scale scenes).
- Cascaded shadow maps (texel-snap cached), **GTAO**, **SSR + ray-traced reflections** (reflections re-sample the same GI radiance cache), volumetric fog, **TAA**, bloom, and automatic exposure.
- **Upscaling & denoise** via vendored native SDKs — AMD **FSR**, Intel **XeSS**, NVIDIA **DLSS**, and Intel **OIDN** for denoising.
- A z-prepass–invariant frame with a transient render-target pool and per-pass GPU timers.

### Editor
- **ImGui-based editor** with a scene viewport, transform gizmos, and collider drag-handles.
- **Attribute-driven inspector** — one drawer pipeline shared by components and post-processing volume parameters (`[Header]`, `[Range]`, `[ShowIf]`, `[FoldoutGroup]`, …); a new value type is one drawer registration, no hand-rolled widgets.
- Hierarchy, asset browser (drag-and-drop), whole-scene YAML undo, and an in-editor realtime profiler.
- **Unity-style post-processing volume framework** (`Volume` → `VolumeProfile` → `VolumeComponent`s, blended per frame).

### Assets & Scripting
- **Unity-style asset pipeline** — `AssetDatabase`, `.meta` GUID sidecars, and a `Library/` artifact cache. Import via AssimpNet / StbImageSharp / Magick.NET, with a `ModelImporter` that auto-binds PBR textures by filename convention, converts FBX units, and can import **Unity `.unitypackage`** scenes/prefabs.
- **C# game scripting** — game code is any `.cs` under the project, compiled to a collectible `AssemblyLoadContext` and **hot-reloaded** (on window-focus regain or Ctrl+R), including **live reload during play**. Script exceptions are sandboxed — they never crash the engine.

### Physics
- **BepuPhysics 2** behind an `IPhysicsWorld` abstraction — rigidbodies, box/sphere/capsule/mesh colliders (with auto-fit), triggers, continuous collision, and `OnCollision*` / `OnTrigger*` contact events on behaviours.

### 🤖 Agent surface (AI-operable)
The engine is fully operable **headlessly**, with a CLI (`bal`) and an MCP server that let a coding agent (or a script) drive it without a window — every verb prints JSON with honest exit codes:

- `bal map / schema / scene` — orient in a project and do typed scene CRUD (never guess component members).
- `bal simulate` — the **real engine headless** (scripts + physics play, no rendering); deterministic scripted input, numeric time series.
- `bal render` + `bal imgdiff` — deterministic, diffable captures and perceptual image diffs.
- `bal query` — **spatial perception** via inline DXR `RayQuery` over the scene TLAS (occupancy / classify / rooms / visibility) so the agent asks the 3D world instead of guessing from pixels.
- `bal gbuffer / perf / validate / describe` — raw G-buffer dumps, perf stats, and scene checks.

---

## 🧱 Tech stack

- **Language / runtime:** C# 13 on .NET 9
- **Graphics:** DirectX 12 + DXR via [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows) (hardware ray tracing required for GI)
- **Math & audio:** OpenTK (`OpenTK.Mathematics` + OpenAL bindings)
- **Physics:** BepuPhysics 2
- **Asset import:** AssimpNet, StbImageSharp, Magick.NET
- **Serialization:** YamlDotNet (scenes) + System.Text.Json
- **Editor UI:** ImGui.NET on a DX12 backend
- **Profiling:** Tracy 0.11.1 (opt-in)

---

## 🚀 Build & run

Requires the **.NET 9 SDK** and a **Windows PC with a ray-tracing-capable GPU** (DX12 + DXR).

```bash
dotnet build BallisticEngine.slnx

# Standalone player
dotnet run --project BallisticEngine.Runtime [projectPath]

# ImGui editor
dotnet run --project BallisticEngine.Editor  [projectPath]

# Headless agent CLI
dotnet run --project BallisticEngine.Cli -- --help
```

`projectPath` defaults to `SampleProject/`. Some large sample scenes reference external test content that is not committed (kept out of git pending a git-lfs decision) — the lighter scenes (e.g. Cornell Box) run out of the box.

---

## 📂 Project layout

The engine is a single library (`BallisticEngine.csproj`) plus thin executables, layered by strict dependency rules that are auditable by grep:

| Project / folder | Role |
|---|---|
| `Abstraction/`, `Shared/`, `ToolKit/` | Engine-agnostic types and BCL-only utilities |
| `Engine/` | Core: entities, components, scenes, rendering data, volumes, physics components |
| `BallisticEngine.DX12/` | The DX12 + DXR backend (renderer, GI, embedded HLSL) |
| `Physics/` | BepuPhysics 2 implementation of `IPhysicsWorld` |
| `AssetPipeline/` | Import (Assimp / Stb / Magick), scripting compilation, Unity import |
| `BallisticEngine.Runtime/` | Standalone player executable |
| `BallisticEngine.Editor/` | ImGui editor executable |
| `BallisticEngine.Cli/` | `bal` — the headless agent CLI |
| `BallisticEngine.Mcp/` | MCP server bridging to the editor |

The only third-party leaks are contained: `AssetPipeline/` is the sole place allowed to touch Assimp/Stb/Magick, `Physics/` the sole place allowed to touch BepuPhysics.

---

## ⚠️ Status

An actively developed solo project and a learning/experimentation ground for graphics programming and engine architecture. Windows-only. APIs move fast and there are no stability guarantees — expect rough edges.

## 📄 License

Released under the [MIT License](LICENSE).

Bundled native SDKs (FSR, XeSS, DLSS, OIDN) are the property of their respective vendors and are covered by their own licenses.
