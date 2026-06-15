using System;
using System.Numerics;
using Vortice.Direct3D12;
using Vortice.Dxc;
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

    // Light buffer (upload, mapped — CPU gathers). grid/index are DEFAULT-heap (the GPU cull writes them as
    // raw UAVs; the deferred lighting reads typed SRVs). CPU-fallback staging mirrors into them via copy.
    ID3D12Resource lightBuf;   unsafe byte* lightMapped;   int lightSrv = -1;
    ID3D12Resource gridBuf;    int gridSrv = -1;    // DEFAULT: int2 {offset,count} per cluster (deferred SRV)
    ID3D12Resource indexBuf;   int indexSrv = -1;   // DEFAULT: flat uint light-index list (deferred SRV)
    ResourceStates gridIndexState = ResourceStates.PixelShaderResource;

    // GPU compute cull (default; BALLISTIC_DX12_GPU_LIGHTCULL=0 = CPU fallback). Inputs uploaded by the CPU
    // (view-space light pos + cluster AABBs) so the cull decision is byte-identical; the loop runs on GPU.
    readonly bool gpuCullOn = Environment.GetEnvironmentVariable("BALLISTIC_DX12_GPU_LIGHTCULL") != "0";
    ID3D12RootSignature cullRootSig; ID3D12PipelineState cullPso;
    ID3D12Resource lightViewBuf; unsafe byte* lightViewMapped;  // float4 per light: xyz view pos, w range
    ID3D12Resource clusterMinBuf; unsafe byte* clusterMinMapped;
    ID3D12Resource clusterMaxBuf; unsafe byte* clusterMaxMapped;
    ID3D12Resource counterBuf;    // DEFAULT raw UAV (1 uint cursor)
    ID3D12Resource zeroCounter;   // upload (1 uint = 0) — copied to reset the counter each frame
    ID3D12Resource cullCb; unsafe byte* cullCbMapped;
    // CPU-fallback staging (upload, mapped) for grid/index — copied into the DEFAULT buffers.
    ID3D12Resource gridStaging;  unsafe byte* gridMapped;
    ID3D12Resource indexStaging; unsafe byte* indexMapped;

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
        // grid/index DEFAULT-heap (GPU cull writes raw UAVs; deferred reads typed SRVs).
        gridBuf = MakeDefaultUav((ulong)(ClusterCount * 2 * sizeof(int)));
        indexBuf = MakeDefaultUav((ulong)(MaxLightIndices * sizeof(uint)));
        // CPU-fallback staging (upload, mapped) — copied into the DEFAULT grid/index when gpuCullOn is false.
        gridStaging = MakeUpload((ulong)(ClusterCount * 2 * sizeof(int)), out gridMapped);
        indexStaging = MakeUpload((ulong)(MaxLightIndices * sizeof(uint)), out indexMapped);

        // SRVs: light = StructuredBuffer (64B stride); grid = Buffer<int2> (R32G32_SInt); index = Buffer<uint>.
        lightSrv = MakeStructuredSrv(lightBuf, MaxLights, GpuLightBytes);
        gridSrv = MakeTypedSrv(gridBuf, ClusterCount, Format.R32G32_SInt);
        indexSrv = MakeTypedSrv(indexBuf, MaxLightIndices, Format.R32_UInt);

        // GPU-cull inputs (CPU-uploaded so the cull is byte-identical) + outputs.
        lightViewBuf = MakeUpload((ulong)(MaxLights * 16), out lightViewMapped);   // float4 per light
        clusterMinBuf = MakeUpload((ulong)(ClusterCount * 16), out clusterMinMapped);
        clusterMaxBuf = MakeUpload((ulong)(ClusterCount * 16), out clusterMaxMapped);
        counterBuf = dev.Device.CreateCommittedResource(HeapProperties.DefaultHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer(sizeof(uint), ResourceFlags.AllowUnorderedAccess), ResourceStates.UnorderedAccess);
        zeroCounter = MakeUpload(sizeof(uint), out byte* zc); *(uint*)zc = 0;
        int cbSize = (System.Runtime.InteropServices.Marshal.SizeOf<int>() * 4 + 255) & ~255;
        cullCb = MakeUpload((ulong)cbSize, out cullCbMapped);
        BuildCullPipeline();
    }

    ID3D12Resource MakeDefaultUav(ulong bytes) =>
        dev.Device.CreateCommittedResource(HeapProperties.DefaultHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer(bytes, ResourceFlags.AllowUnorderedAccess), ResourceStates.PixelShaderResource);

    // Compute cull: CBV b0 + 3 root SRVs (lightView/clusterMin/clusterMax, structured) + 3 root UAVs
    // (grid/index/counter, raw). All root descriptors — no descriptor heap needed.
    void BuildCullPipeline() {
        var ps = new[] {
            new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All),
            new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(0, 0), ShaderVisibility.All),
            new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(1, 0), ShaderVisibility.All),
            new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(2, 0), ShaderVisibility.All),
            new RootParameter1(RootParameterType.UnorderedAccessView, new RootDescriptor1(0, 0), ShaderVisibility.All),
            new RootParameter1(RootParameterType.UnorderedAccessView, new RootDescriptor1(1, 0), ShaderVisibility.All),
            new RootParameter1(RootParameterType.UnorderedAccessView, new RootDescriptor1(2, 0), ShaderVisibility.All),
        };
        cullRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, ps)));
        string hlsl = EmbeddedShaderSource.ReadHlsl("ClusterCull.hlsl");
        cullPso = dev.Device.CreateComputePipelineState(new ComputePipelineStateDescription {
            RootSignature = cullRootSig,
            ComputeShader = Dx12ShaderCompiler.Compile(DxcShaderStage.Compute, hlsl, "CSMain", "ClusterCull.hlsl"),
        });
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

        // Light centers → view space (the cull tests sphere-vs-AABB in view space, GL parity). Also written
        // to the upload buffer the GPU cull reads (so its decision is byte-identical to the CPU path).
        float* lv = (float*)lightViewMapped;
        for (int i = 0; i < lightCount; i++) {
            Vector3 wp = new(lights[i].PosRange.X, lights[i].PosRange.Y, lights[i].PosRange.Z);
            lightViewPos[i] = Vector3.Transform(wp, view);
            lightRange[i] = lights[i].PosRange.W;
            lv[i * 4 + 0] = lightViewPos[i].X; lv[i * 4 + 1] = lightViewPos[i].Y;
            lv[i * 4 + 2] = lightViewPos[i].Z; lv[i * 4 + 3] = lightRange[i];
        }

        // Upload the light records.
        fixed (GpuLight* src = lights)
            Buffer.MemoryCopy(src, lightMapped, MaxLights * GpuLightBytes, (long)lightCount * GpuLightBytes);

        if (gpuCullOn) { GpuCull(); return; }

        // --- CPU fallback: per-cluster cull → grid {offset,count} + flat index list (staging) ---
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
        // Copy the staging grid/index into the DEFAULT buffers the deferred pass reads.
        dev.ExecuteSync(cl => {
            TransitionGridIndex(cl, ResourceStates.CopyDest);
            cl.CopyBufferRegion(gridBuf, 0, gridStaging, 0, (ulong)(ClusterCount * 2 * sizeof(int)));
            cl.CopyBufferRegion(indexBuf, 0, indexStaging, 0, (ulong)(MaxLightIndices * sizeof(uint)));
            TransitionGridIndex(cl, ResourceStates.PixelShaderResource);
        });
    }

    // GPU compute cull: reset the counter, dispatch one thread per cluster (writes grid + index), leave
    // grid/index in PixelShaderResource for the deferred lighting read. Byte-identical to the CPU path.
    unsafe void GpuCull() {
        *(int*)cullCbMapped = lightCount;
        *((int*)cullCbMapped + 1) = ClusterCount;
        *((int*)cullCbMapped + 2) = MaxLightIndices;
        *((int*)cullCbMapped + 3) = MaxLightsPerCluster;
        dev.ExecuteSync(cl => {
            TransitionGridIndex(cl, ResourceStates.UnorderedAccess);
            cl.ResourceBarrierTransition(counterBuf, ResourceStates.UnorderedAccess, ResourceStates.CopyDest);
            cl.CopyBufferRegion(counterBuf, 0, zeroCounter, 0, sizeof(uint));
            cl.ResourceBarrierTransition(counterBuf, ResourceStates.CopyDest, ResourceStates.UnorderedAccess);
            cl.SetComputeRootSignature(cullRootSig);
            cl.SetPipelineState(cullPso);
            cl.SetComputeRootConstantBufferView(0, cullCb.GPUVirtualAddress);
            cl.SetComputeRootShaderResourceView(1, lightViewBuf.GPUVirtualAddress);
            cl.SetComputeRootShaderResourceView(2, clusterMinBuf.GPUVirtualAddress);
            cl.SetComputeRootShaderResourceView(3, clusterMaxBuf.GPUVirtualAddress);
            cl.SetComputeRootUnorderedAccessView(4, gridBuf.GPUVirtualAddress);
            cl.SetComputeRootUnorderedAccessView(5, indexBuf.GPUVirtualAddress);
            cl.SetComputeRootUnorderedAccessView(6, counterBuf.GPUVirtualAddress);
            cl.Dispatch((ClusterCount + 63) / 64, 1, 1);
            TransitionGridIndex(cl, ResourceStates.PixelShaderResource);
        });
    }

    void TransitionGridIndex(ID3D12GraphicsCommandList4 cl, ResourceStates target) {
        if (gridIndexState == target) return;
        cl.ResourceBarrierTransition(gridBuf, gridIndexState, target);
        cl.ResourceBarrierTransition(indexBuf, gridIndexState, target);
        gridIndexState = target;
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
    unsafe void EnsureClusters(Matrix4x4 proj, int width, int height, float near, float far) {
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
                    // Mirror into the GPU cull's upload buffers (float4 per cluster).
                    float* mn = (float*)clusterMinMapped + c * 4;
                    float* mx = (float*)clusterMaxMapped + c * 4;
                    mn[0] = lo.X; mn[1] = lo.Y; mn[2] = lo.Z; mn[3] = 0f;
                    mx[0] = hi.X; mx[1] = hi.Y; mx[2] = hi.Z; mx[3] = 0f;
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
        gridStaging?.Dispose(); indexStaging?.Dispose();
        lightViewBuf?.Dispose(); clusterMinBuf?.Dispose(); clusterMaxBuf?.Dispose();
        counterBuf?.Dispose(); zeroCounter?.Dispose(); cullCb?.Dispose();
        cullRootSig?.Dispose(); cullPso?.Dispose();
    }
}
