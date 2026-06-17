using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D12;
using Vortice.Dxc;
using Vortice.DXGI;

namespace BallisticEngine.DX12;

// Owns the Hillaire-2020 aerial-perspective FROXEL VOLUME for the DX12 procedural sky: a small camera-anchored
// 32x32x32 RGBA16F 3D texture where each froxel holds the accumulated single-scatter inscatter (rgb) + mean
// transmittance (a) of a short Rayleigh/Mie march from the camera to that froxel's view distance, using the
// SAME atmosphere the sky shows. Re-baked every frame from the current view (a compute dispatch on the frame
// list) — cheap (32k threads, one short march each). The AP pass samples it by (screenUV, linearViewDistance)
// and applies `scene*T + inscatter` so distant geometry fades into exactly the colour of the sky behind it.
//
// This is the physical replacement for the old analytic AP veil (a hardcoded lux-scaled blue tint over a fake
// linear-distance term). See Docs/Plans/dx12-aerial-perspective-rework.md.
public sealed class Dx12AerialPerspectiveLut : IDisposable {
    // Froxel resolution. 32^3 is the Hillaire/Unreal default — the haze is low-frequency so a coarse volume is
    // plenty, and linear sampling across slices hides the slice boundaries.
    public const int VolW = 32, VolH = 32, VolD = 32;

    readonly Dx12Device dev;
    ID3D12Resource volume;              // 32^3 RGBA16F UAV/SRV froxel volume
    int srvIndex = -1;                  // persistent SRV "home" in the SrvStore (the AP pass copies it per frame)
    ID3D12RootSignature rootSig;        // ApLutConstants CBV (b0) + volume UAV (u0)
    ID3D12PipelineState pso;
    ID3D12Resource cb;
    unsafe byte* cbMapped;
    Dx12DescriptorHeap uavHeap;         // shader-visible: the volume UAV at slot 0 for the bake dispatch
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

        // 32^3 RGBA16F, UAV-writable (bake) + SRV-readable (AP pass).
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

        // Persistent SRV home (Texture3D) so the AP pass can CopyDescriptorsSimple it into its own heap per frame.
        srvIndex = Dx12Backend.SrvStore.Allocate();
        dev.Device.CreateShaderResourceView(volume, new ShaderResourceViewDescription {
            Format = Format.R16G16B16A16_Float, ViewDimension = ShaderResourceViewDimension.Texture3D,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Texture3D = new Texture3DShaderResourceView { MipLevels = 1, MostDetailedMip = 0 },
        }, Dx12Backend.SrvStore.Cpu(srvIndex));

        // Root sig: ApLutConstants CBV (b0) + a 1-UAV table (u0 = the volume).
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

    // Bake the froxel volume for this frame. Records the compute dispatch on the pipelined frame list (via
    // dev.ExecuteSync) and leaves the volume in PixelShaderResource for the AP pass to sample. All the haze
    // tuning (strength/distance/density) comes from the PostFX block (the AerialPerspective volume bridge).
    // `intensity` is the RESOLVED master strength (PostFX value with any BALLISTIC_DX12_AP_STRENGTH env
    // override already applied by the caller) — it does NOT read pf.AerialPerspectiveIntensity itself.
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
            cl.Dispatch((VolW + 7) / 8, (VolH + 7) / 8, VolD);   // [numthreads(8,8,1)] over W,H; one row of threads per slice
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
