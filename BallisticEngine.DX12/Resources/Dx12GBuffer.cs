using System;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace BallisticEngine.DX12;

// The fat G-buffer for the DX12 clustered-deferred renderer. The geometry pass writes 4 color MRTs +
// depth; the deferred lighting pass reads them back as SRVs to shade one screen-space pass. This is the
// DX12 equivalent of a deferred G-buffer FBO — a multi-RTV target where the depth is the SAME typeless
// resource the lighting/SSAO/fog passes read (D32 DSV + R32_Float SRV), so world position reconstructs
// from depth with no separate position target.
//
// Layout (matches GBuffer.hlsl's GBufferOut):
//   RT0 R8G8B8A8_UNorm_SRGB  : albedo.rgb  (a = specularReflectance for F0)
//   RT1 R16G16B16A16_Float   : world normal.xyz packed [0,1] (a = unused)
//   RT2 R8G8B8A8_UNorm       : metallic, roughness, ao, (a = cutout flag)
//   RT3 R16G16B16A16_Float   : emissive radiance.rgb (HDR, added directly in lighting)
//   RT4 R16G16_Float         : screen-space motion vectors (prevUV - currUV, UNJITTERED) — TAA + FSR
// RT0 is sRGB so the SRGB albedo round-trips like a sampled diffuse map; normal/emissive are float so the
// normal keeps precision and emissive stays HDR. The metallic/roughness/ao pack is linear UNORM. Motion is
// linear RG (UV-space delta, top-left origin); FSR consumes it with motionVectorScale = (renderW, renderH).
public sealed class Dx12GBuffer : IDisposable {
    public const int RtCount = 5;          // total MRTs (4 shaded + 1 motion)
    public const int ShadedRtCount = 4;    // G0..G3 — the surface attributes the deferred lighting pass reads
    public const int MotionRtIndex = 4;    // RG16F screen-space motion (TAA reprojection + FSR upscaler)
    public static readonly Format[] ColorFormats = {
        Format.R8G8B8A8_UNorm_SRgb,     // albedo + specF0
        Format.R16G16B16A16_Float,      // world normal
        Format.R8G8B8A8_UNorm,          // metallic/roughness/ao/flags
        Format.R16G16B16A16_Float,      // emissive (HDR)
        Format.R16G16_Float,            // motion vectors (prevUV - currUV)
    };
    public const Format DepthFormat = Format.D32_Float;

    public int Width { get; }
    public int Height { get; }

    readonly Dx12Device dev;
    readonly ID3D12Resource[] colors = new ID3D12Resource[RtCount];
    readonly ID3D12DescriptorHeap rtvHeap;     // RtCount contiguous RTVs
    readonly uint rtvInc;
    readonly int[] colorSrv = new int[RtCount]; // persistent SRV indices in Dx12Backend.SrvStore
    readonly ResourceStates[] colorState = new ResourceStates[RtCount];

    readonly ID3D12Resource depth;
    readonly ID3D12DescriptorHeap dsvHeap;
    readonly CpuDescriptorHandle dsvHandle;
    int depthSrv = -1;
    ResourceStates depthState = ResourceStates.DepthWrite;

    public Dx12GBuffer(Dx12Device device, int width, int height) {
        dev = device;
        Width = width;
        Height = height;

        rtvHeap = dev.Device.CreateDescriptorHeap(new DescriptorHeapDescription(
            DescriptorHeapType.RenderTargetView, RtCount));
        rtvInc = dev.Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);
        CpuDescriptorHandle rtvStart = rtvHeap.GetCPUDescriptorHandleForHeapStart();

        for (int i = 0; i < RtCount; i++) {
            Format f = ColorFormats[i];
            var rtDesc = ResourceDescription.Texture2D(f, (uint)width, (uint)height, mipLevels: 1, arraySize: 1);
            rtDesc.Flags = ResourceFlags.AllowRenderTarget;
            var clearVal = new ClearValue(f, new Vortice.Mathematics.Color4(0, 0, 0, 0));
            colors[i] = dev.Device.CreateCommittedResource(
                HeapProperties.DefaultHeapProperties, HeapFlags.None, rtDesc,
                ResourceStates.RenderTarget, clearVal);
            colors[i].Name = $"GBuffer{i}";
            colorState[i] = ResourceStates.RenderTarget;
            dev.Device.CreateRenderTargetView(colors[i], null, RtvHandle(i));

            colorSrv[i] = Dx12Backend.SrvStore.Allocate();
            dev.Device.CreateShaderResourceView(colors[i], new ShaderResourceViewDescription {
                Format = f,
                ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
                Shader4ComponentMapping = ShaderComponentMapping.Default,
                Texture2D = new Texture2DShaderResourceView { MipLevels = 1, MostDetailedMip = 0 },
            }, Dx12Backend.SrvStore.Cpu(colorSrv[i]));
        }

        // Depth: typeless so it serves a D32 DSV (geometry write) and an R32_Float SRV (lighting/SSAO/fog).
        var dDesc = ResourceDescription.Texture2D(Format.R32_Typeless, (uint)width, (uint)height,
            mipLevels: 1, arraySize: 1);
        dDesc.Flags = ResourceFlags.AllowDepthStencil;
        var dClear = new ClearValue(DepthFormat, 1.0f, 0);
        depth = dev.Device.CreateCommittedResource(
            HeapProperties.DefaultHeapProperties, HeapFlags.None, dDesc,
            ResourceStates.DepthWrite, dClear);
        depth.Name = "GBufferDepth";
        dsvHeap = dev.Device.CreateDescriptorHeap(new DescriptorHeapDescription(
            DescriptorHeapType.DepthStencilView, 1));
        dsvHandle = dsvHeap.GetCPUDescriptorHandleForHeapStart();
        dev.Device.CreateDepthStencilView(depth, new DepthStencilViewDescription {
            Format = DepthFormat, ViewDimension = DepthStencilViewDimension.Texture2D,
        }, dsvHandle);

        depthSrv = Dx12Backend.SrvStore.Allocate();
        dev.Device.CreateShaderResourceView(depth, new ShaderResourceViewDescription {
            Format = Format.R32_Float,
            ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Texture2D = new Texture2DShaderResourceView { MipLevels = 1, MostDetailedMip = 0 },
        }, Dx12Backend.SrvStore.Cpu(depthSrv));
    }

    public CpuDescriptorHandle ColorSrvCpu(int i) => Dx12Backend.SrvStore.Cpu(colorSrv[i]);
    public CpuDescriptorHandle DepthSrvCpu => Dx12Backend.SrvStore.Cpu(depthSrv);
    public CpuDescriptorHandle DsvHandle => dsvHandle;
    public ID3D12Resource DepthResource => depth;

    CpuDescriptorHandle RtvHandle(int i) => new(rtvHeap.GetCPUDescriptorHandleForHeapStart(), i, rtvInc);

    // Geometry pass: transition all 4 RTs + depth to write state, bind them, clear, run the draws. One
    // ExecuteSync — the whole G-buffer fill in a single command-list submission (mirrors RenderIntoCleared).
    public void RenderGeometry(Action<ID3D12GraphicsCommandList4> record) {
        dev.ExecuteSync(cl => {
            for (int i = 0; i < RtCount; i++) ColorTransition(cl, i, ResourceStates.RenderTarget);
            DepthTransition(cl, ResourceStates.DepthWrite);
            cl.RSSetViewport(0, 0, Width, Height);
            cl.RSSetScissorRect(Width, Height);
            Span<CpuDescriptorHandle> rtvs = stackalloc CpuDescriptorHandle[RtCount];
            for (int i = 0; i < RtCount; i++) {
                rtvs[i] = RtvHandle(i);
                cl.ClearRenderTargetView(rtvs[i], new Vortice.Mathematics.Color4(0, 0, 0, 0));
            }
            cl.ClearDepthStencilView(dsvHandle, ClearFlags.Depth, 1.0f, 0);
            cl.OMSetRenderTargets(rtvs, dsvHandle);
            record(cl);
        });
    }

    // Transition every G-buffer color + depth to PixelShaderResource so the deferred lighting pass (and
    // SSAO) can sample them. Single ExecuteSync — all barriers batched.
    public void ToShaderResource() {
        dev.ExecuteSync(cl => {
            for (int i = 0; i < RtCount; i++) ColorTransition(cl, i, ResourceStates.PixelShaderResource);
            DepthTransition(cl, ResourceStates.PixelShaderResource);
        });
    }

    // After the deferred lighting pass, the sky draws at the far plane and must depth-test against the
    // G-buffer depth. Transition depth back to a readable-for-DSV-bind state. The sky binds the HDR color
    // RTV + this depth (read-only via LessEqual + no-write); leave colors as SRVs (they're done being read
    // by then for normal use, but keep transitions explicit). Returns depth to DepthRead for the sky test.
    public void DepthToReadOnly() => dev.ExecuteSync(cl => DepthTransition(cl, ResourceStates.DepthRead));
    public void DepthToWrite() => dev.ExecuteSync(cl => DepthTransition(cl, ResourceStates.DepthWrite));
    public void DepthToShaderResource() => dev.ExecuteSync(cl => DepthTransition(cl, ResourceStates.PixelShaderResource));
    // Compute-readable (for the Hi-Z pyramid build, which reads the previous frame's depth in a CS).
    public void DepthToNonPixelShaderResource() => dev.ExecuteSync(cl => DepthTransition(cl, ResourceStates.NonPixelShaderResource));

    void ColorTransition(ID3D12GraphicsCommandList4 cl, int i, ResourceStates target) {
        if (colorState[i] == target) return;
        cl.ResourceBarrierTransition(colors[i], colorState[i], target);
        colorState[i] = target;
    }
    void DepthTransition(ID3D12GraphicsCommandList4 cl, ResourceStates target) {
        if (depthState == target) return;
        cl.ResourceBarrierTransition(depth, depthState, target);
        depthState = target;
    }

    public void Dispose() {
        dsvHeap.Dispose();
        depth.Dispose();
        rtvHeap.Dispose();
        for (int i = 0; i < RtCount; i++) colors[i].Dispose();
    }
}
