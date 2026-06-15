# GpuSceneQuery — API Design Proposal (M1)

**Status:** PROPOSAL / awaiting approval (2026-06-15). Do NOT implement until checked in.
**Parent plans:** [gpu-scene-query-autoplacement.md](gpu-scene-query-autoplacement.md) (the *why*),
[ai-native-engine-master-plan.md](ai-native-engine-master-plan.md) §4/§5/§9.
**Branch:** `dx12-renderer`. **Substrate:** the DXR TLAS in
[BallisticEngine.DX12/Resources/Dx12SceneAS.cs](../../BallisticEngine.DX12/Resources/Dx12SceneAS.cs) — NOT the old
GL SDF (slow + broken; HW rays make a distance field unnecessary for occupancy/visibility).

---

## 0. M0 result (the gate this builds on)

Clean base VERIFIED: build 0 errors; zero `OpenTK.Mathematics`/`OpenTK.Graphics`/`OpenGL` *code* refs (OpenTK
windowing/audio deliberately kept per the user's Step-5 decision); SunTemple + Bistro render correct; A/B vs
pre-math-migration commit `6f555414` = pure ULP noise (SkyTest mean 0.004/max 1; LightTest mean 0.54/max 12,
visually pixel-perfect). No silent math regression.

---

## 1. The decisive infrastructure facts (from reading the code)

These drive every design choice below; each is verified in the current tree:

- **DXR is `Tier1_1` on the dev GPU** (the `Dx12DxrProbe` self-test reported it). Tier 1.1 ⇒ **inline `RayQuery`
  in a plain compute shader is available** — no RT PSO state object, no shader binding table, no raygen/miss/
  closesthit shaders. The existing RT *render* effects (`DxrShadows/Reflections/Gi.hlsl`) use the heavier
  `DispatchRays` path; a *query* layer does not need it.
- **`Dx12SceneAS`** already builds one BLAS per mesh + a TLAS over `RuntimeSet<IStaticMeshRenderer>`, stamp-cached
  (rebuild only when geometry/transforms change). It exposes `Ensure(renderers)`, `Valid`, `TlasAddress`, and
  `CreateTlasSrv(cpuHandle)` (writes a null-resource AS SRV into any heap slot). `RuntimeSet<IStaticMeshRenderer>`
  is populated in `OnAttach` (fires in BOTH edit + play + headless), so the TLAS is buildable **headlessly**.
  Today it is created *lazily, only when an RT volume effect is on* — the query layer must own its **own**
  `Dx12SceneAS` (or share it) so queries work with RT effects off.
- **Compute-pass template** (`Dx12HiZ.cs`): root sig with SRV/UAV/CBV `DescriptorRange1` tables → compile via
  `Dx12ShaderCompiler.Compile(DxcShaderStage.Compute, hlsl, "Entry", "File.hlsl")` → `Dx12DescriptorHeap(dev,
  …SrvUav…, n, shaderVisible:true)` → `dev.ExecuteSync(cl => { SetDescriptorHeaps; SetComputeRootSignature;
  SetPipelineState; tables; Dispatch; barriers; })`. **GOTCHA:** `SetDescriptorHeaps` BEFORE the root sig.
- **Buffer + readback helpers** (`Dx12Device`/`Dx12Buffer<T>`): `CreateDefaultBuffer<T>(span, finalState)` (upload
  input), `CreateUavBuffer<T>(…, UnorderedAccess)` (compute output), `CreateReadbackBuffer(bytes)` (CPU read);
  structured-buffer SRV/UAV = `ViewDimension.Buffer` + `StructureByteStride`. `ExecuteUpload` is a SEPARATE
  command list from the render path (never share — corrupts).
- **`bal` CLI** dispatches `ICommand` by verb (`Program.cs` `ICommand[]`), prints JSON to stdout via
  `Json.Write`, logs to stderr, honest exit codes (2 = usage, 1 = error). `RenderCommand` does NOT host DX12 —
  it **spawns `BallisticEngine.Runtime.exe`** with `BALLISTIC_*` env vars and reads back the output file. The
  CLI process itself is device-free.
- **MCP tool** = a `ToolDefinitions` JSON-schema entry + a `MapTool` arm → a command-port method → a
  `RemoteHandlers.Dispatch` switch arm in the **editor** (runs on the editor main thread, over the named pipe).
  Requires the editor to be running. The editor renders on DX12, so it has a live `Dx12SceneAS`.
- **G-buffer** (`Dx12GBuffer`): RT0 albedo `R8G8B8A8_UNorm_SRgb`, RT1 world-normal `R16G16B16A16_Float`
  packed `N*0.5+0.5`, RT2 metal/rough/ao `R8G8B8A8_UNorm`, RT3 emissive `R16G16B16A16_Float`, RT4 motion
  `R16G16_Float`; depth `R32_Typeless` (DSV `D32`, SRV `R32_Float`). Readback = `GetCopyableFootprints` →
  readback heap → `CopyTextureRegion` → `Map` (256-byte row-pitch alignment — use the returned `rowPitch`).
  `Dx12OffscreenTarget.SaveBmp/ReadColorRgb/ReadColorRgba8` are the existing readback recipes to mirror.

---

## 2. The verbs (the API surface)

One C# class `GpuSceneQuery` (in `BallisticEngine.DX12/Query/`), owning a `Dx12SceneAS` + a small set of compute
PSOs. All methods are **batched** internally (the GPU cost is in the dispatch, not the point count) even when the
public call takes a single point — a single-point call just uploads a 1-element buffer.

| Verb | Signature (conceptual) | Substrate | Milestone |
|---|---|---|---|
| `OccupancyAt` | `bool[] OccupancyAt(Vector3[] pts)` → inside-solid per point | inline ray-parity (count hits along a fixed axis; odd ⇒ inside) | M2 |
| `Visibility` | `bool[] Visibility((Vector3 a, Vector3 b)[] pairs)` → a sees b | one `RAY_FLAG_ACCEPT_FIRST_HIT` ray a→b, hit before `dist` ⇒ blocked | M2 |
| `ClassifySpace` | `SpaceClass[] ClassifySpace(Vector3[] pts)` (Open / Enclosed / Solid) | fixed N-ray sphere from p; hit-fraction + mean hit-distance ⇒ class | M2 |
| `NudgeToFreeSpace` | `Vector3[] NudgeToFreeSpace(Vector3[] pts)` → nearest free point | if occupied, march outward along the least-occluded fixed direction | M3 |
| `VisibilityClusters` | `int[] VisibilityClusters(Vector3[] pts)` → room label per point | pairwise visibility graph (fixed sample) → connected components | M3 |

**Why ray-parity for occupancy (not an SDF):** with a TLAS we get exact triangle intersection for free. Cast a
ray from `p` along `+X` (a fixed axis) to a far plane and count opaque hits; **odd ⇒ inside solid**. Watertight
meshes give exact results; for the (common) non-watertight case we cast along **3 fixed axes** (±X dominant, ±Y,
±Z as tie-breakers) and majority-vote — deterministic, no field to bake, no JFA. This is the "HW rays make a
distance field unnecessary" insight from the plan.

**`SpaceClass`**: from a fixed-pattern sphere of K rays (see §4) cast from `p`, `hitFraction = hits/K` and
`meanHitDist`. `Solid` = ray-parity says occupied. `Enclosed` = high hitFraction (walls close on most sides).
`Open` = low hitFraction / far mean distance (sky/outdoors). Two tuning constants, both internal/hidden (the APV
anti-pattern rule: zero front-door knobs).

---

## 3. Inline `RayQuery` (compute) vs `DispatchRays` — **RECOMMENDATION: inline RayQuery**

| | Inline `RayQuery` (compute) ✅ recommended | `DispatchRays` (RT PSO + SBT) |
|---|---|---|
| Shader objects | 1 compute shader, `RayQuery<flags>` template | raygen + miss + closesthit lib + hit group |
| Pipeline | `CreateComputePipelineState` (same as HiZ) | `CreateStateObject` + SBT records + `SetPipelineState1` |
| Binding | TLAS as plain SRV `t0`, points SRV `t1`, results UAV `u0`, consts `b0` | global root sig + per-record SBT layout |
| Batching | one thread per query point — natural fan-out | one ray per `DispatchRays` thread; awkward for arbitrary point lists |
| HW requirement | **Tier 1.1** (have it) | Tier 1.0 |
| Determinism | fully under our control (no driver SBT scheduling) | same rays, more moving parts |
| Reuses | the `Dx12HiZ` compute template verbatim | the `Dx12DxrProbe` template |

Inline RayQuery is **strictly simpler** for a point-list query workload (no SBT, no hit shaders — the compute
thread does `q.TraceRayInline(...); q.Proceed(); q.CommittedStatus()`), batches naturally (thread = query point),
and is fully Tier-1.1-supported here. If a future low-end target is Tier 1.0-only, the same HLSL logic ports to a
`DispatchRays` raygen with no algorithm change — but that is a fallback, not the primary path. **Decision needed:
approve inline RayQuery as the primary path.**

---

## 4. Determinism scheme (mandatory — the verify harness is byte-identical-based)

No `Math.Random`, no frame-rotated hash (the GI shader's `Hash2(idx, frameIndex)` is deliberately non-
deterministic + temporally accumulated — we must NOT copy it). Instead, **fixed sample patterns baked as shader
constants**, exactly like the JFA "7-ray-parity shell seeds":

- **Occupancy / visibility:** the ray directions are *fixed axes* / the *exact a→b vector* — already deterministic.
- **ClassifySpace sphere rays:** a **fixed Fibonacci-sphere of K directions** (K compile-time constant, e.g. 32),
  computed once on the CPU and uploaded as a constant array — identical every run, every machine (the golden
  ratio spiral is closed-form, no RNG). Two runs ⇒ byte-identical classification.
- **NudgeToFreeSpace:** marches along the *fixed* least-occluded direction from the ClassifySpace pattern — no
  stochastic search.

Determinism is verified by a self-test door that runs each query twice and asserts identical output (M2).

---

## 5. Descriptor / heap / dispatch plan (one compute pass per verb family)

Mirrors `Dx12HiZ` exactly:

```
root sig:  table0 = SRV range t0..t1  (t0 = TLAS AS-SRV, t1 = query-input structured buffer)
           table1 = UAV range u0      (u0 = result structured buffer)
           CBV     b0                 (QueryConstants: count, mode, K, params)
heap (shader-visible, SrvUav): slot0 = TLAS SRV (via sceneAS.CreateTlasSrv), slot1 = input SRV, slot2 = result UAV
dispatch:  ExecuteSync(cl => { SetDescriptorHeaps(heap); SetComputeRootSignature; SetPipelineState;
             SetComputeRootDescriptorTable(0, gpu0); SetComputeRootDescriptorTable(1, gpu2);
             SetComputeRootConstantBufferView(2, cb); Dispatch((count+63)/64,1,1);
             UAV barrier; transition result→CopySource; CopyBufferRegion→readback; })
readback:  Map the readback buffer, copy results to a managed array, Unmap.
```

The input buffer holds query points (or a/b pairs); the result buffer holds `uint`/`float` per point. One
`Query.hlsl` with multiple entry points (`CSOccupancy`, `CSVisibility`, `CSClassify`, `CSNudge`) — one PSO each,
all sharing the root sig. (`Visibility` input element = two `float3`; the others = one `float3`.)

**Ordering / lifecycle:** `GpuSceneQuery.Run*` first calls `sceneAS.Ensure(RuntimeSet<IStaticMeshRenderer>
.ReadOnlyCollection)`; if `!sceneAS.Valid` (empty scene) it returns the trivial answer (all-open / all-visible /
not-occupied). It is an **on-demand** call (not per-frame) — a `bal query` run boots the headless DX12 runtime,
builds the AS once, runs the dispatch, writes JSON, exits. No per-frame budget concern.

---

## 6. How the agent reaches it (M4 — flagged here, built later)

Three surfaces, all thin over the same `GpuSceneQuery`:

1. **`bal query occupancy|visibility|classify <scene> --points "x,y,z;..."`** (+ `bal query nudge`,
   `bal query rooms`). Same subprocess pattern as `bal render`: the CLI spawns `BallisticEngine.Runtime.exe` in a
   new **query mode** (env `BALLISTIC_QUERY=<json-spec-path>` → the headless runtime runs the query, writes
   `BALLISTIC_QUERY_OUT`, exits), then the CLI relays the JSON. Keeps the CLI device-free; reuses proven headless
   DX12. **JSON in, JSON out, honest exit codes.**
2. **MCP tools `occupancy` / `visibility` / `classify`** → command-port methods `query.occupancy/...` →
   `RemoteHandlers` arms that call the **editor's live** `GpuSceneQuery` (editor renders on DX12). Live, no
   subprocess. (Editor-side; gated carefully given GPU-hang history — verify headless first.)
3. **`bal gbuffer <scene> --out dir`**: dumps depth (linear), world-normal (unpacked), albedo as raw arrays
   (`.bin` + a `.json` header with dims/encoding) for the agent's "raw perception" — reuses the
   `Dx12OffscreenTarget.ReadColor*` readback recipe over the G-buffer MRTs (a new query-mode env or a
   `BALLISTIC_GBUFFER_DUMP` flag in the headless runtime).

---

## 7. Milestone plan (each = build + verify + commit + evidence)

- **M2** — `GpuSceneQuery` + `Query.hlsl` (`CSOccupancy/CSVisibility/CSClassify`) + a standalone `Dx12SceneAS`.
  Self-test door `BALLISTIC_DX12_SCENEQUERY_TEST=1`: point-in-a-known-wall ⇒ occupied; point-in-open-air ⇒ free;
  a/b pair across a wall ⇒ not visible; a/b in open line-of-sight ⇒ visible; run twice ⇒ byte-identical. Commit.
- **M3** — `NudgeToFreeSpace` + `VisibilityClusters`. Verify on SunTemple: distinct rooms get distinct labels; a
  point nudged out of a column ends up in free space. Commit.
- **M4** — `bal query *` + `bal gbuffer` + MCP `occupancy/visibility/classify`. Verify the agent gets sane JSON.
- **M5** — structured perf query (per-pass GPU ms, already in `RenderStats`) + live runtime introspection (read
  component live values during play, building on the existing `--watch`/`entity.get` reflection surface).

**STOP after M5.** No GI / auto-placement / intent verbs (those are Track-B follow-ons, out of scope here).

---

## 8. Decisions (M1 check-in — APPROVED 2026-06-15)

1. **Inline RayQuery (compute, Tier 1.1) as the primary path** — ✅ APPROVED (§3). Ports to DispatchRays only if a
   Tier-1.0-only low-end target ever needs it.
2. **Occupancy = ray-parity (3 fixed axes, majority vote), no SDF** — ✅ APPROVED (§2). No distance-field fallback;
   the old GL SDF dies with GL.
3. **`bal query` via subprocess** (device-free CLI, like `bal render`) — ✅ APPROVED (implied by #4).
4. **M4 = CLI-only** (`bal query` + `bal gbuffer` + MCP tools that shell out to the CLI). The editor's LIVE DX12
   query path (RemoteHandlers `query.*` against the editor's `Dx12SceneAS`) is **DEFERRED** — it touches the same
   GPU surface that hung before; re-open deliberately once verified headless. ✅ APPROVED.

**Workflow survey (wf_6e50c773-649) corroboration:** float UAVs have no DX12 atomics (fine — one thread writes one
result, no atomics needed); `SetDescriptorHeaps`-before-rootsig only matters for *bindless* (the query pass uses
descriptor tables, so it follows the HiZ order); `CreateUavBuffer<T>`/`CreateReadbackBuffer`/structured SRV+UAV all
confirmed available on `Dx12Device`.
