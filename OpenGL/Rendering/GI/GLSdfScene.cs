using System.Runtime.InteropServices;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace BallisticEngine.OpenGL.GI;

// DESIGN.md component 2 (P6.3). Builds the two SSBOs the SDF-GI march reads:
//
//   binding 8 = SdfInstance[]  — one per visible/opaque renderer with a bakeable mesh:
//               { mat4 worldToLocal; uint slot; uint p0,p1,p2; }
//   binding 9 = SdfSlotGpu[]    — the atlas slot table (offset/res/local bounds), indexed by `slot`.
//
// This class is a DUMB UPLOADER: the caller (GLSdfGiPass) decides which renderer maps to which
// atlas slot (via GLSdfAtlas), and passes (worldMatrix, slotIndex) pairs in. We only:
//   * compute worldToLocal = invert(world),
//   * raw-blit the instance + slot structs into std430 SSBOs (mirroring GpuDrivenRenderer's
//     MemoryMarshal.AsBytes / struct-array BufferData upload — OpenTK Matrix4/Vector4 blit straight
//     into std430 layout, verified bit-identical by the GPU-driven path),
//   * keep it allocation-light: scratch arrays + GPU buffers are reused; the GPU buffer is only
//     reallocated (BufferData) when the count outgrows capacity, otherwise BufferSubData in place.
//
// SSBO bindings 2-7 (GpuDriven) and UBO 0 (PassData) are taken — DESIGN.md assigns 8/9 here.
public sealed class GLSdfScene : IDisposable {
    public const int InstanceBinding = 8;
    public const int SlotTableBinding = 9;

    // binding 8 entry. std430: mat4 (64B) + 4 vec4 (64B) + 4 uints (16B) = 144B, multiple of 16.
    // The march pre-rejects by WorldAabbMin/Max (cheap, before the transform), then transforms a
    // world point into mesh-local space with WorldToLocal and looks up SdfSlotGpu[Slot] to sample.
    // Albedo/Emissive feed the surface-cache radiance inject (the lit radiance at this surface).
    [StructLayout(LayoutKind.Sequential)]
    public struct SdfInstance {
        public Matrix4 WorldToLocal; // 64B — inverse(world)
        public Vector4 WorldAabbMin; // 16B — instance world-space AABB min (xyz; w unused)
        public Vector4 WorldAabbMax; // 16B — instance world-space AABB max
        public Vector4 Albedo;       // 16B — diffuse albedo (xyz; w unused) for the inject
        public Vector4 Emissive;     // 16B — emissive radiance (xyz; w unused) for the inject
        public uint Slot;            // index into the slot table (binding 9)
        public uint Pad0;
        public uint Pad1;
        public uint Pad2;            // pad to 144B (multiple of 16)

        public const int SizeBytes = 144;
    }

    // binding 9 entry — mirrors GLSdfAtlas.SdfSlot for the GPU. Kept dead simple: all vec4 so the
    // std430 layout has no surprises (every member 16-aligned, no implicit padding to reason about).
    //   AtlasOffset.xyz = texel offset of the slot's origin in the atlas 3D texture (w unused).
    //   AtlasRes.xyz    = the slot's grid resolution in texels (w unused).
    //   BoundsMin/Max   = the MeshSdf field bounds in MESH-LOCAL space (matches MeshSdf.BoundsMin/Max).
    // The march maps a local point -> [0,1] via (local-BoundsMin)/(BoundsMax-BoundsMin), then to
    // atlas texels via AtlasOffset + uvw*AtlasRes, and samples the R16F atlas (manual trilinear).
    [StructLayout(LayoutKind.Sequential)]
    public struct SdfSlotGpu {
        public Vector4 AtlasOffset; // xyz = texel offset, w unused — 16B
        public Vector4 AtlasRes;    // xyz = texel resolution, w unused — 16B
        public Vector4 BoundsMin;   // xyz = mesh-local min, w unused — 16B
        public Vector4 BoundsMax;   // xyz = mesh-local max, w unused — 16B

        // 4 * 16 = 64 bytes.
        public const int SizeBytes = 64;

        public SdfSlotGpu(Vector3i atlasOffset, Vector3i res, Vector3 boundsMin, Vector3 boundsMax) {
            AtlasOffset = new Vector4(atlasOffset.X, atlasOffset.Y, atlasOffset.Z, 0f);
            AtlasRes = new Vector4(res.X, res.Y, res.Z, 0f);
            BoundsMin = new Vector4(boundsMin, 0f);
            BoundsMax = new Vector4(boundsMax, 0f);
        }
    }

    // The atlas exposes its slot table through this seam so GLSdfScene stays decoupled from the
    // (parallel-built) GLSdfAtlas concrete type and from any GL atlas details. GLSdfAtlas implements
    // it: SlotCount = number of packed meshes; SlotAt(i) = that slot's offset/res/bounds. The caller
    // passes slot INDICES into Build that index this same table.
    public interface ISdfAtlas {
        int SlotCount { get; }
        SdfSlotGpu SlotAt(int slot);
    }

    // ---- Uniform spatial grid over the instances (binding 10 = cell ranges, 11 = flattened
    // instance indices). The march maps a world point to a cell and loops ONLY that cell's
    // instances, cutting the per-step iteration from all-N to the handful overlapping the cell —
    // the perf fix that makes hundreds of per-submesh fields affordable.
    public const int GridCellBinding = 10;
    public const int GridListBinding = 11;
    const int GridRes = 32;             // 32^3 cells
    const int GridCellCount = GridRes * GridRes * GridRes;

    int instanceBuffer;     // SdfInstance[]  (binding 8)
    int slotBuffer;         // SdfSlotGpu[]   (binding 9)
    int gridCellBuffer;     // ivec2 (start,count) per cell  (binding 10)
    int gridListBuffer;     // uint flattened instance indices, grouped by cell  (binding 11)
    int instanceCapacity;   // SdfInstance entries the instanceBuffer is sized for
    int slotCapacity;       // SdfSlotGpu entries the slotBuffer is sized for
    int gridListCapacity;   // uint entries the gridListBuffer is sized for

    SdfInstance[] instanceScratch = [];
    SdfSlotGpu[] slotScratch = [];
    // Grid CPU scratch: per-cell (start,count) packed as 2 ints, and the flattened index list.
    readonly int[] cellStart = new int[GridCellCount];
    readonly int[] cellCount = new int[GridCellCount];
    int[] cellRangesPacked = new int[GridCellCount * 2]; // (start,count) per cell, uploaded
    uint[] gridList = [];

    bool initialized;

    public int InstanceCount { get; private set; }
    public int SlotCount { get; private set; }

    // Grid bounds + inverse cell size the march needs to map a world point to a cell.
    public Vector3 GridMin { get; private set; }
    public Vector3 GridInvCell { get; private set; }   // 1 / cellSize per axis
    public int GridResolution => GridRes;

    void EnsureBuffers() {
        if (initialized)
            return;
        instanceBuffer = GL.GenBuffer();
        slotBuffer = GL.GenBuffer();
        gridCellBuffer = GL.GenBuffer();
        gridListBuffer = GL.GenBuffer();
        initialized = true;
    }

    // Rebuilds both SSBOs from the caller's (world, slot) instance list and the atlas slot table.
    // Call once per frame (or only when the renderer set / transforms / atlas change). Allocation-
    // light: scratch + GPU buffers are reused; GPU storage is reallocated only when the count grows.
    public void Build(IEnumerable<(Matrix4 world, int slot, Vector3 albedo, Vector3 emissive)> instances,
        ISdfAtlas atlas) {
        EnsureBuffers();

        // ---- Instances (binding 8) ----
        int count = instances is ICollection<(Matrix4, int, Vector3, Vector3)> c ? c.Count : 0;
        if (instanceScratch.Length < Math.Max(count, 1))
            instanceScratch = new SdfInstance[Math.Max(count, 16)];

        int n = 0;
        foreach (var (world, slot, albedo, emissive) in instances) {
            if (n >= instanceScratch.Length)
                Array.Resize(ref instanceScratch, instanceScratch.Length * 2);
            // worldToLocal: Matrix4.Invert returns the inverse (throws only on a singular matrix,
            // which a valid TRS world matrix never is).
            Matrix4 worldToLocal = Matrix4.Invert(world);
            // World-space AABB of this instance's brick, for the march's cheap pre-reject: transform
            // the 8 corners of the slot's mesh-local bounds by `world` and take the extents. Computed
            // once per frame here so the per-step march only does a 6-compare box test.
            int s = Math.Max(slot, 0);
            SdfSlotGpu sl = s < (atlas?.SlotCount ?? 0) ? atlas!.SlotAt(s) : default;
            WorldAabb(world, sl.BoundsMin.Xyz, sl.BoundsMax.Xyz, out Vector3 wMin, out Vector3 wMax);
            instanceScratch[n] = new SdfInstance {
                WorldToLocal = worldToLocal,
                WorldAabbMin = new Vector4(wMin, 0f),
                WorldAabbMax = new Vector4(wMax, 0f),
                Albedo = new Vector4(albedo, 0f),
                Emissive = new Vector4(emissive, 0f),
                Slot = (uint)s,
                Pad0 = 0, Pad1 = 0, Pad2 = 0,
            };
            n++;
        }
        InstanceCount = n;
        UploadStructs(instanceBuffer, instanceScratch, n, SdfInstance.SizeBytes, ref instanceCapacity);

        // ---- Slot table (binding 9) ----
        int slots = atlas?.SlotCount ?? 0;
        if (slotScratch.Length < Math.Max(slots, 1))
            slotScratch = new SdfSlotGpu[Math.Max(slots, 8)];
        for (var i = 0; i < slots; i++)
            slotScratch[i] = atlas!.SlotAt(i);
        SlotCount = slots;
        UploadStructs(slotBuffer, slotScratch, slots, SdfSlotGpu.SizeBytes, ref slotCapacity);

        // ---- Spatial grid (bindings 10/11) ----
        BuildGrid(n);
    }

    // Bins the `n` instances (their world AABBs are already in instanceScratch) into a GridRes^3
    // uniform grid, so the march loops only the instances overlapping the current cell. Two-pass
    // counting sort: count per cell -> prefix-sum to cell starts -> scatter instance indices.
    void BuildGrid(int n) {
        // Grid bounds = union of all instance world AABBs (a little padding so edge cells are safe).
        Vector3 gmin = new(float.MaxValue), gmax = new(float.MinValue);
        for (int i = 0; i < n; i++) {
            gmin = Vector3.ComponentMin(gmin, instanceScratch[i].WorldAabbMin.Xyz);
            gmax = Vector3.ComponentMax(gmax, instanceScratch[i].WorldAabbMax.Xyz);
        }
        if (n == 0) { gmin = Vector3.Zero; gmax = Vector3.One; }
        Vector3 ext = Vector3.ComponentMax(gmax - gmin, new Vector3(1e-3f));
        GridMin = gmin;
        Vector3 cellSize = ext / GridRes;
        GridInvCell = new Vector3(1f / cellSize.X, 1f / cellSize.Y, 1f / cellSize.Z);

        Array.Clear(cellCount, 0, GridCellCount);

        // Pass 1: count how many (instance, cell) pairs land in each cell.
        long totalPairs = 0;
        for (int i = 0; i < n; i++) {
            CellRange(instanceScratch[i], out int x0, out int y0, out int z0, out int x1, out int y1, out int z1);
            for (int z = z0; z <= z1; z++)
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++) {
                        cellCount[CellIndex(x, y, z)]++;
                        totalPairs++;
                    }
        }

        // Prefix-sum -> cell starts; pack (start,count) for upload.
        if (cellRangesPacked.Length < GridCellCount * 2)
            cellRangesPacked = new int[GridCellCount * 2];
        int running = 0;
        for (int c = 0; c < GridCellCount; c++) {
            cellStart[c] = running;
            cellRangesPacked[c * 2 + 0] = running;
            cellRangesPacked[c * 2 + 1] = cellCount[c];
            running += cellCount[c];
        }

        // Pass 2: scatter instance indices into the flattened list at each cell's write cursor.
        // Reuse cellCount as the per-cell write cursor, seeded from cellStart (cellCount's counts
        // were already folded into cellRangesPacked above, so it's free scratch now).
        // Size the list to EXACTLY totalPairs (clamped to int — guards the cast-overflow that left a
        // tiny array and crashed pass 2 when one frame's pairs were huge).
        int listLen = (int)Math.Min(totalPairs, int.MaxValue);
        if (gridList.Length < Math.Max(listLen, 1))
            gridList = new uint[Math.Max(listLen, 16)];
        Array.Copy(cellStart, cellCount, GridCellCount);
        for (int i = 0; i < n; i++) {
            CellRange(instanceScratch[i], out int x0, out int y0, out int z0, out int x1, out int y1, out int z1);
            for (int z = z0; z <= z1; z++)
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++) {
                        int c = CellIndex(x, y, z);
                        int w = cellCount[c]++;
                        if ((uint)w < (uint)gridList.Length)   // defensive: never write OOB
                            gridList[w] = (uint)i;
                    }
        }

        // Upload: cell ranges (binding 10, fixed GridCellCount*2 ints) + the flattened index list
        // (binding 11). Both blit straight into std430 (int/uint are 4B, tightly packed).
        UploadStructs(gridCellBuffer, cellRangesPacked, GridCellCount * 2, sizeof(int), ref gridCellCapacity);
        UploadStructs(gridListBuffer, gridList, (int)totalPairs, sizeof(uint), ref gridListCapacity);
    }

    int gridCellCapacity;

    void CellRange(in SdfInstance inst, out int x0, out int y0, out int z0, out int x1, out int y1, out int z1) {
        Vector3 lo = (inst.WorldAabbMin.Xyz - GridMin) * GridInvCell;
        Vector3 hi = (inst.WorldAabbMax.Xyz - GridMin) * GridInvCell;
        x0 = Math.Clamp((int)MathF.Floor(lo.X), 0, GridRes - 1);
        y0 = Math.Clamp((int)MathF.Floor(lo.Y), 0, GridRes - 1);
        z0 = Math.Clamp((int)MathF.Floor(lo.Z), 0, GridRes - 1);
        x1 = Math.Clamp((int)MathF.Floor(hi.X), 0, GridRes - 1);
        y1 = Math.Clamp((int)MathF.Floor(hi.Y), 0, GridRes - 1);
        z1 = Math.Clamp((int)MathF.Floor(hi.Z), 0, GridRes - 1);
    }

    static int CellIndex(int x, int y, int z) => x + GridRes * (y + GridRes * z);

    // World-space AABB of a local box under a transform: the 8 transformed corners' extents. (A
    // rotation-aware AABB — looser than oriented bounds but correct and cheap, computed once/frame.)
    static void WorldAabb(Matrix4 world, Vector3 lmin, Vector3 lmax, out Vector3 wmin, out Vector3 wmax) {
        wmin = new Vector3(float.MaxValue);
        wmax = new Vector3(float.MinValue);
        for (int i = 0; i < 8; i++) {
            var corner = new Vector3(
                (i & 1) == 0 ? lmin.X : lmax.X,
                (i & 2) == 0 ? lmin.Y : lmax.Y,
                (i & 4) == 0 ? lmin.Z : lmax.Z);
            Vector3 w = (new Vector4(corner, 1f) * world).Xyz;
            wmin = Vector3.ComponentMin(wmin, w);
            wmax = Vector3.ComponentMax(wmax, w);
        }
    }

    // Uploads the first `count` structs from `data` into `buffer`. Reallocates (BufferData) only when
    // the count outgrows `capacity`; otherwise BufferSubData in place. The struct array blits straight
    // into std430 (Sequential layout, Matrix4/Vector4 are 16-aligned) — same upload the GPU-driven
    // SubmeshMeta path uses (verified bit-identical there).
    static void UploadStructs<T>(int buffer, T[] data, int count, int stride, ref int capacity)
        where T : unmanaged {
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, buffer);
        if (count > capacity) {
            int cap = Math.Max(count, Math.Max(capacity * 2, 16));
            GL.BufferData(BufferTarget.ShaderStorageBuffer, cap * stride, IntPtr.Zero,
                BufferUsageHint.DynamicDraw);
            capacity = cap;
        }
        if (count > 0) {
            // MemoryMarshal.AsBytes mirrors GpuDrivenRenderer's struct upload — explicit byte view so
            // the partial (first-`count`) range uploads even when the scratch array is larger.
            ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(data.AsSpan(0, count));
            GL.BufferSubData(BufferTarget.ShaderStorageBuffer, IntPtr.Zero, count * stride,
                ref MemoryMarshal.GetReference(bytes));
        }
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, 0);
    }

    // Binds all four SSBOs at their fixed bindings (8 instances, 9 slot table, 10 grid cells,
    // 11 grid index list) for the march compute.
    public void Bind() {
        if (!initialized)
            return;
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, InstanceBinding, instanceBuffer);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, SlotTableBinding, slotBuffer);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, GridCellBinding, gridCellBuffer);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, GridListBinding, gridListBuffer);
    }

    public void Dispose() {
        if (instanceBuffer != 0) GL.DeleteBuffer(instanceBuffer);
        if (slotBuffer != 0) GL.DeleteBuffer(slotBuffer);
        if (gridCellBuffer != 0) GL.DeleteBuffer(gridCellBuffer);
        if (gridListBuffer != 0) GL.DeleteBuffer(gridListBuffer);
        instanceBuffer = 0;
        slotBuffer = 0;
        gridCellBuffer = 0;
        gridListBuffer = 0;
        initialized = false;
        InstanceCount = 0;
        SlotCount = 0;
    }
}
