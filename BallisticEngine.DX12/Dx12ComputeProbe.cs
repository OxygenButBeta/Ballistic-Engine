using Vortice.Direct3D12;
using Vortice.Dxc;

namespace BallisticEngine.DX12;

public static class Dx12ComputeProbe {
    public static bool SelfTest(Dx12Device dev) {
        const int Count = 256;
        bool ok = true;
        try {
            var countConst = new RootParameter1(new RootConstants(0, 0, 1), ShaderVisibility.All);
            var uav0 = new RootParameter1(RootParameterType.UnorderedAccessView, new RootDescriptor1(0, 0), ShaderVisibility.All);
            var uav1 = new RootParameter1(RootParameterType.UnorderedAccessView, new RootDescriptor1(1, 0), ShaderVisibility.All);
            using ID3D12RootSignature rootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
                new RootSignatureDescription1(RootSignatureFlags.None, new[] { countConst, uav0, uav1 })));

            string hlsl = EmbeddedShaderSource.ReadHlsl("ComputeProbe.hlsl");
            byte[] cs = Dx12ShaderCompiler.Compile(DxcShaderStage.Compute, hlsl, "CSMain", "ComputeProbe.hlsl");
            using ID3D12PipelineState pso = dev.Device.CreateComputePipelineState(
                new ComputePipelineStateDescription { RootSignature = rootSig, ComputeShader = cs });

            var zerosOut = new uint[Count];
            var zerosCounter = new uint[1];
            using ID3D12Resource output = dev.CreateUavBuffer<uint>(zerosOut, ResourceStates.UnorderedAccess);
            using ID3D12Resource counter = dev.CreateUavBuffer<uint>(zerosCounter, ResourceStates.UnorderedAccess);
            using ID3D12Resource outputRb = dev.CreateReadbackBuffer(Count * sizeof(uint));
            using ID3D12Resource counterRb = dev.CreateReadbackBuffer(sizeof(uint));

            dev.ExecuteSync(cl => {
                cl.SetComputeRootSignature(rootSig);
                cl.SetPipelineState(pso);
                cl.SetComputeRoot32BitConstant(0, Count, 0);
                cl.SetComputeRootUnorderedAccessView(1, output.GPUVirtualAddress);
                cl.SetComputeRootUnorderedAccessView(2, counter.GPUVirtualAddress);
                cl.Dispatch((Count + 63) / 64, 1, 1);
                cl.ResourceBarrierTransition(output, ResourceStates.UnorderedAccess, ResourceStates.CopySource);
                cl.ResourceBarrierTransition(counter, ResourceStates.UnorderedAccess, ResourceStates.CopySource);
                cl.CopyBufferRegion(outputRb, 0, output, 0, (ulong)(Count * sizeof(uint)));
                cl.CopyBufferRegion(counterRb, 0, counter, 0, sizeof(uint));
            });

            Span<uint> o = outputRb.Map<uint>(0, Count);
            int bad = 0;
            for (uint i = 0; i < Count; i++)
                if (o[(int)i] != i * 2u + 1u) bad++;
            outputRb.Unmap(0);

            Span<uint> cc = counterRb.Map<uint>(0, 1);
            uint counterVal = cc[0];
            counterRb.Unmap(0);

            uint expectedEven = (Count + 1) / 2;
            if (bad != 0) { ok = false; Console.WriteLine($"[Dx12ComputeProbe] FAIL: {bad}/{Count} output elements wrong."); }
            if (counterVal != expectedEven) { ok = false; Console.WriteLine($"[Dx12ComputeProbe] FAIL: counter={counterVal}, expected {expectedEven}."); }
        } catch (Exception e) {
            ok = false;
            Console.WriteLine($"[Dx12ComputeProbe] FAIL (exception): {e.Message}");
            Console.WriteLine(dev.DrainDebugMessages());
        }

        Console.WriteLine(ok
            ? "[Dx12ComputeProbe] PASS: compute PSO + root UAVs + InterlockedAdd + readback verified."
            : "[Dx12ComputeProbe] FAIL: compute foundation broken (see above).");
        return ok;
    }
}
