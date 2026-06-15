using System;
using System.Numerics;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace BallisticEngine.DX12;

// Clustered (froxel) punctual-light culling for the DX12 deferred renderer — a faithful port of the GL
// GLClusteredLights design (same 16x9x24 log-Z froxel grid, same 64-byte GpuLight, same per-cluster
// {offset,count} grid + flat light-index list, same sphere-vs-AABB cull). The deferred lighting shader
// reads the three structured buffers and shades each pixel's cluster's lights.
//
// The cull runs on the CPU here (≤1024 lights × 3456 clusters is cheap, and it keeps the shader contract
// identical to a future GPU compute cull — that's a perf swap, not a redesign). The cluster view-space
// AABBs are rebuilt only when the projection/viewport changes (camera-translation-invariant, GL parity).
//
// All three GPU buffers are UPLOAD-heap, persistently mapped, rewritten each frame (light set + culling
// change per frame). Each has a persistent SRV in Dx12Backend.SrvStore the renderer copies per frame.
public sealed class Dx12ClusteredLights : IDisposable {
    public const int ClusterX = 16, ClusterY = 9, ClusterZ = 24;
    public const int ClusterCount = ClusterX * ClusterY * ClusterZ;   // 3456
    public const int MaxLights = 1024;
    public const int MaxLightsPerCluster = 128;
    public const int MaxLightIndices = ClusterCount * 32;             // 110,592

    // 64-byte GPU light record, byte-identical to the GL GpuLight (StructuredBuffer<GpuLight> in HLSL).
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public struct GpuLight {
        public Vector4 PosRange;     // xyz world pos, w range
        public Vector4 Color;        // xyz radiance (HDR, NOT pre-exposed — composite meters it), w type (0 point/1 spot)
        public Vector4 DirCosOuter;  // xyz spot dir (world), w cosOuter
        public Vector4 Extra;        // x cosInner, y shadowSlot(-1), z sourceRadius, w pad
    }
    public const int GpuLightBytes = 64;

    readonly Dx12Device dev;

    // GPU buffers (upload heap, persistently mapped).
    ID3D12Resource lightBuf;   unsafe byte* lightMapped;   int lightSrv = -1;
    ID3D12Resource gridBuf;    unsafe byte* gridMapped;    int gridSrv = -1;   // int2 {offset,count} per cluster
    ID3D12Resource indexBuf;   unsafe byte* indexMapped;   int indexSrv = -1;  // flat uint light-index list

    // CPU scratch.
    readonly GpuLight[] lights = new GpuLight[MaxLights];
    int lightCount;
    readonly Vector3[] lightViewPos = new Vector3[MaxLights];   // light center in view space (for the cull)
    readonly float[] lightRange = new float[MaxLights];
    // Per-cluster view-space AABB (min,max), rebuilt on proj/viewport change.
    readonly Vector3[] clusterMin = new Vector3[ClusterCount];
    readonly Vector3[] clusterMax = new Vector3[ClusterCount];
    bool clustersBuilt;
    Matrix4x4 builtProj; int builtW, builtH;

    public int LightCount => lightCount;
    public CpuDescriptorHandle LightSrvCpu => Dx12Backend.SrvStore.Cpu(lightSrv);
    public CpuDescriptorHandle GridSrvCpu => Dx12Backend.SrvStore.Cpu(gridSrv);
    public CpuDescriptorHandle IndexSrvCpu => Dx12Backend.SrvStore.Cpu(indexSrv);

    public unsafe Dx12ClusteredLights(Dx12Device device) {
        dev = device;
        lightBuf = MakeUpload((ulong)(MaxLights * GpuLightBytes), out lightMapped);
        gridBuf = MakeUpload((ulong)(ClusterCount * 2 * sizeof(int)), out gridMapped);
        indexBuf = MakeUpload((ulong)(MaxLightIndices * sizeof(uint)), out indexMapped);

        // SRVs: light = StructuredBuffer (64B stride); grid = Buffer<int2> (R32G32_SInt elements);
        // index = Buffer<uint> (R32_UInt). All in the persistent CPU SRV store.
        lightSrv = MakeStructuredSrv(lightBuf, MaxLights, GpuLightBytes);
        gridSrv = MakeTypedSrv(gridBuf, ClusterCount, Format.R32G32_SInt);
        indexSrv = MakeTypedSrv(indexBuf, MaxLightIndices, Format.R32_UInt);
    }

    unsafe ID3D12Resource MakeUpload(ulong bytes, out byte* mapped) {
        var r = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer(bytes), ResourceStates.GenericRead);
        mapped = r.Map<byte>(0);
        return r;
    }

    int MakeStructuredSrv(ID3D12Resource res, int count, int stride) {
        int idx = Dx12Backend.SrvStore.Allocate();
        dev.Device.CreateShaderResourceView(res, new ShaderResourceViewDescription {
            Format = Format.Unknown,   // structured buffer
            ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Buffer,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Buffer = new BufferShaderResourceView {
                FirstElement = 0, NumElements = (uint)count, StructureByteStride = (uint)stride,
                Flags = BufferShaderResourceViewFlags.None,
            },
        }, Dx12Backend.SrvStore.Cpu(idx));
        return idx;
    }

    int MakeTypedSrv(ID3D12Resource res, int count, Format fmt) {
        int idx = Dx12Backend.SrvStore.Allocate();
        dev.Device.CreateShaderResourceView(res, new ShaderResourceViewDescription {
            Format = fmt,
            ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Buffer,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Buffer = new BufferShaderResourceView {
                FirstElement = 0, NumElements = (uint)count, StructureByteStride = 0,
                Flags = BufferShaderResourceViewFlags.None,
            },
        }, Dx12Backend.SrvStore.Cpu(idx));
        return idx;
    }

    // Gather the scene's active punctual lights into the GPU light buffer (called by the renderer, which
    // owns the RuntimeSet iteration so this stays free of engine refs beyond the GpuLight pack). Returns
    // the count. The renderer fills `lights[i]` via SetLight before calling Cull.
    public void BeginGather() => lightCount = 0;

    public void AddPoint(Vector3 worldPos, float range, Vector3 radianceHdr, float sourceRadius) {
        if (lightCount >= MaxLights) return;
        lights[lightCount] = new GpuLight {
            PosRange = new Vector4(worldPos, MathF.Max(range, 1e-3f)),
            Color = new Vector4(radianceHdr, 0f),           // type 0 = point
            DirCosOuter = Vector4.Zero,
            Extra = new Vector4(0f, -1f, sourceRadius, 0f), // shadowSlot -1 (punctual shadows are a later step)
        };
        lightCount++;
    }

    public void AddSpot(Vector3 worldPos, Vector3 dir, float range, Vector3 radianceHdr,
        float cosInner, float cosOuter, float sourceRadius) {
        if (lightCount >= MaxLights) return;
        lights[lightCount] = new GpuLight {
            PosRange = new Vector4(worldPos, MathF.Max(range, 1e-3f)),
            Color = new Vector4(radianceHdr, 1f),           // type 1 = spot
            DirCosOuter = new Vector4(Vector3.Normalize(dir), cosOuter),
            Extra = new Vector4(cosInner, -1f, sourceRadius, 0f),
        };
        lightCount++;
    }

    // Build view-space cluster AABBs (only when proj/viewport changes) + CPU-cull the gathered lights into
    // the per-cluster grid + flat index list, then upload all three buffers (persistent map = just memcpy).
    public unsafe void Cull(Matrix4x4 view, Matrix4x4 proj, int width, int height, float near, float far) {
        EnsureClusters(proj, width, height, near, far);

        // Light centers → view space (the cull tests sphere-vs-AABB in view space, GL parity).
        for (int i = 0; i < lightCount; i++) {
            Vector3 wp = new(lights[i].PosRange.X, lights[i].PosRange.Y, lights[i].PosRange.Z);
            lightViewPos[i] = Vector3.Transform(wp, view);
            lightRange[i] = lights[i].PosRange.W;
        }

        // Upload the light records.
        fixed (GpuLight* src = lights)
            Buffer.MemoryCopy(src, lightMapped, MaxLights * GpuLightBytes, (long)lightCount * GpuLightBytes);

        // Per-cluster cull → grid {offset,count} + flat index list.
        int* grid = (int*)gridMapped;
        uint* indices = (uint*)indexMapped;
        int cursor = 0;
        Span<int> local = stackalloc int[MaxLightsPerCluster];
        for (int c = 0; c < ClusterCount; c++) {
            Vector3 lo = clusterMin[c], hi = clusterMax[c];
            int n = 0;
            for (int i = 0; i < lightCount && n < MaxLightsPerCluster; i++) {
                float r = lightRange[i];
                if (SqDistPointAabb(lightViewPos[i], lo, hi) <= r * r)
                    local[n++] = i;
            }
            int offset = cursor;
            if (n > 0 && cursor + n <= MaxLightIndices) {
                for (int k = 0; k < n; k++) indices[cursor + k] = (uint)local[k];
                cursor += n;
            } else {
                n = 0;   // overflow: this cluster gets sun+ambient only (GL parity)
            }
            grid[c * 2 + 0] = offset;
            grid[c * 2 + 1] = n;
        }
    }

    // Squared distance from a point to an AABB (0 if inside) — the sphere-vs-AABB overlap test (GL parity).
    static float SqDistPointAabb(Vector3 p, Vector3 lo, Vector3 hi) {
        float d = 0f;
        if (p.X < lo.X) d += (lo.X - p.X) * (lo.X - p.X); else if (p.X > hi.X) d += (p.X - hi.X) * (p.X - hi.X);
        if (p.Y < lo.Y) d += (lo.Y - p.Y) * (lo.Y - p.Y); else if (p.Y > hi.Y) d += (p.Y - hi.Y) * (p.Y - hi.Y);
        if (p.Z < lo.Z) d += (lo.Z - p.Z) * (lo.Z - p.Z); else if (p.Z > hi.Z) d += (p.Z - hi.Z) * (p.Z - hi.Z);
        return d;
    }

    // Cluster view-space AABBs from the projection. View space is RH (looking down -Z, so view Z is
    // NEGATIVE). Log-Z slices: zNear(slice) = -near * (far/near)^(slice/ClusterZ), matching the GL
    // ClusterBuild_Comp. XY froxel bounds come from unprojecting the tile corners at each slice's depth.
    void EnsureClusters(Matrix4x4 proj, int width, int height, float near, float far) {
        if (clustersBuilt && width == builtW && height == builtH && proj.Equals(builtProj)) return;
        builtProj = proj; builtW = width; builtH = height; clustersBuilt = true;

        Matrix4x4.Invert(proj, out Matrix4x4 invProj);
        for (int z = 0; z < ClusterZ; z++) {
            float zNear = -near * MathF.Pow(far / near, (float)z / ClusterZ);
            float zFar = -near * MathF.Pow(far / near, (float)(z + 1) / ClusterZ);
            for (int y = 0; y < ClusterY; y++) {
                for (int x = 0; x < ClusterX; x++) {
                    // Tile's NDC xy extents [-1,1].
                    float u0 = (float)x / ClusterX, u1 = (float)(x + 1) / ClusterX;
                    float v0 = (float)y / ClusterY, v1 = (float)(y + 1) / ClusterY;
                    float nx0 = u0 * 2f - 1f, nx1 = u1 * 2f - 1f;
                    // NDC y: screen tile y grows downward; flip to NDC up.
                    float ny0 = 1f - v1 * 2f, ny1 = 1f - v0 * 2f;

                    // Unproject the 4 tile corners at the near AND far slice plane, take the AABB of all 8.
                    Vector3 lo = new(float.MaxValue), hi = new(float.MinValue);
                    foreach (float zv in stackalloc float[] { zNear, zFar })
                    foreach (float nx in stackalloc float[] { nx0, nx1 })
                    foreach (float ny in stackalloc float[] { ny0, ny1 }) {
                        Vector3 p = UnprojectToViewAtZ(invProj, nx, ny, zv);
                        lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p);
                    }
                    int c = x + ClusterX * (y + ClusterY * z);
                    clusterMin[c] = lo; clusterMax[c] = hi;
                }
            }
        }
    }

    // Unproject an NDC xy at a target VIEW-space z (negative) into a view-space point. Build an NDC point
    // at an arbitrary depth, transform by invProj to view (w-divide), then scale the ray to hit zView.
    static Vector3 UnprojectToViewAtZ(Matrix4x4 invProj, float ndcX, float ndcY, float zView) {
        // Unproject at NDC z=0 (DX near). gives a view-space ray point; the eye is at origin.
        Vector4 clip = new(ndcX, ndcY, 0f, 1f);
        Vector4 v = Vector4.Transform(clip, invProj);
        Vector3 ray = new Vector3(v.X, v.Y, v.Z) / v.W;   // a point on the ray through this NDC xy
        // Scale the ray (from the eye at origin) so its z equals zView.
        float t = zView / ray.Z;
        return ray * t;
    }

    public unsafe void Dispose() {
        lightBuf?.Dispose(); gridBuf?.Dispose(); indexBuf?.Dispose();
    }
}
