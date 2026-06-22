using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Dxc;
using Vortice.Mathematics;

namespace BallisticEngine.DX12;

public static class Dx12DxrProbe {
    const int W = 32, H = 32;

    public static unsafe bool SelfTest(Dx12Device dev) {
        try {
            var opt5 = dev.Device.CheckFeatureSupport<FeatureDataD3D12Options5>(Vortice.Direct3D12.Feature.Options5);
            if (opt5.RaytracingTier < RaytracingTier.Tier1_0) {
                Console.WriteLine($"[DxrTest] DXR not supported (tier {opt5.RaytracingTier})"); return false;
            }
            Console.WriteLine($"[DxrTest] DXR {opt5.RaytracingTier}");
            using ID3D12Device5 device5 = dev.Device.QueryInterface<ID3D12Device5>();

            ID3D12Resource Buf(ulong size, ResourceFlags flags, ResourceStates st, HeapProperties heap) =>
                dev.Device.CreateCommittedResource(heap, HeapFlags.None,
                    ResourceDescription.Buffer(size, flags), st);

            float[] verts = { -0.6f, -0.6f, 1.0f,   0.6f, -0.6f, 1.0f,   0.0f, 0.7f, 1.0f };
            ID3D12Resource vb = Buf((ulong)(verts.Length * 4), ResourceFlags.None,
                ResourceStates.GenericRead, HeapProperties.UploadHeapProperties);
            { byte* p = vb.Map<byte>(0); fixed (float* s = verts) Unsafe.CopyBlock(p, s, (uint)(verts.Length * 4)); vb.Unmap(0); }

            var geom = new RaytracingGeometryDescription {
                Type = RaytracingGeometryType.Triangles,
                Flags = RaytracingGeometryFlags.Opaque,
                Triangles = new RaytracingGeometryTrianglesDescription {
                    VertexBuffer = new GpuVirtualAddressAndStride(vb.GPUVirtualAddress, 12),
                    VertexFormat = Format.R32G32B32_Float, VertexCount = 3,
                    IndexBuffer = 0, IndexFormat = Format.Unknown, IndexCount = 0, Transform3x4 = 0,
                },
            };
            var blasInputs = new BuildRaytracingAccelerationStructureInputs {
                Type = RaytracingAccelerationStructureType.BottomLevel, Layout = ElementsLayout.Array,
                Flags = RaytracingAccelerationStructureBuildFlags.PreferFastTrace,
                DescriptorsCount = 1, GeometryDescriptions = new[] { geom },
            };
            var blasPre = device5.GetRaytracingAccelerationStructurePrebuildInfo(blasInputs);
            ID3D12Resource blasScratch = Buf(blasPre.ScratchDataSizeInBytes, ResourceFlags.AllowUnorderedAccess,
                ResourceStates.UnorderedAccess, HeapProperties.DefaultHeapProperties);
            ID3D12Resource blas = Buf(blasPre.ResultDataMaxSizeInBytes, ResourceFlags.AllowUnorderedAccess,
                ResourceStates.RaytracingAccelerationStructure, HeapProperties.DefaultHeapProperties);

            var inst = new RaytracingInstanceDescription {
                Transform = new Matrix3x4(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0),
                InstanceMask = 0xFF, AccelerationStructure = blas.GPUVirtualAddress,
            };
            ID3D12Resource instBuf = Buf((ulong)Marshal.SizeOf<RaytracingInstanceDescription>(),
                ResourceFlags.None, ResourceStates.GenericRead, HeapProperties.UploadHeapProperties);
            { byte* p = instBuf.Map<byte>(0); Marshal.StructureToPtr(inst, (IntPtr)p, false); instBuf.Unmap(0); }

            var tlasInputs = new BuildRaytracingAccelerationStructureInputs {
                Type = RaytracingAccelerationStructureType.TopLevel, Layout = ElementsLayout.Array,
                Flags = RaytracingAccelerationStructureBuildFlags.PreferFastTrace,
                DescriptorsCount = 1, InstanceDescriptions = instBuf.GPUVirtualAddress,
            };
            var tlasPre = device5.GetRaytracingAccelerationStructurePrebuildInfo(tlasInputs);
            ID3D12Resource tlasScratch = Buf(tlasPre.ScratchDataSizeInBytes, ResourceFlags.AllowUnorderedAccess,
                ResourceStates.UnorderedAccess, HeapProperties.DefaultHeapProperties);
            ID3D12Resource tlas = Buf(tlasPre.ResultDataMaxSizeInBytes, ResourceFlags.AllowUnorderedAccess,
                ResourceStates.RaytracingAccelerationStructure, HeapProperties.DefaultHeapProperties);

            dev.ExecuteSync(cl => {
                cl.BuildRaytracingAccelerationStructure(new BuildRaytracingAccelerationStructureDescription {
                    Inputs = blasInputs, DestinationAccelerationStructureData = blas.GPUVirtualAddress,
                    ScratchAccelerationStructureData = blasScratch.GPUVirtualAddress,
                });
                cl.ResourceBarrier(new ResourceBarrier(new ResourceUnorderedAccessViewBarrier(blas)));
                cl.BuildRaytracingAccelerationStructure(new BuildRaytracingAccelerationStructureDescription {
                    Inputs = tlasInputs, DestinationAccelerationStructureData = tlas.GPUVirtualAddress,
                    ScratchAccelerationStructureData = tlasScratch.GPUVirtualAddress,
                });
                cl.ResourceBarrier(new ResourceBarrier(new ResourceUnorderedAccessViewBarrier(tlas)));
            });

            var outDesc = ResourceDescription.Texture2D(Format.R8G8B8A8_UNorm, W, H, 1, 1);
            outDesc.Flags = ResourceFlags.AllowUnorderedAccess;
            ID3D12Resource outTex = dev.Device.CreateCommittedResource(HeapProperties.DefaultHeapProperties,
                HeapFlags.None, outDesc, ResourceStates.Common);
            var heap = new Dx12DescriptorHeap(dev,
                DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 2, shaderVisible: true);
            dev.Device.CreateShaderResourceView(null, new ShaderResourceViewDescription {
                Format = Format.Unknown, ViewDimension = ShaderResourceViewDimension.RaytracingAccelerationStructure,
                Shader4ComponentMapping = ShaderComponentMapping.Default,
                RaytracingAccelerationStructure = new RaytracingAccelerationStructureShaderResourceView { Location = tlas.GPUVirtualAddress },
            }, heap.Cpu(0));
            dev.Device.CreateUnorderedAccessView(outTex, null, new UnorderedAccessViewDescription {
                Format = Format.R8G8B8A8_UNorm, ViewDimension = UnorderedAccessViewDimension.Texture2D,
            }, heap.Cpu(1));

            var srvRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 1, 0);
            var uavRange = new DescriptorRange1(DescriptorRangeType.UnorderedAccessView, 1, 0);
            using ID3D12RootSignature globalRS = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
                new RootSignatureDescription1(RootSignatureFlags.None,
                    new[] { new RootParameter1(new RootDescriptorTable1(srvRange, uavRange), ShaderVisibility.All) })));

            string hlsl = EmbeddedShaderSource.ReadHlsl("DxrProbe.hlsl");
            byte[] dxil = Dx12ShaderCompiler.Compile(DxcShaderStage.Library, hlsl, "", "DxrProbe.hlsl");
            var subs = new[] {
                new StateSubObject(new DxilLibraryDescription(dxil,
                    new ExportDescription("RayGen"), new ExportDescription("Miss"), new ExportDescription("ClosestHit"))),
                new StateSubObject(new HitGroupDescription("HitGroup", HitGroupType.Triangles, "", "ClosestHit", "")),
                new StateSubObject(new RaytracingShaderConfig(16, 8)),
                new StateSubObject(new RaytracingPipelineConfig(1)),
                new StateSubObject(new GlobalRootSignature(globalRS)),
            };
            using ID3D12StateObject pso = device5.CreateStateObject(
                new StateObjectDescription(StateObjectType.RaytracingPipeline, subs));

            using ID3D12StateObjectProperties props = pso.QueryInterface<ID3D12StateObjectProperties>();
            uint idSize = D3D12.ShaderIdentifierSizeInBytes;
            const int slot = 64;
            ID3D12Resource sbt = Buf(slot * 3, ResourceFlags.None, ResourceStates.GenericRead, HeapProperties.UploadHeapProperties);
            {
                byte* p = sbt.Map<byte>(0);
                Unsafe.CopyBlock(p + 0 * slot, (void*)props.GetShaderIdentifier("RayGen"), idSize);
                Unsafe.CopyBlock(p + 1 * slot, (void*)props.GetShaderIdentifier("Miss"), idSize);
                Unsafe.CopyBlock(p + 2 * slot, (void*)props.GetShaderIdentifier("HitGroup"), idSize);
                sbt.Unmap(0);
            }

            dev.ExecuteSync(cl => {
                cl.ResourceBarrierTransition(outTex, ResourceStates.Common, ResourceStates.UnorderedAccess);
                cl.SetDescriptorHeaps(heap.Heap);
                cl.SetComputeRootSignature(globalRS);
                cl.SetPipelineState1(pso);
                cl.SetComputeRootDescriptorTable(0, heap.Gpu(0));
                cl.DispatchRays(new DispatchRaysDescription {
                    Width = W, Height = H, Depth = 1,
                    RayGenerationShaderRecord = new GpuVirtualAddressRange { StartAddress = sbt.GPUVirtualAddress, SizeInBytes = idSize },
                    MissShaderTable = new GpuVirtualAddressRangeAndStride { StartAddress = sbt.GPUVirtualAddress + slot, SizeInBytes = idSize, StrideInBytes = idSize },
                    HitGroupTable = new GpuVirtualAddressRangeAndStride { StartAddress = sbt.GPUVirtualAddress + 2 * slot, SizeInBytes = idSize, StrideInBytes = idSize },
                });
            });

            var fps = new PlacedSubresourceFootPrint[1]; var rc = new uint[1]; var rs = new ulong[1];
            dev.Device.GetCopyableFootprints(outTex.Description, 0, 1, 0, fps, rc, rs, out ulong total);
            int rowPitch = (int)fps[0].Footprint.RowPitch;
            using ID3D12Resource readback = Buf(total, ResourceFlags.None, ResourceStates.CopyDest, HeapProperties.ReadbackHeapProperties);
            dev.ExecuteSync(cl => {
                cl.ResourceBarrierTransition(outTex, ResourceStates.UnorderedAccess, ResourceStates.CopySource);
                cl.CopyTextureRegion(new TextureCopyLocation(readback, fps[0]), 0, 0, 0, new TextureCopyLocation(outTex, 0), null);
            });
            int red = 0, blue = 0;
            byte* m = readback.Map<byte>(0);
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++) {
                    byte* px = m + y * rowPitch + x * 4;
                    if (px[0] > 200 && px[1] < 60 && px[2] < 60) red++;
                    else if (px[2] > 200 && px[0] < 60 && px[1] < 60) blue++;
                }
            readback.Unmap(0);
            Console.WriteLine($"[DxrTest] traced {W}x{H}: hit(red)={red}  miss(blue)={blue}");

            blas.Dispose(); blasScratch.Dispose(); tlas.Dispose(); tlasScratch.Dispose();
            vb.Dispose(); instBuf.Dispose(); sbt.Dispose(); outTex.Dispose(); readback.Dispose(); heap.Dispose();
            return red > 0 && blue > 0;
        } catch (Exception e) {
            Console.WriteLine($"[DxrTest] FAILED: {e.Message}\n{e.StackTrace}");
            return false;
        }
    }
}
