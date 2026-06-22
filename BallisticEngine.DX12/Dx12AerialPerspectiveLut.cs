using System.Runtime.InteropServices;
using Vortice.Direct3D12;
using Vortice.Dxc;
using Vortice.DXGI;

namespace BallisticEngine.DX12;

public sealed class Dx12AerialPerspectiveLut : IDisposable {
    public const int VolW = 32, VolH = 32, VolD = 32;

    readonly Dx12Device dev;
    ID3D12Resource volume;
    int srvIndex = -1;
    ID3D12RootSignature rootSig;
    ID3D12PipelineState pso;
    ID3D12Resource cb;
    unsafe byte* cbMapped;
    Dx12DescriptorHeap uavHeap;
    ResourceStates volumeState;

    public ID3D12Resource Volume => volume;
    public CpuDescriptorHandle SrvCpu => Dx12Backend.SrvStore.Cpu(srvIndex);

    [StructLayout(LayoutKind.Sequential)]
    struct ApLutConstants {
        public Matrix4x4 InvViewProj;
        public Vector3 CameraPos; public float MaxDistance;
        public Vector3 SunDirection; public float StartDistance;
        public Vector3 SunRadiance; public float DensityScale;
        public Vector3 SkyTint; public float Anisotropy;
        public float AirDensity, Haze, OzoneDensity, Intensity;
        public Vector3 Tint; public float PadL0;
        public uint VolSizeX, VolSizeY, VolSizeZ; public float PadL;
    }

    public unsafe Dx12AerialPerspectiveLut(Dx12Device device) {
        dev = device;

        var desc = new ResourceDescription {
            Dimension = ResourceDimension.Texture3D,
            Width = VolW, Height = VolH, DepthOrArraySize = VolD, MipLevels = 1,
            Format = Format.R16G16B16A16_Float, SampleDescription = new SampleDescription(1, 0),
            Layout = TextureLayout.Unknown, Flags = ResourceFlags.AllowUnorderedAccess,
        };
        volume = dev.Device.CreateCommittedResource(HeapProperties.DefaultHeapProperties, HeapFlags.None,
            desc, ResourceStates.UnorderedAccess);
        volume.Name = "AerialPerspectiveLut";
        volumeState = ResourceStates.UnorderedAccess;

        srvIndex = Dx12Backend.SrvStore.Allocate();
        dev.Device.CreateShaderResourceView(volume, new ShaderResourceViewDescription {
            Format = Format.R16G16B16A16_Float, ViewDimension = ShaderResourceViewDimension.Texture3D,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Texture3D = new Texture3DShaderResourceView { MipLevels = 1, MostDetailedMip = 0 },
        }, Dx12Backend.SrvStore.Cpu(srvIndex));

        var cbv = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        var uavRange = new DescriptorRange1(DescriptorRangeType.UnorderedAccessView, 1, baseShaderRegister: 0);
        var uavTable = new RootParameter1(new RootDescriptorTable1(uavRange), ShaderVisibility.All);
        rootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, new[] { cbv, uavTable })));

        string hlsl = EmbeddedShaderSource.ReadHlsl("AerialPerspectiveLut.hlsl");
        byte[] cs = Dx12ShaderCompiler.Compile(DxcShaderStage.Compute, hlsl, "CSMain", "AerialPerspectiveLut.hlsl");
        pso = dev.Device.CreateComputePipelineState(
            new ComputePipelineStateDescription { RootSignature = rootSig, ComputeShader = cs });

        int cbSize = (Marshal.SizeOf<ApLutConstants>() + 255) & ~255;
        cb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)cbSize), ResourceStates.GenericRead);
        cbMapped = cb.Map<byte>(0);

        uavHeap = new Dx12DescriptorHeap(dev,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 1, shaderVisible: true,
            framesInFlight: dev.FramesInFlight);
        dev.Device.CreateUnorderedAccessView(volume, null, new UnorderedAccessViewDescription {
            Format = Format.R16G16B16A16_Float, ViewDimension = UnorderedAccessViewDimension.Texture3D,
            Texture3D = new Texture3DUnorderedAccessView { FirstWSlice = 0, WSize = VolD, MipSlice = 0 },
        }, uavHeap.Cpu(0));
    }

    void Transition(ID3D12GraphicsCommandList4 cl, ResourceStates to) {
        if (volumeState == to) return;
        cl.ResourceBarrierTransition(volume, volumeState, to);
        volumeState = to;
    }

    public unsafe void Bake(Matrix4x4 invViewProj, Vector3 camPos, Vector3 sunDir, Vector3 sunRadiance,
                            Vector3 skyTint, ProceduralSky sky, PostProcessSettings pf, float intensity) {
        *(ApLutConstants*)cbMapped = new ApLutConstants {
            InvViewProj = Matrix4x4.Transpose(invViewProj),
            CameraPos = camPos, MaxDistance = MathF.Max(pf.AerialPerspectiveMaxDistance, 1f),
            SunDirection = sunDir, StartDistance = MathF.Max(pf.AerialPerspectiveStartDistance, 0f),
            SunRadiance = sunRadiance, DensityScale = MathF.Max(pf.AerialPerspectiveDensityScale, 0f),
            SkyTint = skyTint, Anisotropy = sky is not null ? Math.Clamp(sky.HazeAnisotropy, -0.95f, 0.95f) : 0.8f,
            AirDensity = sky is not null ? MathF.Max(sky.AirDensity, 0f) : 1f,
            Haze = sky is not null ? MathF.Max(sky.Haze, 0f) : 1f,
            OzoneDensity = sky is not null ? MathF.Max(sky.OzoneDensity, 0f) : 1f,
            Intensity = MathF.Max(intensity, 0f),
            Tint = pf.AerialPerspectiveTint,
            VolSizeX = VolW, VolSizeY = VolH, VolSizeZ = VolD,
        };

        dev.ExecuteSync(cl => {
            Transition(cl, ResourceStates.UnorderedAccess);
            cl.SetDescriptorHeaps(uavHeap.Heap);
            cl.SetComputeRootSignature(rootSig);
            cl.SetPipelineState(pso);
            cl.SetComputeRootConstantBufferView(0, cb.GPUVirtualAddress);
            cl.SetComputeRootDescriptorTable(1, uavHeap.Gpu(0));
            cl.Dispatch((VolW + 7) / 8, (VolH + 7) / 8, VolD);
            Transition(cl, ResourceStates.PixelShaderResource);
        });
    }

    public void Dispose() {
        uavHeap?.Dispose();
        cb?.Dispose();
        pso?.Dispose();
        rootSig?.Dispose();
        volume?.Dispose();
    }
}
