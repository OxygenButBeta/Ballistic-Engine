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
    // BANDWIDTH PACK (BALLISTIC_DX12_GBUFFER_PACK=1, opt-in): normal RGBA16F(8B)→RGB10A2_UNorm(4B) and
    // emissive RGBA16F(8B)→R11G11B10_Float(4B). The geo-pass MRT write drops 8B/px and every reader (GTAO/SSR/
    // RTAO/deferred/Lumen) reads less. RGB10A2 is chosen over an oct-encode so NO reader shader changes: the
    // normal is already stored [0,1] (N*0.5+0.5) and HW normalises RGB10A2 reads back to [0,1] — the existing
    // rgb*2-1 decode works verbatim. Emissive R11G11B10F max ≈ 65024 covers material emissive (not the EXR sun,
    // which is in SceneColor, not the G-buffer). Read ONCE at static init so RTV/SRV/UAV/PSO all derive the same
    // format from this array; default OFF keeps the shipping path byte-identical.
    static readonly bool Pack = Environment.GetEnvironmentVariable("BALLISTIC_DX12_GBUFFER_PACK") == "1";
    public static readonly Format[] ColorFormats = {
        Format.R8G8B8A8_UNorm_SRgb,                                   // albedo + specF0
        Pack ? Format.R10G10B10A2_UNorm : Format.R16G16B16A16_Float,  // world normal
        Format.R8G8B8A8_UNorm,                                        // metallic/roughness/ao/flags
        Pack ? Format.R11G11B10_Float   : Format.R16G16B16A16_Float,  // emissive (HDR)
        Format.R16G16_Float,                                          // motion vectors (prevUV - currUV)
    };
    public const Format DepthFormat = Format.D32_Float;

    // R5: the typeless storage format for each shaded color (resource is typeless; RTV/SRV use the shaded format,
    // the resolve UAV uses the non-SRGB UNORM aliasing format).
    static Format TypelessOf(Format shaded) => shaded switch {
        Format.R8G8B8A8_UNorm_SRgb => Format.R8G8B8A8_Typeless,
        Format.R8G8B8A8_UNorm      => Format.R8G8B8A8_Typeless,
        Format.R16G16B16A16_Float  => Format.R16G16B16A16_Typeless,
        Format.R16G16_Float        => Format.R16G16_Typeless,
        Format.R10G10B10A2_UNorm   => Format.R10G10B10A2_Typeless,   // packed normal (no _Typeless for R11G11B10F → default)
        _ => shaded,
    };
    // The format the resolve UAV writes through (SRGB → plain UNORM; floats unchanged).
    public static Format UavFormatOf(Format shaded) => shaded switch {
        Format.R8G8B8A8_UNorm_SRgb => Format.R8G8B8A8_UNorm,
        _ => shaded,
    };

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

        // R5: each color is created TYPELESS so the SAME resource serves a shaded-format RTV/SRV (the raster path +
        // lighting, unchanged) AND a non-SRGB UNORM UAV (the visibility-buffer resolve compute writes through it —
        // a UAV can't be SRGB). The typeless resource is byte-for-byte the shaded format; only the views differ.
        // When R5 is off this is invisible (the RTV/SRV use the shaded format exactly as before).
        for (int i = 0; i < RtCount; i++) {
            Format shaded = ColorFormats[i];
            Format typeless = TypelessOf(shaded);
            var rtDesc = ResourceDescription.Texture2D(typeless, (uint)width, (uint)height, mipLevels: 1, arraySize: 1);
            rtDesc.Flags = ResourceFlags.AllowRenderTarget | ResourceFlags.AllowUnorderedAccess;
            var clearVal = new ClearValue(shaded, new Vortice.Mathematics.Color4(0, 0, 0, 0));
            colors[i] = dev.Device.CreateCommittedResource(
                HeapProperties.DefaultHeapProperties, HeapFlags.None, rtDesc,
                ResourceStates.RenderTarget, clearVal);
            colors[i].Name = $"GBuffer{i}";
            colorState[i] = ResourceStates.RenderTarget;
            // RTV + SRV in the SHADED format (SRGB for RT0) — the raster path + lighting are unchanged.
            dev.Device.CreateRenderTargetView(colors[i], new RenderTargetViewDescription {
                Format = shaded, ViewDimension = RenderTargetViewDimension.Texture2D,
            }, RtvHandle(i));

            colorSrv[i] = Dx12Backend.SrvStore.Allocate();
            dev.Device.CreateShaderResourceView(colors[i], new ShaderResourceViewDescription {
                Format = shaded,
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
    public ID3D12Resource MotionResource => colors[MotionRtIndex];   // RG16F motion (FSR input)

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

    // Transition every G-buffer color + depth to a combined PIXEL|NON_PIXEL shader-resource state so BOTH
    // the deferred/SSAO/SSR pixel passes AND the DXR (compute-stage) ray passes can sample them — RT shaders
    // require NON_PIXEL_SHADER_RESOURCE. The combined state is a superset, so pixel reads stay valid and the
    // output is unchanged (a barrier never affects pixels). Single ExecuteSync.
    const ResourceStates ShaderRead = ResourceStates.PixelShaderResource | ResourceStates.NonPixelShaderResource;
    public void ToShaderResource() {
        dev.ExecuteSync(cl => {
            for (int i = 0; i < RtCount; i++) ColorTransition(cl, i, ShaderRead);
            DepthTransition(cl, ShaderRead);
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

    // Raw G-buffer readback for the agent's "raw perception" (`bal gbuffer`). Copies a G-buffer subresource
    // to a CPU byte array (tightly packed, row-pitch padding removed). Transitions the resource to CopySource
    // and back to its prior state, so it's safe to call after a frame (resources are in ShaderRead state).
    // `which`: -1 = depth (R32_Float, 4 B/px), 0..RtCount-1 = a color MRT (its ColorFormats[which]).
    public unsafe byte[] ReadbackRaw(int which, out int bytesPerPixel) {
        ID3D12Resource res = which < 0 ? depth : colors[which];
        ResourceDescription resDesc = res.Description;
        var fps = new PlacedSubresourceFootPrint[1]; var rc = new uint[1]; var rs = new ulong[1];
        dev.Device.GetCopyableFootprints(resDesc, 0, 1, 0, fps, rc, rs, out ulong totalBytes);
        int rowPitch = (int)fps[0].Footprint.RowPitch;
        int rowBytes = (int)rs[0];
        bytesPerPixel = rowBytes / Width;

        using ID3D12Resource readback = dev.Device.CreateCommittedResource(
            HeapProperties.ReadbackHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer(totalBytes), ResourceStates.CopyDest);

        // Depth's SRV format is R32_Float but the resource is typeless — the copy uses the resource as-is.
        ResourceStates prior = which < 0 ? depthState : colorState[which];
        dev.ExecuteSync(cl => {
            Transition(cl, res, ref prior, ResourceStates.CopySource, which);
            cl.CopyTextureRegion(new TextureCopyLocation(readback, fps[0]), 0, 0, 0,
                new TextureCopyLocation(res, 0), null);
        });
        // Restore the resource's tracked state (prior was mutated by Transition; put it back).
        ResourceStates restoreTo = which < 0 ? ResourceStates.NonPixelShaderResource
                                             : (ResourceStates.PixelShaderResource | ResourceStates.NonPixelShaderResource);
        dev.ExecuteSync(cl => Transition(cl, res, ref prior, restoreTo, which));

        var dst = new byte[(long)Width * Height * bytesPerPixel];
        byte* mapped = readback.Map<byte>(0);
        try {
            for (int y = 0; y < Height; y++)
                System.Runtime.InteropServices.Marshal.Copy(
                    (IntPtr)(mapped + (long)y * rowPitch), dst, y * Width * bytesPerPixel, Width * bytesPerPixel);
        } finally { readback.Unmap(0); }
        return dst;
    }

    void Transition(ID3D12GraphicsCommandList4 cl, ID3D12Resource res, ref ResourceStates cur, ResourceStates target, int which) {
        if (cur == target) return;
        cl.ResourceBarrierTransition(res, cur, target);
        cur = target;
        if (which < 0) depthState = target; else colorState[which] = target;
    }

    void ColorTransition(ID3D12GraphicsCommandList4 cl, int i, ResourceStates target) {
        if (colorState[i] == target) return;
        cl.ResourceBarrierTransition(colors[i], colorState[i], target);
        colorState[i] = target;
    }

    // R5: transition all color RTs to UnorderedAccess (resolve write), then create a non-SRGB UNORM UAV per color
    // at the given heap slots, and after the resolve transition them to the combined shader-read state for lighting.
    public void ColorsToUav(ID3D12GraphicsCommandList4 cl) {
        for (int i = 0; i < RtCount; i++) ColorTransition(cl, i, ResourceStates.UnorderedAccess);
    }
    public void CreateColorUav(int i, CpuDescriptorHandle dst) {
        dev.Device.CreateUnorderedAccessView(colors[i], null, new UnorderedAccessViewDescription {
            Format = UavFormatOf(ColorFormats[i]), ViewDimension = UnorderedAccessViewDimension.Texture2D,
        }, dst);
    }
    public void ColorsToShaderRead(ID3D12GraphicsCommandList4 cl) {
        for (int i = 0; i < RtCount; i++) ColorTransition(cl, i, ShaderRead);
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
