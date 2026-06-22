# R5 — Visibility buffer (design + shaders landed)

**Date:** 2026-06-20 · **Branch:** `dx12-perf-radical` · **Parent:** [dx12-perf-radical-plan.md](dx12-perf-radical-plan.md)
**Status:** SHADERS LANDED (`VisBuffer.hlsl` raster, `VisResolve.hlsl` material resolve). C# integration is the next
focused step (G-buffer typeless+UAV is the load-bearing change). Opt-in, DRED/perceptual-parity gated.

## Goal + architecture

Cut the geometry pass's G-buffer write bandwidth: instead of rasterizing into the fat 5-RT G-buffer, rasterize a
SINGLE RG32_UINT visibility id `{ DrawIndex, (meshletIndex<<8)|localPrim }` + depth, then a compute pass resolves
each pixel's material into the SAME fat G-buffer the deferred lighting already reads — so downstream
(lighting / Lumen / SSR / GTAO) is UNCHANGED.

- **VisBuffer.hlsl** (landed): reuses R4's amplification + mesh shader (frustum + cone cull); the PS writes only the
  id. Per-primitive output carries `(meshletIndex<<8)|localPrim` so the resolve recovers the meshlet (VertOffset)
  AND the local triangle (the packed local-vert triple).
- **VisResolve.hlsl** (landed): per pixel — load the id, fetch the 3 global verts, recover PERSPECTIVE-CORRECT
  barycentrics from the clip-space positions, interpolate uv/normal/tangent/pos, compute UV gradients via QUAD wave
  ops (`QuadReadAcrossX/Y` — HW-equivalent ddx/ddy; dispatched 8×8 so the 2×2 quad is co-resident), then decode the
  material EXACTLY like GBufferBindless::PSMain using `SampleGrad` with those gradients, and write the fat G-buffer
  UAVs. Manual gradient solves the "no ddx/ddy in compute → wrong mip" problem.

## C# integration (the remaining work — each DRED + perceptual-parity gated, opt-in `BALLISTIC_DX12_VISBUFFER=1`)

1. **G-buffer typeless+UAV (load-bearing).** The resolve writes the color RTs as UAVs. RT0 is `_SRGB` → a UAV can't
   be SRGB. Make each color a TYPELESS resource (`R8G8B8A8_Typeless`, `R16G16B16A16_Typeless`, …) with
   `AllowRenderTarget | AllowUnorderedAccess`; create the RTV in the shaded format (SRGB for RT0), the SRV in the
   shaded format (lighting reads unchanged), and a UAV in the NON-SRGB UNORM format for the resolve write. This is
   the one change that touches the whole G-buffer — verify the raster path (R4/ExecuteIndirect) is byte-identical
   after the typeless switch BEFORE adding the resolve.
2. **Vis target:** an `RG32_UINT` render target + the same depth as the fat G-buffer (so depth-test still rejects
   occluded fragments and feeds Hi-Z). Cleared to `{0xFFFFFFFF, …}` (sky/no-hit sentinel).
3. **Vis raster PSO:** a mesh-shader PSO like R4's, but RTV format = RG32_UINT (single RT), PS = VisBuffer::PSMain.
   Reuse `Dx12MeshShaderPso.Create` with a one-element format array.
4. **Resolve compute PSO + pass:** rootsig = CBV b0 (ResolveCB) + root SRVs t0..t10 (PerDraws, GpuMaterials,
   Meshlets, Verts, Prims, Pos, Normal, UV, Tangent, VisId) + UAV u0..u4 (the 5 G-buffer UAVs) + static samplers
   s0 LinearWrap, and the directly-indexed flag (bindless material textures). Dispatch `(w+7)/8 × (h+7)/8`.
   Barriers: VisId RT→SRV, G-buffer UAVs write, then UAV→PixelShaderResource for lighting.
5. **Orchestration:** when `BALLISTIC_DX12_VISBUFFER=1` (+ meshlets available), the geometry pass runs the vis
   raster (whole-mesh) → resolve compute, instead of RenderInto/RenderIntoMeshlet. The CPU per-submesh / skinned /
   split paths still fill the fat G-buffer directly (they're not vis-buffered in v1) — so the resolve must NOT
   clobber pixels those wrote: either (a) vis-buffer ALL opaque geometry, or (b) run the resolve only where VisId
   != sentinel AND composite. v1: vis-buffer the whole-mesh set only; the resolve writes only hit pixels (it early-
   outs to sky-neutral on the sentinel), and the CPU paths draw AFTER into the same fat G-buffer — but that races
   the resolve's sky-neutral writes. CLEANEST v1: vis-buffer the whole-mesh set, resolve into the fat G-buffer
   FIRST, then the CPU/skinned/split paths draw on top (they win at their pixels). Depth-test keeps it correct.

## Hazards

- **SRGB-UAV** (#1) is the trap: writing SRGB through a UAV is illegal; the typeless+non-SRGB-UAV view is mandatory.
- **Perspective barycentric** must match the HW interpolation closely; expect the same sub-pixel raster tie-break
  as R3a/R4 (perceptual parity, not byte-identical) PLUS a possible mip-selection delta if the manual gradient
  diverges from HW. Gate on `bal imgdiff` meanError ~0 + hotspot ~0; if mip differs, tune the gradient (the quad
  ops should match HW ddx/ddy to <1 mip).
- **Quad gradient at triangle edges:** helper lanes across a silhouette read a different triangle's uv → a wrong
  gradient on the boundary pixel (1px). HW has the same artifact class; acceptable.
- **Hang class:** the resolve binds the bindless heap → heap-before-rootsig (the R2 hang). The G-buffer UAV
  barriers must be exact (the R3b BeforeAfterMismatch class).

## Test gate

- After step #1 (typeless G-buffer, no resolve yet): R4/ExecuteIndirect BYTE-IDENTICAL (the typeless switch alone
  changes nothing).
- Full R5 on Bistro: `bal imgdiff` meanError ~0, hotspot ~0 vs ExecuteIndirect (perceptual parity, same bar as
  R3a/R4). DRED-clean. GBV zero new. Default OFF; default path unchanged.

## BLOCKER found during integration (2026-06-20): per-draw vertex buffers

The resolve compute is ONE dispatch over the whole screen, but each pixel's triangle lives in a DIFFERENT mesh's
vertex buffers (Normal/UV/Tangent/Pos are separate `Dx12Buffer`s per mesh). A root SRV binds ONE buffer — so the
resolve can't fetch the right mesh's verts per pixel as written. VisResolve.hlsl assumes a single global vertex
stream. To make R5 correct, geometry needs ONE of:
- **Bindless vertex buffers**: add per-mesh Pos/Normal/UV/Tangent bindless SRV indices to PerDraw; the resolve does
  `ResourceDescriptorHeap[pd.PosIdx]` etc. Cleanest; ~the R2 bindless-material pattern extended to geometry.
- **Unified mega-buffer**: concatenate all mesh vertex streams into 4 big buffers + a per-draw base-vertex offset.
  Simpler shader, but a bigger upload/lifetime change.

This is an infrastructure change LARGER than R5's own passes — it's the real reason a visibility buffer is a big
commitment. **R5 is parked here:** shaders done (with the per-draw-bindless-geometry assumption to add), the
typeless+UAV G-buffer done + byte-identical, the pass skeleton (PSOs/target/rootsigs) done. The next step is the
bindless-geometry substrate, then wire the resolve. RenderVis (the vis raster draw loop) is done and reuses the
meshlet cull; it works as soon as the resolve can read geometry bindlessly.

## Done vs pending

- DONE: VisBuffer.hlsl (vis raster, reuses R4 cull), VisResolve.hlsl (perspective barycentric + QUAD-op manual mip
  gradient + verbatim GBufferBindless material decode). Both embed + build.
- PENDING: the C# — G-buffer typeless+UAV (#1, validate byte-identical first), vis target, vis PSO, resolve PSO +
  pass, orchestration. Each its own DRED/perceptual-parity-gated commit. The typeless G-buffer is the gate to the
  rest (do it + prove byte-identical before the resolve).
