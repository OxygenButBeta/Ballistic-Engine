using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.Dxc;
using Vortice.DXGI;

namespace BallisticEngine.DX12;

public sealed class Dx12IblBaker : IDisposable {
    const int EnvRes = 256;

    const int IrradianceRes = 32;
    const int PrefilterRes = 128;
    const int PrefilterMips = 5;
    const int BrdfRes = 256;

    readonly Dx12Device dev;

    Dx12CubeTarget envCube;
    Dx12CubeTarget irradianceCube;
    Dx12CubeTarget prefilterCube;
    ID3D12Resource brdfLut;
    int brdfSrvIndex = -1;

    ID3D12RootSignature envRootSig;
    ID3D12PipelineState envPso;
    ID3D12RootSignature iblRootSig;
    ID3D12PipelineState irradiancePso;
    ID3D12PipelineState prefilterPso;
    ID3D12PipelineState brdfPso;

    ID3D12Resource procSkyCb;
    unsafe byte* procSkyCbMapped;
    ID3D12Resource iblCb;
    unsafe byte* iblCbMapped;
    int iblSlotSize, iblSlotCount;
    Dx12DescriptorHeap envSrvVisible;

    int paramStamp = -1;
    public bool HasBaked { get; private set; }

    public CpuDescriptorHandle EnvSrv => envCube.SrvCpu;
    public CpuDescriptorHandle IrradianceSrv => irradianceCube.SrvCpu;
    public CpuDescriptorHandle PrefilterSrv => prefilterCube.SrvCpu;
    public CpuDescriptorHandle BrdfSrv => Dx12Backend.SrvStore.Cpu(brdfSrvIndex);
    public int PrefilterMipCount => PrefilterMips;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct ProcSkyConstants {
        public Matrix4x4 ViewProjNoTranslate;
        public Vector3 SunDirection; public float SunAngularRadius;
        public Vector3 SunRadiance; public float SunDiskIntensity;
        public Vector3 GroundAlbedo; public float AirDensity;
        public float Haze, HazeAnisotropy, OzoneDensity, MultiScatter;
        public float Exposure, BakeFace; public Vector2 Pad0;
        public float CloudsEnabled, CloudCoverage, CloudDensity, CloudAltitude;
        public float CloudThickness, CloudScale, CloudDetail, CloudAmbient;
        public Vector3 CloudWindOffset; public float CloudWindAngle;
        public float CirrusCoverage, StarIntensity; public Vector2 Pad1;
    }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct IblConstants { public int Face; public float Roughness; public float SourceResolution; public float Pad; }

    public Dx12IblBaker(Dx12Device device) {
        dev = device;
        BuildPipelines();
    }

    unsafe void BuildPipelines() {
        envRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, new[] {
                new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All) })));

        var iblCbv = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        var srvRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 1, baseShaderRegister: 0);
        var srvTable = new RootParameter1(new RootDescriptorTable1(srvRange), ShaderVisibility.Pixel);
        var samp = new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0) {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp,
            MaxAnisotropy = 1, ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        iblRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, new[] { iblCbv, srvTable }, new[] { samp })));

        string sky = BallisticEngine.DX12.EmbeddedShaderSource.ReadHlsl("ProceduralSky.hlsl");
        string ibl = BallisticEngine.DX12.EmbeddedShaderSource.ReadHlsl("IblBake.hlsl");

        envPso = FsqPso(envRootSig,
            Dx12ShaderCompiler.Compile(DxcShaderStage.Vertex, sky, "VSEnvBake", "ProceduralSky.hlsl"),
            Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, sky, "PSEnvBake", "ProceduralSky.hlsl"),
            Dx12CubeTarget.Fmt);
        byte[] fsqVs = Dx12ShaderCompiler.Compile(DxcShaderStage.Vertex, ibl, "VSFullscreen", "IblBake.hlsl");
        irradiancePso = FsqPso(iblRootSig, fsqVs,
            Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, ibl, "PSIrradiance", "IblBake.hlsl"), Dx12CubeTarget.Fmt);
        prefilterPso = FsqPso(iblRootSig, fsqVs,
            Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, ibl, "PSPrefilter", "IblBake.hlsl"), Dx12CubeTarget.Fmt);
        brdfPso = FsqPso(iblRootSig, fsqVs,
            Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, ibl, "PSBrdf", "IblBake.hlsl"), Format.R16G16_Float);

        int skyCbSize = (System.Runtime.InteropServices.Marshal.SizeOf<ProcSkyConstants>() + 255) & ~255;
        procSkyCb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)(skyCbSize * 6)), ResourceStates.GenericRead);
        procSkyCbMapped = procSkyCb.Map<byte>(0);

        iblSlotSize = (System.Runtime.InteropServices.Marshal.SizeOf<IblConstants>() + 255) & ~255;
        iblSlotCount = 6 * (PrefilterMips + 1) + 1;
        iblCb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)(iblSlotSize * iblSlotCount)), ResourceStates.GenericRead);
        iblCbMapped = iblCb.Map<byte>(0);

        envSrvVisible = new Dx12DescriptorHeap(dev,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 1, shaderVisible: true);

        envCube = new Dx12CubeTarget(dev, EnvRes);
        irradianceCube = new Dx12CubeTarget(dev, IrradianceRes);
        prefilterCube = new Dx12CubeTarget(dev, PrefilterRes, PrefilterMips);

        var lutDesc = ResourceDescription.Texture2D(Format.R16G16_Float, BrdfRes, BrdfRes, 1, 1);
        lutDesc.Flags = ResourceFlags.AllowRenderTarget;
        brdfLut = dev.Device.CreateCommittedResource(HeapProperties.DefaultHeapProperties, HeapFlags.None,
            lutDesc, ResourceStates.PixelShaderResource);
        brdfLut.Name = "BrdfLut";
        brdfSrvIndex = Dx12Backend.SrvStore.Allocate();
        dev.Device.CreateShaderResourceView(brdfLut, new ShaderResourceViewDescription {
            Format = Format.R16G16_Float, ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Texture2D = new Texture2DShaderResourceView { MipLevels = 1, MostDetailedMip = 0 },
        }, Dx12Backend.SrvStore.Cpu(brdfSrvIndex));

        brdfRtvHeap = dev.Device.CreateDescriptorHeap(new DescriptorHeapDescription(
            DescriptorHeapType.RenderTargetView, 1));
        brdfRtv = brdfRtvHeap.GetCPUDescriptorHandleForHeapStart();
        dev.Device.CreateRenderTargetView(brdfLut, null, brdfRtv);
    }

    ID3D12PipelineState FsqPso(ID3D12RootSignature rs, byte[] vs, byte[] ps, Format rtFmt) {
        return dev.Device.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription {
            RootSignature = rs, VertexShader = vs, PixelShader = ps, InputLayout = null,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle, SampleMask = uint.MaxValue,
            RasterizerState = RasterizerDescription.CullNone, BlendState = BlendDescription.Opaque,
            DepthStencilState = DepthStencilDescription.None,
            RenderTargetFormats = new[] { rtFmt }, DepthStencilFormat = Format.Unknown,
            SampleDescription = new SampleDescription(1, 0),
        });
    }

    public unsafe void EnsureBaked(ProceduralSky sky, Vector3 sunDir, Vector3 sunRadiance, float sunAngularRadius) {
        float cloudTime = Dx12SkyCloudParams.CloudTime(sky);
        int stamp = System.HashCode.Combine(
            System.HashCode.Combine(sunDir.X, sunDir.Y, sunDir.Z, sunRadiance.X),
            System.HashCode.Combine(sky.AirDensity, sky.Haze, sky.OzoneDensity, sky.MultipleScattering),
            System.HashCode.Combine(sky.Exposure, sky.SunDiskIntensity, sky.GroundColor.X, sunAngularRadius),
            System.HashCode.Combine(sky.CloudsEnabled, sky.CloudCoverage, sky.CloudDensity, sky.CloudAltitude),
            System.HashCode.Combine(sky.CloudThickness, sky.CloudScale, sky.CloudDetail, sky.CloudAmbient),
            System.HashCode.Combine(sky.CirrusCoverage, sky.StarIntensity, sky.CloudWindDirection, cloudTime));
        if (stamp == paramStamp && HasBaked) return;
        paramStamp = stamp;

        Vector3 windOffset = Dx12SkyCloudParams.WindOffset(sky, cloudTime);
        float windAngle = Dx12SkyCloudParams.WindRadians(sky);

        int skyCbSize = (System.Runtime.InteropServices.Marshal.SizeOf<ProcSkyConstants>() + 255) & ~255;
        for (int face = 0; face < 6; face++) {
            var sc = new ProcSkyConstants {
                ViewProjNoTranslate = Matrix4x4.Identity,
                SunDirection = sunDir, SunAngularRadius = MathF.Max(sunAngularRadius, 1e-4f),
                SunRadiance = sunRadiance, SunDiskIntensity = MathF.Max(sky.SunDiskIntensity, 0f),
                GroundAlbedo = new Vector3(sky.GroundColor.X, sky.GroundColor.Y, sky.GroundColor.Z),
                AirDensity = MathF.Max(sky.AirDensity, 0f), Haze = MathF.Max(sky.Haze, 0f),
                HazeAnisotropy = Math.Clamp(sky.HazeAnisotropy, 0f, 0.99f),
                OzoneDensity = MathF.Max(sky.OzoneDensity, 0f), MultiScatter = MathF.Max(sky.MultipleScattering, 1f),
                Exposure = MathF.Max(sky.Exposure, 0f), BakeFace = face,
                CloudsEnabled = sky.CloudsEnabled ? 1f : 0f, CloudCoverage = Math.Clamp(sky.CloudCoverage, 0f, 1f),
                CloudDensity = MathF.Max(sky.CloudDensity, 0f),
                CloudAltitude = Math.Clamp(sky.CloudAltitude, 600f, 20000f),
                CloudThickness = Math.Clamp(sky.CloudThickness, 100f, 20000f),
                CloudScale = MathF.Max(sky.CloudScale, 0.05f), CloudDetail = Math.Clamp(sky.CloudDetail, 0f, 1f),
                CloudAmbient = MathF.Max(sky.CloudAmbient, 0f),
                CloudWindOffset = windOffset, CloudWindAngle = windAngle,
                CirrusCoverage = Math.Clamp(sky.CirrusCoverage, 0f, 1f),
                StarIntensity = MathF.Max(sky.StarIntensity, 0f),
            };
            *(ProcSkyConstants*)(procSkyCbMapped + face * skyCbSize) = sc;
        }

        dev.ExecuteUpload(cl => {
            for (int face = 0; face < 6; face++) {
                envCube.RenderFace(cl, face, 0, c => {
                    c.SetGraphicsRootSignature(envRootSig);
                    c.SetPipelineState(envPso);
                    c.SetGraphicsRootConstantBufferView(0, procSkyCb.GPUVirtualAddress + (ulong)(face * skyCbSize));
                    c.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                    c.DrawInstanced(3, 1, 0, 0);
                });
            }
            envCube.ToShaderResource(cl);

            dev.Device.CopyDescriptorsSimple(1, envSrvVisible.Cpu(0), envCube.SrvCpu,
                DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
            cl.SetDescriptorHeaps(envSrvVisible.Heap);

            int slot = 0;
            for (int face = 0; face < 6; face++) {
                var ic = new IblConstants { Face = face, Roughness = 0, SourceResolution = EnvRes };
                *(IblConstants*)(iblCbMapped + (long)slot * iblSlotSize) = ic;
                ulong cbAddr = iblCb.GPUVirtualAddress + (ulong)((long)slot * iblSlotSize);
                slot++;
                irradianceCube.RenderFace(cl, face, 0, c => {
                    c.SetGraphicsRootSignature(iblRootSig);
                    c.SetPipelineState(irradiancePso);
                    c.SetGraphicsRootConstantBufferView(0, cbAddr);
                    c.SetGraphicsRootDescriptorTable(1, envSrvVisible.Gpu(0));
                    c.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                    c.DrawInstanced(3, 1, 0, 0);
                });
            }
            irradianceCube.ToShaderResource(cl);

            for (int mip = 0; mip < PrefilterMips; mip++) {
                float roughness = PrefilterMips == 1 ? 0f : (float)mip / (PrefilterMips - 1);
                for (int face = 0; face < 6; face++) {
                    var pc = new IblConstants { Face = face, Roughness = roughness, SourceResolution = EnvRes };
                    *(IblConstants*)(iblCbMapped + (long)slot * iblSlotSize) = pc;
                    ulong cbAddr = iblCb.GPUVirtualAddress + (ulong)((long)slot * iblSlotSize);
                    slot++;
                    prefilterCube.RenderFace(cl, face, mip, c => {
                        c.SetGraphicsRootSignature(iblRootSig);
                        c.SetPipelineState(prefilterPso);
                        c.SetGraphicsRootConstantBufferView(0, cbAddr);
                        c.SetGraphicsRootDescriptorTable(1, envSrvVisible.Gpu(0));
                        c.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                        c.DrawInstanced(3, 1, 0, 0);
                    });
                }
            }
            prefilterCube.ToShaderResource(cl);

            cl.ResourceBarrierTransition(brdfLut, ResourceStates.PixelShaderResource, ResourceStates.RenderTarget);
            cl.RSSetViewport(0, 0, BrdfRes, BrdfRes);
            cl.RSSetScissorRect(BrdfRes, BrdfRes);
            cl.OMSetRenderTargets(brdfRtv);
            cl.SetGraphicsRootSignature(iblRootSig);
            cl.SetPipelineState(brdfPso);
            cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            cl.DrawInstanced(3, 1, 0, 0);
            cl.ResourceBarrierTransition(brdfLut, ResourceStates.RenderTarget, ResourceStates.PixelShaderResource);
        });

        HasBaked = true;
    }

    ID3D12DescriptorHeap brdfRtvHeap;
    CpuDescriptorHandle brdfRtv;

    public void Dispose() {
        envCube?.Dispose(); irradianceCube?.Dispose(); prefilterCube?.Dispose();
        brdfRtvHeap?.Dispose(); brdfLut?.Dispose();
    }
}
