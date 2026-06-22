using Vortice.Direct3D12;
using Vortice.Dxc;
using Vortice.DXGI;

namespace BallisticEngine.DX12;

public sealed class Dx12ClusteredLights : IDisposable {
    public const int ClusterX = 16, ClusterY = 9, ClusterZ = 24;
    public const int ClusterCount = ClusterX * ClusterY * ClusterZ;
    public const int MaxLights = 1024;
    public const int MaxLightsPerCluster = 128;
    public const int MaxLightIndices = ClusterCount * 32;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public struct GpuLight {
        public Vector4 PosRange;
        public Vector4 Color;
        public Vector4 DirCosOuter;
        public Vector4 Extra;
        public Vector4 RightAxisHalfW;
    }
    public const int GpuLightBytes = 80;

    readonly Dx12Device dev;

    ID3D12Resource lightBuf;   unsafe byte* lightMapped;   int[] lightSrvs = System.Array.Empty<int>();
    ID3D12Resource gridBuf;    int gridSrv = -1;
    ID3D12Resource indexBuf;   int indexSrv = -1;
    ResourceStates gridIndexState = ResourceStates.PixelShaderResource;

    readonly bool gpuCullOn = Environment.GetEnvironmentVariable("BALLISTIC_DX12_GPU_LIGHTCULL") != "0";

    readonly bool asyncCullDoor = Environment.GetEnvironmentVariable("BALLISTIC_DX12_ASYNC_LIGHTCULL") != "0";
    ID3D12RootSignature cullRootSig; ID3D12PipelineState cullPso;
    ID3D12Resource lightViewBuf; unsafe byte* lightViewMapped;
    ID3D12Resource clusterMinBuf; unsafe byte* clusterMinMapped;
    ID3D12Resource clusterMaxBuf; unsafe byte* clusterMaxMapped;
    ID3D12Resource counterBuf;
    ID3D12Resource zeroCounter;
    ID3D12Resource cullCb; unsafe byte* cullCbMapped;
    ID3D12Resource gridStaging;  unsafe byte* gridMapped;
    ID3D12Resource indexStaging; unsafe byte* indexMapped;

    int lightStride, lightViewStride, clusterMinStride, clusterMaxStride, cullCbStride, gridStagingStride, indexStagingStride;
    int CullCbOffset => dev.FrameSlot * cullCbStride;

    readonly GpuLight[] lights = new GpuLight[MaxLights];
    int lightCount;
    readonly Vector3[] lightViewPos = new Vector3[MaxLights];

    readonly float[] lightRange = new float[MaxLights];
    readonly Vector3[] clusterMin = new Vector3[ClusterCount];
    readonly Vector3[] clusterMax = new Vector3[ClusterCount];
    bool clustersBuilt;
    Matrix4x4 builtProj; int builtW, builtH;

    public int LightCount => lightCount;

    public CpuDescriptorHandle LightSrvCpu => Dx12Backend.SrvStore.Cpu(lightSrvs[dev.FrameSlot]);

    public ulong LightBufGpuAddress => lightBuf is null ? 0 : lightBuf.GPUVirtualAddress + (ulong)(dev.FrameSlot * MaxLights * GpuLightBytes);
    public CpuDescriptorHandle GridSrvCpu => Dx12Backend.SrvStore.Cpu(gridSrv);
    public CpuDescriptorHandle IndexSrvCpu => Dx12Backend.SrvStore.Cpu(indexSrv);

    public unsafe Dx12ClusteredLights(Dx12Device device) {
        dev = device;
        int N = dev.FramesInFlight;
        lightStride = MaxLights * GpuLightBytes;
        lightViewStride = MaxLights * 16;
        clusterMinStride = ClusterCount * 16;
        clusterMaxStride = ClusterCount * 16;
        gridStagingStride = ClusterCount * 2 * sizeof(int);
        indexStagingStride = MaxLightIndices * sizeof(uint);

        lightBuf = MakeUpload((ulong)((long)lightStride * N), out lightMapped);
        gridBuf = MakeDefaultUav((ulong)(ClusterCount * 2 * sizeof(int)));
        indexBuf = MakeDefaultUav((ulong)(MaxLightIndices * sizeof(uint)));
        gridStaging = MakeUpload((ulong)((long)gridStagingStride * N), out gridMapped);
        indexStaging = MakeUpload((ulong)((long)indexStagingStride * N), out indexMapped);

        lightSrvs = new int[N];
        for (int s = 0; s < N; s++)
            lightSrvs[s] = MakeStructuredSrv(lightBuf, MaxLights, GpuLightBytes, firstElement: s * MaxLights);
        gridSrv = MakeTypedSrv(gridBuf, ClusterCount, Format.R32G32_SInt);
        indexSrv = MakeTypedSrv(indexBuf, MaxLightIndices, Format.R32_UInt);

        lightViewBuf = MakeUpload((ulong)((long)lightViewStride * N), out lightViewMapped);
        clusterMinBuf = MakeUpload((ulong)((long)clusterMinStride * N), out clusterMinMapped);
        clusterMaxBuf = MakeUpload((ulong)((long)clusterMaxStride * N), out clusterMaxMapped);
        counterBuf = dev.Device.CreateCommittedResource(HeapProperties.DefaultHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer(sizeof(uint), ResourceFlags.AllowUnorderedAccess), ResourceStates.UnorderedAccess);
        zeroCounter = MakeUpload(sizeof(uint), out byte* zc); *(uint*)zc = 0;
        cullCbStride = (System.Runtime.InteropServices.Marshal.SizeOf<int>() * 4 + 255) & ~255;
        cullCb = MakeUpload((ulong)((long)cullCbStride * N), out cullCbMapped);
        BuildCullPipeline();
    }

    ID3D12Resource MakeDefaultUav(ulong bytes) =>
        dev.Device.CreateCommittedResource(HeapProperties.DefaultHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer(bytes, ResourceFlags.AllowUnorderedAccess), ResourceStates.PixelShaderResource);

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

    int MakeStructuredSrv(ID3D12Resource res, int count, int stride, int firstElement = 0) {
        int idx = Dx12Backend.SrvStore.Allocate();
        dev.Device.CreateShaderResourceView(res, new ShaderResourceViewDescription {
            Format = Format.Unknown,
            ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Buffer,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Buffer = new BufferShaderResourceView {
                FirstElement = (ulong)firstElement, NumElements = (uint)count, StructureByteStride = (uint)stride,
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

    public void BeginGather() => lightCount = 0;

    public void AddPoint(Vector3 worldPos, float range, Vector3 radianceHdr, float sourceRadius) {
        if (lightCount >= MaxLights) return;
        lights[lightCount] = new GpuLight {
            PosRange = new Vector4(worldPos, MathF.Max(range, 1e-3f)),
            Color = new Vector4(radianceHdr, 0f),
            DirCosOuter = Vector4.Zero,
            Extra = new Vector4(0f, -1f, sourceRadius, 0f),
        };
        lightCount++;
    }

    public void AddSpot(Vector3 worldPos, Vector3 dir, float range, Vector3 radianceHdr,
        float cosInner, float cosOuter, float sourceRadius) {
        if (lightCount >= MaxLights) return;
        lights[lightCount] = new GpuLight {
            PosRange = new Vector4(worldPos, MathF.Max(range, 1e-3f)),
            Color = new Vector4(radianceHdr, 1f),
            DirCosOuter = new Vector4(Vector3.Normalize(dir), cosOuter),
            Extra = new Vector4(cosInner, -1f, sourceRadius, 0f),
            RightAxisHalfW = Vector4.Zero,
        };
        lightCount++;
    }

    public void AddRect(Vector3 center, Vector3 forward, Vector3 right, float halfW, float halfH,
        float range, Vector3 radianceHdr, bool twoSided) {
        if (lightCount >= MaxLights) return;
        float cullR = MathF.Max(range, 1e-3f) + MathF.Max(halfW, halfH);
        lights[lightCount] = new GpuLight {
            PosRange = new Vector4(center, cullR),
            Color = new Vector4(radianceHdr, 2f),
            DirCosOuter = new Vector4(Vector3.Normalize(forward), halfW),
            Extra = new Vector4(twoSided ? 1f : 0f, -1f, range, halfH),
            RightAxisHalfW = new Vector4(Vector3.Normalize(right), halfW),
        };
        lightCount++;
    }

    public unsafe void Cull(Matrix4x4 view, Matrix4x4 proj, int width, int height, float near, float far) {
        EnsureClusters(proj, width, height, near, far);

        int slot = dev.FrameSlot;

        float* lv = (float*)(lightViewMapped + slot * lightViewStride);
        for (int i = 0; i < lightCount; i++) {
            Vector3 wp = new(lights[i].PosRange.X, lights[i].PosRange.Y, lights[i].PosRange.Z);
            lightViewPos[i] = Vector3.Transform(wp, view);
            lightRange[i] = lights[i].PosRange.W;
            lv[i * 4 + 0] = lightViewPos[i].X; lv[i * 4 + 1] = lightViewPos[i].Y;
            lv[i * 4 + 2] = lightViewPos[i].Z; lv[i * 4 + 3] = lightRange[i];
        }

        fixed (GpuLight* src = lights)
            Buffer.MemoryCopy(src, lightMapped + slot * lightStride, lightStride, (long)lightCount * GpuLightBytes);

        if (gpuCullOn) { GpuCull(); return; }

        int* grid = (int*)(gridMapped + slot * gridStagingStride);
        uint* indices = (uint*)(indexMapped + slot * indexStagingStride);
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
                n = 0;
            }
            grid[c * 2 + 0] = offset;
            grid[c * 2 + 1] = n;
        }

        dev.ExecuteSync(cl => {
            TransitionGridIndex(cl, ResourceStates.CopyDest);
            cl.CopyBufferRegion(gridBuf, 0, gridStaging, (ulong)(slot * gridStagingStride), (ulong)(ClusterCount * 2 * sizeof(int)));
            cl.CopyBufferRegion(indexBuf, 0, indexStaging, (ulong)(slot * indexStagingStride), (ulong)(MaxLightIndices * sizeof(uint)));
            TransitionGridIndex(cl, ResourceStates.PixelShaderResource);
        });
    }

    unsafe void GpuCull() {
        int slot = dev.FrameSlot;
        int* cb = (int*)(cullCbMapped + slot * cullCbStride);
        cb[0] = lightCount;
        cb[1] = ClusterCount;
        cb[2] = MaxLightIndices;
        cb[3] = MaxLightsPerCluster;

        bool async = asyncCullDoor && dev.AsyncComputeEnabled && dev.FrameOpen;

        Action<ID3D12GraphicsCommandList4> dispatchCull = cl => {
            cl.ResourceBarrierTransition(counterBuf, ResourceStates.UnorderedAccess, ResourceStates.CopyDest);
            cl.CopyBufferRegion(counterBuf, 0, zeroCounter, 0, sizeof(uint));
            cl.ResourceBarrierTransition(counterBuf, ResourceStates.CopyDest, ResourceStates.UnorderedAccess);
            cl.SetComputeRootSignature(cullRootSig);
            cl.SetPipelineState(cullPso);
            cl.SetComputeRootConstantBufferView(0, cullCb.GPUVirtualAddress + (ulong)(slot * cullCbStride));
            cl.SetComputeRootShaderResourceView(1, lightViewBuf.GPUVirtualAddress + (ulong)(slot * lightViewStride));
            cl.SetComputeRootShaderResourceView(2, clusterMinBuf.GPUVirtualAddress + (ulong)(slot * clusterMinStride));
            cl.SetComputeRootShaderResourceView(3, clusterMaxBuf.GPUVirtualAddress + (ulong)(slot * clusterMaxStride));
            cl.SetComputeRootUnorderedAccessView(4, gridBuf.GPUVirtualAddress);
            cl.SetComputeRootUnorderedAccessView(5, indexBuf.GPUVirtualAddress);
            cl.SetComputeRootUnorderedAccessView(6, counterBuf.GPUVirtualAddress);
            cl.Dispatch((ClusterCount + 63) / 64, 1, 1);
        };

        if (async) {
            dev.ExecuteSync(cl => TransitionGridIndex(cl, ResourceStates.UnorderedAccess));
            dev.RecordAsyncCompute(dispatchCull);
            dev.ExecuteSync(cl => TransitionGridIndex(cl, ResourceStates.PixelShaderResource));
        } else {
            dev.ExecuteSync(cl => {
                TransitionGridIndex(cl, ResourceStates.UnorderedAccess);
                dispatchCull(cl);
                TransitionGridIndex(cl, ResourceStates.PixelShaderResource);
            });
        }
    }

    void TransitionGridIndex(ID3D12GraphicsCommandList4 cl, ResourceStates target) {
        if (gridIndexState == target) return;
        cl.ResourceBarrierTransition(gridBuf, gridIndexState, target);
        cl.ResourceBarrierTransition(indexBuf, gridIndexState, target);
        gridIndexState = target;
    }

    static float SqDistPointAabb(Vector3 p, Vector3 lo, Vector3 hi) {
        float d = 0f;
        if (p.X < lo.X) d += (lo.X - p.X) * (lo.X - p.X); else if (p.X > hi.X) d += (p.X - hi.X) * (p.X - hi.X);
        if (p.Y < lo.Y) d += (lo.Y - p.Y) * (lo.Y - p.Y); else if (p.Y > hi.Y) d += (p.Y - hi.Y) * (p.Y - hi.Y);
        if (p.Z < lo.Z) d += (lo.Z - p.Z) * (lo.Z - p.Z); else if (p.Z > hi.Z) d += (p.Z - hi.Z) * (p.Z - hi.Z);
        return d;
    }

    unsafe void EnsureClusters(Matrix4x4 proj, int width, int height, float near, float far) {
        if (clustersBuilt && width == builtW && height == builtH && proj.Equals(builtProj)) return;
        builtProj = proj; builtW = width; builtH = height; clustersBuilt = true;

        Matrix4x4.Invert(proj, out Matrix4x4 invProj);
        for (int z = 0; z < ClusterZ; z++) {
            float zNear = -near * MathF.Pow(far / near, (float)z / ClusterZ);
            float zFar = -near * MathF.Pow(far / near, (float)(z + 1) / ClusterZ);
            for (int y = 0; y < ClusterY; y++) {
                for (int x = 0; x < ClusterX; x++) {
                    float u0 = (float)x / ClusterX, u1 = (float)(x + 1) / ClusterX;
                    float v0 = (float)y / ClusterY, v1 = (float)(y + 1) / ClusterY;
                    float nx0 = u0 * 2f - 1f, nx1 = u1 * 2f - 1f;
                    float ny0 = 1f - v1 * 2f, ny1 = 1f - v0 * 2f;

                    Vector3 lo = new(float.MaxValue), hi = new(float.MinValue);
                    foreach (float zv in stackalloc float[] { zNear, zFar })
                    foreach (float nx in stackalloc float[] { nx0, nx1 })
                    foreach (float ny in stackalloc float[] { ny0, ny1 }) {
                        Vector3 p = UnprojectToViewAtZ(invProj, nx, ny, zv);
                        lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p);
                    }
                    int c = x + ClusterX * (y + ClusterY * z);
                    clusterMin[c] = lo; clusterMax[c] = hi;
                    for (int s = 0; s < dev.FramesInFlight; s++) {
                        float* mn = (float*)(clusterMinMapped + s * clusterMinStride) + c * 4;
                        float* mx = (float*)(clusterMaxMapped + s * clusterMaxStride) + c * 4;
                        mn[0] = lo.X; mn[1] = lo.Y; mn[2] = lo.Z; mn[3] = 0f;
                        mx[0] = hi.X; mx[1] = hi.Y; mx[2] = hi.Z; mx[3] = 0f;
                    }
                }
            }
        }
    }

    static Vector3 UnprojectToViewAtZ(Matrix4x4 invProj, float ndcX, float ndcY, float zView) {
        Vector4 clip = new(ndcX, ndcY, 0f, 1f);
        Vector4 v = Vector4.Transform(clip, invProj);
        Vector3 ray = new Vector3(v.X, v.Y, v.Z) / v.W;
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
