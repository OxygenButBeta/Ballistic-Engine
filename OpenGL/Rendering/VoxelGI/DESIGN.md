# Voxel Cone Tracing GI (UE5/Lumen-class look) — DESIGN

GOAL: replace the flat baked-L1-SH ambient with rich, colored, multi-bounce dynamic GI — the
"Lumen look": shadowed recesses fill with bounced color, surfaces feel grounded. Current SSGI
contributes ~4/255 mean (nearly nothing); IBL ambient is flat. Big headroom.

WHY VXGI (not DDGI/SDF-trace): the engine is a rasterizer with GL 4.6 compute, static-ish scenes,
existing 3D-texture infra (GLTexture3D). Voxel cone tracing voxelizes the scene into a 3D radiance
texture, then cone-traces its mip pyramid for diffuse + glossy GI. It gives infinite-bounce colored
GI, scales (voxelize is amortized for static geo), and plugs into the forward shader's ambient hook.

## Pipeline
1. **Voxelize** (once for static scene, or on geometry change): rasterize the scene into a
   `RGBA8` (or R32UI atomic-accumulated) 3D texture, conservative-ish, storing albedo*directLight
   (sun + shadow + sky) per voxel = the "injected" first-bounce radiance. Resolution e.g. 128^3
   over the scene AABB.
2. **Mip / filter**: build the voxel mip chain (anisotropic ideally; isotropic to start) so cones
   can sample coarser radiance at distance.
3. **Cone trace** (in the forward lit pass or a screen pass): from each shaded pixel, trace ~6
   diffuse cones over the hemisphere (+ 1 specular cone along the reflection) through the voxel
   mips, accumulating radiance with front-to-back alpha. That's the indirect diffuse + glossy.
4. **Multi-bounce**: re-inject the traced radiance into the voxel grid next frame (cheap infinite
   bounce), or sample previous frame's voxel radiance during injection.

## Integration
- New folder `OpenGL/Rendering/VoxelGI/`: GLVoxelGI (owns the 3D textures + passes),
  Voxelize_*.glsl (geom/frag to scatter into the 3D texture via imageStore), VoxelMip_Comp.glsl,
  and the cone-trace added to the forward Frag.glsl ambient section (gated by a new `UseVoxelGI`).
- Opt-in `BALLISTIC_VOXELGI=1` first so I can A/B against the current ambient, screenshot, tune,
  then default-on once it clearly beats the flat look. The existing IrradianceVolume/SSGI stay as
  fallback when voxel GI is off.

## Verification (I judge it myself)
- A/B screenshots: voxel GI on vs off, Sun Temple interior + Bistro. Look for: colored bounce in
  shadows, grounded contact, no light leaking through walls, no flicker. Tune voxel res / cone
  count / GI intensity until it reads UE5-ish. Keep perf in budget (cone trace is the cost — start
  half-res + temporal if needed).
