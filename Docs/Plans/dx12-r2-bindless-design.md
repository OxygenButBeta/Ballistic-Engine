# R2 — Persistent Bindless for the CPU per-submesh draw path (design)

**Date:** 2026-06-20 · **Branch:** `dx12-perf-radical` · **Parent plan:** [dx12-perf-radical-plan.md](dx12-perf-radical-plan.md)
**Status:** DESIGN — for approval before implementation. Byte-identical target, kill-switch OFF default.

## The key realization (changes the risk profile)

R2 does **not** need a new bindless system or an SM6.6 rewrite. The engine **already has** a working persistent
bindless material path — the GPU-driven whole-mesh renderer uses it every frame:

- `Dx12Backend.BindlessHeap` — the persistent shader-visible heap, indexed in HLSL via SM6.6 `ResourceDescriptorHeap[i]`.
- `GpuMaterial` (struct, `Dx12GpuDrivenRenderer`) — carries the 6 bindless texture indices + all material factors.
- `GpuMaterials` — a StructuredBuffer SRV (root SRV t1) of `GpuMaterial`, N-buffered, stamp-gated (rebuilt only on a material-set change).
- `ResolveOrRegisterMaterialId(Material)` — registers ANY material (already used for the RT path, incl. split-import children) into the table and returns its id; `Bindless(tex, type)` maps a texture → persistent heap slot (or the neutral fallback, matching CPU `BindSrv`).
- `GBufferBindless.hlsl` — **shading is byte-identical to GBuffer.hlsl** (the file says so): the ONLY difference is that the material factors + 6 textures come from `GpuMaterials[MaterialId]` via `ResourceDescriptorHeap`, and the model matrix from `PerDraws[DrawIndex]`. Root sig = `drawRootSig` (root const b0 DrawIndex + SRV t0 PerDraws + SRV t1 GpuMaterials + CBV b1 Motion + `…HeapDirectlyIndexed`).

So the per-draw **6× `CopyDescriptorsSimple` + `AllocateRange` + `SetGraphicsRootDescriptorTable`** in the CPU
per-submesh loop (`DX12HDRenderer.cs:1467–1475`) is the ONLY thing standing between the CPU path and the same
bindless table the GPU-driven path already uses. R2 = route the CPU path's material binding through that table.

This is also the material half of **R3** (unified GPU-driven draw): once the CPU path resolves materials by id
into the shared table, collapsing it into ExecuteIndirect is the natural next step (R3 adds the per-submesh
indirect args + the GPU cull; R2 lands the bindless material binding first, still CPU-submitted).

## Current CPU per-submesh draw (what changes)

`DX12HDRenderer.cs:1446–1480`, per opaque submesh:
1. Fill `DrawConstants` (Mvp/Model + material factors) → `cbRing` slot, bind as root CBV b0.
2. `srvVisible.AllocateRange(6)` + 6× `BindSrv` (each = `CopyDescriptorsSimple` into the per-frame ring) + `SetGraphicsRootDescriptorTable(1, …)`.
3. `DrawIndexedInstanced`.

Steps 2 is the per-draw descriptor churn R2 removes. Step 1's material FACTORS also already live in `GpuMaterial`
(so they stop being per-draw CB data too — only the per-draw MODEL matrix stays per-draw).

## Design — two options, recommend Option A

### Option A (recommended): reuse `GBufferBindless.hlsl` + a per-draw root-const MaterialId, CPU-submitted

Keep the CPU submit loop and PSO switch, but bind the **bindless draw root sig + the GpuMaterials table**, and
pass `MaterialId` per draw as a root constant instead of rebinding 6 descriptors.

- **Material table:** call `gpuDriven.EnsureMaterialTable` already registers whole-mesh materials; extend the CPU
  loop to `gpuDriven.ResolveOrRegisterMaterialId(mat)` for each CPU-path submesh (same call the RT path uses, so
  split-import materials land in the table too). The table is already N-buffered + stamp-gated.
- **Per-draw model:** the CPU path doesn't have a `PerDraws` SRV. Two sub-choices:
  - **A1:** keep binding the model via a small per-draw CBV (the existing `cbRing` slot, trimmed to just Mvp/Model),
    and add a `MaterialId` root constant. Shader: a CPU-path entry that reads model from b0 CBV + material from
    `GpuMaterials[MaterialId]`. This is a NEW shader entry (a 3rd variant) — but it can be a thin `#define` over
    GBufferBindless's shading body (which is already identical to GBuffer.hlsl).
  - **A2:** write the CPU path's per-submesh `PerDraw{Mvp,Model,MaterialId}` into a CPU-owned `PerDraws` structured
    buffer (N-buffered) and reuse `GBufferBindless.hlsl` VERBATIM with a per-draw `DrawIndex` root constant. No new
    shader at all — the CPU loop just writes PerDraws[i] and sets DrawIndex=i per draw. **This is the cleanest:
    zero shader changes, reuses the proven byte-identical bindless shader.**
- **Recommend A2.** The CPU loop becomes: register material → write `PerDraws[draw] = {Mvp, Model, materialId}` →
  `SetGraphicsRoot32BitConstant(0, draw)` → `DrawIndexedInstanced`. No descriptor copies, no per-draw table.
  Bind `drawRootSig` + `drawPso` (GBufferBindless) once; bind `PerDraws`(t0) + `GpuMaterials`(t1) + Motion(b1) once.

### Option B: SM6.6 ResourceDescriptorHeap directly in GBuffer.hlsl (rejected)

Rewrite GBuffer.hlsl to index `ResourceDescriptorHeap` with per-material indices passed via root constants. This
duplicates what GBufferBindless.hlsl + GpuMaterials already do, for no gain, and risks shading drift from the
GPU-driven path. **Rejected** — A2 reuses the existing proven shader instead.

## Concrete change list (Option A2)

1. **Renderer (CPU loop, `DX12HDRenderer.cs:1359–1481`):**
   - Before the loop: `gpuDriven.EnsureMaterialTable(...)` already ran; bind `drawRootSig`/`drawPso`, set root SRVs
     t0=CPU `PerDraws`, t1=`gpuDriven.GpuMaterialsAddress`, CBV b1=motion, and the bindless heap.
   - Per submesh: `int mid = gpuDriven.ResolveOrRegisterMaterialId(mat); if (mid < 0) …fallback…;`
     write `cpuPerDraws[draw] = new PerDraw{ Mvp, Model, MaterialId=(uint)mid }`; `cl.SetGraphicsRoot32BitConstant(0,(uint)draw,0)`;
     `DrawIndexedInstanced`. DELETE the `AllocateRange`+6×`BindSrv`+`SetGraphicsRootDescriptorTable` block.
   - Add a CPU-owned N-buffered `PerDraws` structured buffer (mirror `cbRing`'s N-buffering by FrameSlot).
2. **Material registration ordering:** `ResolveOrRegisterMaterialId` may EXTEND the table mid-frame (a CPU-only
   material not in the whole-mesh set). The table is N-buffered + written to all slabs (see `RegisterMaterial`),
   so a mid-frame extend is overlap-safe IF the GpuMaterials buffer has spare capacity (`MaxMaterials`). Verify
   the CPU-path material count + whole-mesh count ≤ `MaxMaterials`; if not, the resolve returns -1 → fall back to
   the OLD descriptor-table path for that submesh (keep `BindSrv` alive as the over-capacity fallback).
3. **Skinned path:** unchanged in R2 (it has its own bone SRV + GBufferSkinned.hlsl). R3 folds it in via compute
   skinning. R2 scopes to the NON-skinned CPU per-submesh opaque path only.
4. **Kill-switch:** `BALLISTIC_DX12_CPU_BINDLESS=0` → the old `BindSrv` descriptor-table path (byte-identical
   fallback). Default ON after the gate passes.

## Hazards + how each is handled

- **Shading drift:** A2 reuses GBufferBindless.hlsl verbatim — the file is already certified byte-identical to
  GBuffer.hlsl. The gate (byte-diff) catches any divergence (e.g. a sampler/address-mode mismatch: GBufferBindless
  uses a static `LinearWrap` sampler; the CPU path's `BindSrv` relied on the same — confirm the wrap/aniso match).
- **Material id capacity:** bounded by `MaxMaterials`; over-capacity → -1 → old path fallback (no crash, no wrong
  material). Log when it happens (no silent cap).
- **N-buffer overlap:** the new CPU `PerDraws` buffer is N-buffered by FrameSlot exactly like `cbRing`/`motionCb`
  (P0b invariant). GpuMaterials is already N-buffered.
- **Descriptor lifetime (the EF3/Hi-Z precedent):** `BindlessHeap.Reset()` happens on a material-table rebuild +
  scene swap (`RenderSetsCleared`); the texture slots are re-registered by `EnsureMaterialTable`. The CPU path
  must register AFTER that reset (it already runs post-EnsureMaterialTable). Verify a scene swap + a resize both
  re-point correctly (the same test R2's gate demands).
- **Capability:** the bindless path already ships on the dev RX 9070 XT (whole-mesh uses it), so no new HW gate is
  needed beyond what GPU-driven already requires. If GPU-driven is force-disabled (`BALLISTIC_DX12_GPUDRIVEN=0`),
  the material table isn't built → CPU bindless must fall back to the old path (gate on `gpuDrivenOn`).

## Verification (the gate)

- **Byte-identical** vs the pre-R2 deterministic reference on: BistroExterior (mostly whole-mesh — few CPU draws),
  a split-by-node import scene (HEAVILY CPU-path — the real R2 exercise), CornellBox, SkinTest (skinned untouched),
  TransparentTest (forward untouched). `bal imgdiff` meanError 0.
- **Scene swap + resize** re-point check (load scene A → B, resize 4K→1080p): no wrong-material, no hang.
- **Over-capacity fallback:** force `MaxMaterials` low via a temp door, confirm the -1 path renders correctly.
- **Tracy (live GPU):** the per-draw `CopyDescriptorsSimple` cost disappears from CPU submit on the split-node
  scene; the per-frame ring no longer churns for the CPU path.

## What R2 does NOT do (scoping)

- Does not touch the skinned path, transparents, or any post pass (those keep their own descriptor binding).
- Does not remove the per-frame `srvVisible` ring outright (skinned + any non-bindless consumer still use it) —
  it just stops the CPU OPAQUE path from feeding it. Full ring removal waits until R3 also moves skinned to
  bindless.
- Does not change ExecuteIndirect/cull — the CPU path still CPU-submits one draw per submesh (R3 collapses that).
