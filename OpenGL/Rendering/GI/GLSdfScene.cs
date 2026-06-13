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

    // binding 8 entry. std430: mat4 (64B, 16-aligned) + 4 uints (16B) = 80B, multiple of 16.
    // The march transforms a world-space point into mesh-local space with WorldToLocal, then
    // looks up SdfSlotGpu[Slot] to find the atlas sub-volume to sample.
    [StructLayout(LayoutKind.Sequential)]
    public struct SdfInstance {
        public Matrix4 WorldToLocal; // 64B — inverse(world)
        public uint Slot;            // index into the slot table (binding 9)
        public uint Pad0;
        public uint Pad1;
        public uint Pad2;            // pad to 80B (multiple of 16)

        public const int SizeBytes = 80;
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

    int instanceBuffer;     // SdfInstance[]  (binding 8)
    int slotBuffer;         // SdfSlotGpu[]   (binding 9)
    int instanceCapacity;   // SdfInstance entries the instanceBuffer is sized for
    int slotCapacity;       // SdfSlotGpu entries the slotBuffer is sized for

    SdfInstance[] instanceScratch = [];
    SdfSlotGpu[] slotScratch = [];

    bool initialized;

    public int InstanceCount { get; private set; }
    public int SlotCount { get; private set; }

    void EnsureBuffers() {
        if (initialized)
            return;
        instanceBuffer = GL.GenBuffer();
        slotBuffer = GL.GenBuffer();
        initialized = true;
    }

    // Rebuilds both SSBOs from the caller's (world, slot) instance list and the atlas slot table.
    // Call once per frame (or only when the renderer set / transforms / atlas change). Allocation-
    // light: scratch + GPU buffers are reused; GPU storage is reallocated only when the count grows.
    public void Build(IEnumerable<(Matrix4 world, int slot)> instances, ISdfAtlas atlas) {
        EnsureBuffers();

        // ---- Instances (binding 8) ----
        int count = instances is ICollection<(Matrix4, int)> c ? c.Count : 0;
        if (instanceScratch.Length < Math.Max(count, 1))
            instanceScratch = new SdfInstance[Math.Max(count, 16)];

        int n = 0;
        foreach (var (world, slot) in instances) {
            if (n >= instanceScratch.Length)
                Array.Resize(ref instanceScratch, instanceScratch.Length * 2);
            // worldToLocal: Matrix4.Invert returns the inverse (throws only on a singular matrix,
            // which a valid TRS world matrix never is).
            Matrix4 worldToLocal = Matrix4.Invert(world);
            instanceScratch[n] = new SdfInstance {
                WorldToLocal = worldToLocal,
                Slot = (uint)Math.Max(slot, 0),
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

    // Binds both SSBOs at their fixed bindings (8 = instances, 9 = slot table) for the march compute.
    public void Bind() {
        if (!initialized)
            return;
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, InstanceBinding, instanceBuffer);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, SlotTableBinding, slotBuffer);
    }

    public void Dispose() {
        if (instanceBuffer != 0) GL.DeleteBuffer(instanceBuffer);
        if (slotBuffer != 0) GL.DeleteBuffer(slotBuffer);
        instanceBuffer = 0;
        slotBuffer = 0;
        initialized = false;
        InstanceCount = 0;
        SlotCount = 0;
    }
}
