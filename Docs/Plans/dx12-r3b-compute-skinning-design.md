# R3b — Compute skinning + skinned into GPU-driven (design + shader landed)

**Date:** 2026-06-20 · **Branch:** `dx12-perf-radical` · **Parent:** [dx12-perf-radical-plan.md](dx12-perf-radical-plan.md)
**Status:** SHADER LANDED (`Shaders/SkinCompute.hlsl`), C# integration is the next focused step. Opt-in, DRED-gated.

## Goal

Skin a skinned mesh's position/normal/tangent on the GPU (compute) into a transient buffer, then draw the result
through the SAME non-skinned GPU-driven ExecuteIndirect + GBufferBindless path as static/whole-mesh geometry —
removing the dedicated skinned PSO + per-skinned-draw VS skinning + per-draw 6× descriptor binds.

## Why byte-identical is achievable (the key bet)

`SkinCompute.hlsl` is a **verbatim copy** of `GBufferSkinned.hlsl`'s skin stage: same `SkinMatrix` weighted blend
(same bone order), same `mul(float4(v,1), skin).xyz`, same row-vector convention, same `(int4)(indices+0.5)`
rounding. Only the MESH-LOCAL skin is done in compute; the model/Mvp transform stays in GBufferBindless (already
byte-identical to the static path). So compute-skin → bindless-draw should match the VS-skinned path to the bit,
IF the FP math order is preserved (it is — do NOT reorder the weighted sum). This is the gate: prove it before
wiring RenderInto.

## C# integration steps (each DRED + byte-identical gated, opt-in `BALLISTIC_DX12_GPUDRIVEN_SKINNED=1`)

1. **Compute pass** in `Dx12GpuDrivenRenderer` (or a sibling): rootsig = CBV b0 (SkinParams) + SRV t0 bones +
   t1..t5 in-streams + UAV u0..u2 out-streams; PSO from `SkinCompute.hlsl` CSMain. Mirror the RtAO compute pass
   setup (rootsig1 + CreateComputePipelineState + a small descriptor heap).
2. **Transient skinned-vertex buffers**, one set (Pos float3 / Normal float3 / Tangent float4) per skinned
   renderer per frame, sized to the mesh's vertex count. N-buffered by FrameSlot (P0b) OR pooled + fence-gated.
   The bone matrices already upload via `boneMatrixRing` (reuse it as the t0 SRV — it's the same transposed data
   the skinned VS read).
3. **Dispatch** `(vertexCount + 63)/64` per skinned renderer, UAV-barrier the out-buffers, then transition them
   to NonPixelShaderResource/vertex-buffer state for the draw.
4. **Draw**: RenderInto must accept a PER-RENDERER vertex-buffer OVERRIDE (the skinned out-buffers replace
   mesh.VertexBuffer/NormalBuffer/TangentBuffer; UV + index come from the ORIGINAL mesh buffers). The skinned
   model matrix is identity-for-skin (skin already in mesh-local animated pose) → Mvp = Model * viewProj exactly
   as the skinned VS did. Add skinned renderers to `gpuDrivenGeometry` carrying their override buffers.
   - Smaller first slice: draw the skinned out-buffers through GBufferBindless DIRECTLY (a dedicated skinned-GPU
     loop), NOT through RenderInto, to validate the compute-skin byte-identical bet before the RenderInto refactor.

## Hazards (R2/R3a lessons)

- **Descriptor-heap order** (the R2 PC-reset hang): any bindless draw binds the heap BEFORE the directly-indexed
  root sig. The compute pass uses a plain (non-bindless) rootsig so it's exempt, but the bindless DRAW of the
  skinned result is not.
- **Draw-order determinism** (the R3a fix): if skinned renderers feed RenderInto, order them by first-appearance
  (meshOrder), never by HashSet/Dictionary iteration.
- **NOT byte-identical risk:** if the compute-skin diverges from the VS skin (FP reorder, a normalize moved), the
  skinned surface shifts. Gate on SkinTest (`Assets/Characters/CesiumMan.glb`) deterministic capture: compute-skin
  ON vs OFF must match (or be sign-off visual parity if a sub-pixel residual like R3a's appears).
- **Transient buffer lifetime under P0b overlap:** the skinned out-buffers are GPU-written then GPU-read same
  frame; under overlap the next frame must not reuse them while in flight → N-buffer or fence-gate (DeferredRelease
  pattern).

## Test gate

- SkinTest scene, deterministic paused capture: `BALLISTIC_DX12_GPUDRIVEN_SKINNED=1` vs OFF — byte-identical (or
  documented sub-pixel residual). DRED trap clean. GBV adds zero new messages.
- Default OFF; default path (skinned VS) stays byte-identical.

## What's done vs pending

- DONE: `SkinCompute.hlsl` (byte-identical skin math, embedded, builds).
- PENDING: the C# compute pass + transient buffers + dispatch + the draw integration + the SkinTest gate. This is
  an R2+R3a-sized subsystem; each step is its own DRED/byte-identical-gated commit (do not land it half-wired —
  an untested transient-buffer/dispatch path is exactly the class that PC-reset-hung during R2).
