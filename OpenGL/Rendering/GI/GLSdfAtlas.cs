using System;
using System.Collections.Generic;
using OpenTK.Graphics.OpenGL;
using OpenTK.Mathematics;
using BallisticEngine.GI;

namespace BallisticEngine.OpenGL.GI;

// Component 1 of the SDF World-Space GI subsystem (DESIGN.md). Owns ONE R16F 3D texture and
// packs each distinct mesh's baked MeshSdf grid into it as an axis-aligned sub-volume via a
// simple 3D shelf/row allocator. The march reads the atlas + the per-slot table (uploaded by
// GLSdfScene into its own SSBO from the public Slots list here).
//
// The CPU keeps a slot table so callers can build the GPU instance/slot SSBOs without re-reading
// the texture. Distances upload as float into the R16F target — the driver narrows to half; we do
// NOT pre-convert (avoids a half-float dependency and the conversion is lossless enough for SDFs).
//
// Layering: OpenGL/ may use GL + Engine/Abstraction types. MeshSdf lives in BallisticEngine.GI
// (Abstraction). No asset I/O, no Assimp/Stb/Magick here.
public sealed class GLSdfAtlas : IDisposable {
    // One packed mesh-SDF sub-volume inside the atlas.
    public struct SdfSlot {
        // Texel offset of the sub-volume's (0,0,0) corner inside the atlas texture.
        public Vector3i AtlasOffset;
        // Sub-volume size in texels (== the MeshSdf.Res that was packed).
        public Vector3i Res;
        // Mesh-local bounds of the field (MeshSdf.BoundsMin/Max), so the march can map a
        // local-space point into [0,Res) texel coordinates of this slot.
        public Vector3 BoundsMin;
        public Vector3 BoundsMax;
    }

    // Atlas cube edge length in texels. 256^3 R16F = 32 MB.
    public const int DefaultSize = 256;

    public int TextureId { get; private set; }
    // Parallel RGBA16F radiance volume — the SURFACE CACHE. Same dimensions + slot layout as the SDF
    // atlas, so a brick's voxel (offset+local) addresses the SAME texel in both: the radiance-inject
    // compute WRITES each near-surface voxel's lit radiance here, and the march READS it at a hit
    // (stable per-surface radiance, no per-pixel screen-reprojection flicker). rgb = radiance,
    // a = occupancy/confidence (0 = empty voxel, >0 = a surface voxel with valid radiance).
    //
    // PING-PONG: there are TWO radiance volumes. The inject READS last frame's converged radiance
    // (RadianceReadTextureId) via a sampler and WRITES this frame's into RadianceWriteTextureId via
    // an image — never the same texture, so the bounce-gather is a clean one-bounce-per-frame
    // radiosity iteration with NO same-frame read-during-write (the noise/instability source). After
    // the inject, SwapRadiance() flips them so the march (and next frame) read the fresh result.
    public int RadianceReadTextureId => radianceTextures[radianceRead];
    public int RadianceWriteTextureId => radianceTextures[1 - radianceRead];
    // Back-compat alias used where "the current readable radiance" is meant (march sampler binding).
    public int RadianceTextureId => RadianceReadTextureId;
    public int Size { get; }

    public IReadOnlyList<SdfSlot> Slots => slots;

    // The highest Z (depth) any packed brick reaches in the atlas — the shelf allocator fills from
    // Z=0 up, so [0, UsedDepth) bounds ALL bricks. Lets the ping-pong copy only the used sub-volume
    // (Size x Size x UsedDepth) instead of the whole Size^3 (most of the 256^3 atlas is empty).
    public int UsedDepth { get; private set; }

    readonly List<SdfSlot> slots = new();

    // The two ping-pong radiance volumes. radianceRead indexes the one holding last frame's
    // converged radiance (read by the inject's gather + the march); the other is written this frame.
    readonly int[] radianceTextures = new int[2];
    int radianceRead;

    // 3D shelf cursor. Sub-volumes lay out in rows along +X; a full row stacks along +Y to form a
    // "layer"; full layers stack along +Z. shelfHeight/depth track the current row/layer extents
    // so the next row/layer starts past the tallest/deepest member placed so far.
    int cursorX, cursorY, cursorZ;
    int rowMaxY;   // max Res.Y of any slot in the current row (advance Y by this on row break)
    int layerMaxZ; // max Res.Z of any slot in the current layer (advance Z by this on layer break)

    bool disposed;

    public GLSdfAtlas(int size = DefaultSize) {
        Size = size;
        TextureId = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture3D, TextureId);
        GL.TexStorage3D(TextureTarget3d.Texture3D, 1, SizedInternalFormat.R16f, size, size, size);
        // Linear so the march can read the atlas hardware-filtered (or do manual trilinear in
        // texel space — either way ClampToEdge keeps an out-of-brick fetch on the boundary cell).
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMinFilter,
            (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMagFilter,
            (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapS,
            (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapT,
            (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapR,
            (int)TextureWrapMode.ClampToEdge);
        GL.BindTexture(TextureTarget.Texture3D, 0);

        // The TWO parallel radiance volumes (surface cache ping-pong). RGBA16F, same size + slot
        // layout. Linear so the march reads them hardware-filtered (smooth per-surface radiance).
        // The inject binds one as an image (write) and samples the other (last frame's read); cleared
        // to 0 on creation by TexStorage's spec (driver-zeroed) — confirmed below with an explicit
        // clear so a first-frame read can't pick up garbage.
        for (var i = 0; i < 2; i++)
            radianceTextures[i] = CreateRadianceVolume(size);
    }

    static int CreateRadianceVolume(int size) {
        int tex = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture3D, tex);
        GL.TexStorage3D(TextureTarget3d.Texture3D, 1, SizedInternalFormat.Rgba16f, size, size, size);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMinFilter,
            (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMagFilter,
            (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapS,
            (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapT,
            (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapR,
            (int)TextureWrapMode.ClampToEdge);
        // Explicit zero-clear (don't rely on driver-zeroed storage for a texture the inject reads
        // before it has written a full frame).
        GL.ClearTexImage(tex, 0, PixelFormat.Rgba, PixelType.Float, IntPtr.Zero);
        GL.BindTexture(TextureTarget.Texture3D, 0);
        return tex;
    }

    // Flip the ping-pong: the volume just written becomes the readable one. Called once per frame
    // after the radiance inject, before the march reads RadianceTextureId.
    public void SwapRadiance() => radianceRead = 1 - radianceRead;

    // Packs and uploads one mesh's SDF. Returns false (logging via Debugging.Log — NO silent
    // truncation) when the sub-volume does not fit in the remaining atlas space, or when the SDF
    // itself is larger than the whole atlas. MUST be called on the GL thread (it issues GL calls);
    // bake the MeshSdf off-thread, then call this. slotIndex indexes Slots.
    public bool TryAdd(MeshSdf sdf, out int slotIndex) {
        slotIndex = -1;
        if (disposed || TextureId == 0) {
            Debugging.Log("[GLSdfAtlas] TryAdd on a disposed/uninitialized atlas.");
            return false;
        }
        if (sdf == null) {
            Debugging.Log("[GLSdfAtlas] TryAdd given a null MeshSdf.");
            return false;
        }

        Vector3i res = sdf.Res;
        if (res.X <= 0 || res.Y <= 0 || res.Z <= 0) {
            Debugging.Log($"[GLSdfAtlas] MeshSdf has a degenerate resolution {res}; skipped.");
            return false;
        }
        if (res.X > Size || res.Y > Size || res.Z > Size) {
            Debugging.Log($"[GLSdfAtlas] MeshSdf res {res} exceeds atlas size {Size}^3; skipped " +
                          "(re-bake with a smaller MaxResolution).");
            return false;
        }
        long expected = (long)res.X * res.Y * res.Z;
        if (sdf.Distances == null || sdf.Distances.Length != expected) {
            Debugging.Log($"[GLSdfAtlas] MeshSdf distance count {sdf.Distances?.Length ?? 0} != " +
                          $"{expected} for res {res}; skipped.");
            return false;
        }

        if (!TryReserve(res, out Vector3i offset)) {
            Debugging.Log($"[GLSdfAtlas] atlas full: cannot fit res {res} (atlas {Size}^3, " +
                          $"{slots.Count} slots packed); skipped — no truncation.");
            return false;
        }

        GL.BindTexture(TextureTarget.Texture3D, TextureId);
        // x-fastest float source matches MeshSdf.Index = x + Res.X*(y + Res.Y*z); R16F target
        // narrows on upload (PixelFormat.Red, PixelType.Float).
        GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        GL.TexSubImage3D(TextureTarget.Texture3D, 0,
            offset.X, offset.Y, offset.Z,
            res.X, res.Y, res.Z,
            PixelFormat.Red, PixelType.Float, sdf.Distances);
        GL.BindTexture(TextureTarget.Texture3D, 0);

        slotIndex = slots.Count;
        slots.Add(new SdfSlot {
            AtlasOffset = offset,
            Res = res,
            BoundsMin = sdf.BoundsMin,
            BoundsMax = sdf.BoundsMax,
        });
        return true;
    }

    // Reserves a res-sized sub-box from the shelf allocator. Rows grow +X; on overflow the row
    // breaks to +Y; on layer overflow it breaks to +Z; failure when the next layer would exceed
    // the atlas depth. No defragmentation — Clear() resets between rebuilds.
    bool TryReserve(Vector3i res, out Vector3i offset) {
        offset = default;

        // Break the current row if this sub-volume runs past the atlas width.
        if (cursorX + res.X > Size) {
            cursorX = 0;
            cursorY += rowMaxY;
            rowMaxY = 0;
        }
        // Break the current layer if the new row runs past the atlas height.
        if (cursorY + res.Y > Size) {
            cursorY = 0;
            cursorX = 0;
            rowMaxY = 0;
            cursorZ += layerMaxZ;
            layerMaxZ = 0;
        }
        // Out of depth: the atlas is full.
        if (cursorZ + res.Z > Size)
            return false;

        offset = new Vector3i(cursorX, cursorY, cursorZ);
        cursorX += res.X;
        if (res.Y > rowMaxY) rowMaxY = res.Y;
        if (res.Z > layerMaxZ) layerMaxZ = res.Z;
        // Track the deepest brick extent so the ping-pong copy can skip the empty tail of the atlas.
        if (cursorZ + res.Z > UsedDepth) UsedDepth = cursorZ + res.Z;
        return true;
    }

    // Drops all packed slots and resets the allocator. The texture is kept (reused for the next
    // pack); stale texels are simply unreferenced. Call when the renderer set changes wholesale.
    public void Clear() {
        slots.Clear();
        cursorX = cursorY = cursorZ = 0;
        rowMaxY = layerMaxZ = 0;
        UsedDepth = 0;
    }

    public void Dispose() {
        if (disposed) return;
        disposed = true;
        if (TextureId != 0) {
            GL.DeleteTexture(TextureId);
            TextureId = 0;
        }
        for (var i = 0; i < radianceTextures.Length; i++) {
            if (radianceTextures[i] != 0) {
                GL.DeleteTexture(radianceTextures[i]);
                radianceTextures[i] = 0;
            }
        }
        slots.Clear();
    }
}
