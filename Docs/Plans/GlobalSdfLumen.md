# Global SDF for Lumen (SDF-GI) — design

## Why
The current SDF-GI bakes one tight brick **per submesh** and marches an instance grid. This works on
connected/whole-mesh scenes (SunTemple: raw gather mean 101) but FAILS on fragmented per-object
scenes (BistroInterior: ~2000 separate props → only a few hundred sparse bricks fit the budget →
rays escape → gather mean ~0). Proven by `BALLISTIC_SDFGI_DEBUG=1` raw-gather capture.

## Approach: one global, camera-centered scene SDF
Replace (behind an env/flag, A/B-able) the per-mesh bricks + instance grid with a **single 3D distance
field** covering a world box around the scene/camera. Baked by voxelizing ALL opaque triangles
(transformed to WORLD space) into one `TriangleBvh`, reusing `MeshSdfBaker`'s exact distance+sign math.
The march samples this ONE field directly — no instance loop, no per-object bricks, no 512 cap, no
fragmentation. This is the standard Lumen/clipmap global-SDF.

## Pieces
1. **`MeshSdfBaker.BakeWorldTriangles(Vector3[] worldVerts, ... bounds, res)`** — new entry that bakes a
   field over an explicit WORLD-space bounds from a flat world-triangle list (skip the per-submesh
   bounds fit; bounds are the scene box). Reuse `TriangleBvh` + the parallel grid loop verbatim.
2. **`GLGlobalSdf`** (OpenGL/Rendering/GI/) — owns one R16F 3D texture (e.g. 128³) + its world
   BoundsMin/CellSize. `EnsureBaked(opaque)`:
   - Gather all eligible opaque triangles to WORLD space (skip cutout/tiny like now), bounded by a
     world box (scene core bounds, reuse ComputeSceneFitBounds-style cluster fit).
   - Build BVH + bake on a BACKGROUND task (it's CPU; 128³ over a room is heavy — do NOT block the GL
     thread). Upload the float[] to the 3D texture when done. Re-bake only when geometry changes
     (stamp), like the probe cascade cache.
   - Keep the existing SURFACE-CACHE radiance atlas for the hit radiance (orthogonal — that already
     works; the global field only changes WHERE rays hit, not how radiance is read). For v1 the hit
     radiance can use the screen-space/IBL fallback (HitDirect) since the per-mesh radiance atlas slots
     don't exist in the global path; a global radiance grid is a follow-up.
3. **`SdfTrace_Comp.glsl`** — add a `GLOBAL_SDF` path: `SceneSdf` samples the one global 3D texture
   (hardware trilinear, world→grid-UVW) instead of the instance grid. Hit radiance via HitDirect
   (sun-at-hit + IBL) — coherent, no per-mesh radiance slot needed. Gate by a uniform so the per-mesh
   path stays available for A/B.

## Coordinate space
World point → `(worldP - GlobalMin) / GlobalCellSize` → grid UVW → `texture(GlobalSdf, uvw/res)`.
Stored signed distance in WORLD metres (negative inside). The march's sphere-trace stays identical
(advance by `max(d, MIN_STEP)`); just the SDF source changes.

## Risk / cost
- Bake cost: 128³ = 2M cells × BVH closest-tri over (say) 500k tris. Must be BACKGROUND + amortized;
  a first-bake stall would freeze the editor. Possibly bake at 64³ first (fast, coarse) then refine.
- Resolution vs room size: 128³ over a 25m room = ~0.2m cells — enough for occlusion, not fine detail.
- Memory: 128³ R16F = 4MB (fine). 256³ = 32MB.

## Verify
BistroInterior raw gather (`BALLISTIC_SDFGI_DEBUG=1`) must go from ~0 to a real value; SunTemple must
stay good (or improve); per-frame GPU cost acceptable; no first-frame stall (background bake).
