# GPU-Driven Rendering Rework — Design

Goal: maximize FPS, zero quality loss. Render output byte-identical vs pre-rework.
Diagnosis (user's editor view): CPU 30.16ms vs GPU 12ms => 100% CPU-submit bound.
6070 draw calls + 6069 depth draws submitted one-by-one on the CPU. GPU idle 18ms.

## Strategy (multi-faceted, in order of impact)

1. **GPU-driven culling + MultiDrawIndirect** — collapse 6070 draws -> ~1-5.
   Compute shader does frustum (+later Hi-Z) cull on GPU, compacts visible submeshes
   into an indirect command buffer + count, drawn via glMultiDrawElementsIndirectCount.
2. **Bindless textures** — material textures live in an SSBO of uvec2 handles; the
   fragment shader selects by per-draw materialID, so DIFFERENT materials batch into
   ONE MDI call. Removes the per-draw texture-bind that forces a draw call boundary.
3. **Persistent triple-buffered buffers** — command/metadata SSBOs are persistently
   mapped (GL_MAP_PERSISTENT|COHERENT) and triple-buffered with fence sync, so per-frame
   writes never stall on the GPU still reading last frame's buffer.
4. **Hi-Z occlusion** (phase 2) — depth pyramid from the prepass; cull occluded submeshes.
5. **Auto-LOD** (phase 3) — import-time decimation; cull compute picks LOD by screen size.
6. **Shadow GPU-driven** (phase 4) — per-cascade cull compute + MDI for shadow casters.

## Scope: WHOLE-MESH renderer only (SubMeshIndex < 0), Bistro's ~1600-submesh mesh.
That single renderer accounts for ~all of the 6070 draws. Per-submesh/instanced/skinned
renderers keep the existing CPU path. Fallback: BALLISTIC_GPUDRIVEN=0 -> old path.

## Key facts from the existing engine (must replicate exactly)

- Mesh: ONE index buffer, submeshes = (IndexStart, IndexCount) ranges, baseVertex=0 always.
  One VAO via renderContext. Instance attribs at loc 4-7 (divisor 1).
- Per-submesh model matrix = InverseNodeTransforms[i] * WorldMatrix (whole-mesh; identity
  for merged-by-material). The renderer's ModelMatrix() helper.
- Vert.glsl computes gl_Position = projection * view * modelMatrix * pos. The GPU-driven
  path MUST produce the bit-identical matrix (z-prepass invariance, `invariant gl_Position`).
- PassData UBO at binding 0 holds view/projection + lights/shadows (std140). Reused as-is.
- Materials bind 6 sampler2D + flags via SetMaterialUniforms. Bindless replaces the samplers.
- Per-submesh world AABBs already computed each frame in ComputeSubmeshVisibility (CPU) and
  cached in wholeMeshSubmeshAabb. GPU-driven moves the frustum test to the GPU but reuses the
  per-submesh local bounds (Mesh.GetSubMeshBounds) uploaded once; world AABB derived in compute.

## SSBO bindings (avoid 0=PassData UBO, 1=bone SSBO)
- binding 2: SubmeshMeta[]  (readonly)  — per-submesh model mat4, localAABB min/max, indexStart, indexCount, materialID, lodBase...
- binding 3: DrawCommand[]  (write)     — DrawElementsIndirectCommand, compacted
- binding 4: DrawCount (atomic_uint / single uint)
- binding 5: PerDrawData[] (write)      — model matrix + materialID per compacted draw, indexed by gl_DrawID
- binding 6: MaterialTable[] (readonly) — bindless handles + factors, indexed by materialID
- binding 7: CullParams UBO/SSBO        — frustum planes, screen size, Hi-Z params

## gl_DrawID indexing
Compute writes commands densely (slot = atomicAdd). For each emitted command it ALSO writes
PerDrawData[slot] = { model, materialID }. The vertex shader indexes PerDrawData[gl_DrawID]
(core in 4.6 with MDI). Frag reads materialID -> MaterialTable for bindless samplers.

## Hi-Z occlusion (DONE — default ON, BALLISTIC_GPUDRIVEN_HIZ=0 to disable)

GLHiZPass builds a MAX-depth mip pyramid (HiZ_Down.glsl) from the previous frame's depth;
GpuCull_Comp's occludedByHiZ() projects each world AABB to screen via the pyramid's view-proj,
samples the MAX occluder over the footprint, and culls when the AABB's nearest LINEAR view distance
is behind it + a 0.25 m bias. A camera-delta gate disables it the frame after a big jump
(reprojection/hole safety). Shadows never use Hi-Z (camera depth is the wrong projection).

VERIFIED byte-identical (0% pixel diff): Sun Temple 1000->473 draws (-53%), Bistro ~814->719.
Two bugs found & fixed: (1) compare must be in LINEAR view distance (window depth bunches in
[0.96,1.0] near the far plane — direct window-depth compare is precision-hostile); the reconstruction
is |M43/(z_ndc + M33)|. (2) the pyramid BUILD leaked GL state (a stray color attachment + a texture
left bound on unit 0 + DepthTest/CullFace disabled) that corrupted the later passes (sky/SSGI/SSR) —
merely building it changed the image even when nothing was culled. GLHiZPass.Build now detaches +
restores fully.

## Verification
Anchor: e:/tmp/gpudriven/baseline.bmp (deterministic paused frame 120, 1920x1080).
Every phase: re-capture -> bal imgdiff vs baseline -> must be byte-identical (or <budget).
