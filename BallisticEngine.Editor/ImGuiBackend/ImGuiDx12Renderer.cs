using System.Runtime.InteropServices;
using BallisticEngine.DX12;
using Hexa.NET.ImGui;
using Vortice.Direct3D12;
using Vortice.Dxc;
using Vortice.DXGI;

namespace BallisticEngine.Editor;

// Dear ImGui DX12 backend — the device-side twin of ImGuiGLRenderer. Records the ImGui draw data into the
// editor swapchain's OPEN UI command list (resolved via `currentList` at render time, since the swapchain
// is created after this renderer). One merged upload-heap vertex+index buffer (grown as needed) honoring
// ImDrawCmd.VtxOffset/IdxOffset (RendererHasVtxOffset is set); an ortho CBV (b0); a single SRV table (t0)
// + static linear-clamp sampler. The font atlas and every sampled texture live in the shared shader-visible
// Dx12Backend.UiHeap, so ImTextureID == a GPU descriptor ptr into that heap and one SetDescriptorHeaps
// covers all draws (the standard imgui_impl_dx12 model). The backbuffer RTV is already bound + cleared by
// Dx12SwapChain.BeginFrame, so this only records pipeline state + draws.
internal sealed unsafe class ImGuiDx12Renderer : IImGuiRenderer {
    readonly Func<ID3D12GraphicsCommandList4> currentList;
    Dx12Device Dev => Dx12Backend.Device;

    ID3D12RootSignature rootSig;
    ID3D12PipelineState pso;

    // One merged upload-heap vertex+index buffer (mapped, persistent), grown to fit the frame's draw data.
    ID3D12Resource vtxBuffer, idxBuffer;
    int vtxCapacity, idxCapacity;   // in elements
    byte* vtxMapped, idxMapped;

    ID3D12Resource orthoCb;         // b0: the ortho projection matrix (transposed on upload, codebase convention)
    byte* orthoMapped;

    ID3D12Resource fontTexture;
    int fontUiSlot = -1;            // stable UiHeap slot for the font atlas SRV (re-pointed on DPI rebuild)
    nint fontHandle;                // ImTextureID for the font atlas (UiHeap GPU ptr)

    static readonly int VtxStride = Marshal.SizeOf<ImDrawVert>();   // 20 bytes (pos2 + uv2 + col4)

    public ImGuiDx12Renderer(Func<ID3D12GraphicsCommandList4> currentList) {
        this.currentList = currentList;
    }

    public void CreateDeviceResources() {
        // Root signature: b0 ortho CBV (vertex), t0 SRV table (pixel) + static linear-clamp sampler s0.
        var cbv = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.Vertex);
        var srvRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 1, baseShaderRegister: 0);
        var srvTable = new RootParameter1(new RootDescriptorTable1(srvRange), ShaderVisibility.Pixel);
        var samp = new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0) {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp, MaxAnisotropy = 1,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        rootSig = Dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.AllowInputAssemblerInputLayout,
                new[] { cbv, srvTable }, new[] { samp })));

        string hlsl = BallisticEngine.DX12.EmbeddedShaderSource.ReadHlsl("EditorImGui.hlsl");
        byte[] vs = Dx12ShaderCompiler.Compile(DxcShaderStage.Vertex, hlsl, "VSMain", "EditorImGui.hlsl");
        byte[] ps = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, hlsl, "PSMain", "EditorImGui.hlsl");
        pso = Dev.Device.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription {
            RootSignature = rootSig, VertexShader = vs, PixelShader = ps,
            InputLayout = new InputLayoutDescription(
                new InputElementDescription("POSITION", 0, Format.R32G32_Float, 0, 0),
                new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 8, 0),
                new InputElementDescription("COLOR", 0, Format.R8G8B8A8_UNorm, 16, 0)),
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle, SampleMask = uint.MaxValue,
            RasterizerState = RasterizerDescription.CullNone,
            // Straight-alpha blend (SrcAlpha / 1-SrcAlpha) on both color and alpha — matches the GL backend's
            // BlendFunc(SrcAlpha, OneMinusSrcAlpha). The backbuffer alpha is irrelevant (opaque window).
            BlendState = new BlendDescription(Blend.SourceAlpha, Blend.InverseSourceAlpha),
            DepthStencilState = DepthStencilDescription.None,
            RenderTargetFormats = new[] { Dx12SwapChain.BackbufferFormat }, DepthStencilFormat = Format.Unknown,
            SampleDescription = new SampleDescription(1, 0),
        });

        int cbSize = (64 + 255) & ~255;   // float4x4 ortho, 256-aligned CBV
        orthoCb = Dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)cbSize), ResourceStates.GenericRead);
        orthoMapped = orthoCb.Map<byte>(0);

        RecreateFontTexture();
    }

    public void RecreateFontTexture() {
        ImGuiIOPtr io = ImGui.GetIO();
        byte* pixels; int w, h;
        io.Fonts.GetTexDataAsRGBA32(&pixels, &w, &h);

        fontTexture?.Dispose();
        var desc = ResourceDescription.Texture2D(Format.R8G8B8A8_UNorm, (uint)w, (uint)h, arraySize: 1, mipLevels: 1);
        fontTexture = Dev.Device.CreateCommittedResource(HeapProperties.DefaultHeapProperties, HeapFlags.None,
            desc, ResourceStates.CopyDest);
        fontTexture.Name = "ImGui Font Atlas";

        var footprints = new PlacedSubresourceFootPrint[1];
        var rowCounts = new uint[1]; var rowSizes = new ulong[1];
        Dev.Device.GetCopyableFootprints(desc, 0, 1, 0, footprints, rowCounts, rowSizes, out ulong total);
        PlacedSubresourceFootPrint fp = footprints[0];
        long rowPitch = fp.Footprint.RowPitch;

        using ID3D12Resource upload = Dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties,
            HeapFlags.None, ResourceDescription.Buffer(total), ResourceStates.GenericRead);
        byte* dst = upload.Map<byte>(0);
        for (int y = 0; y < h; y++)
            System.Buffer.MemoryCopy(pixels + (long)y * w * 4, dst + (long)y * rowPitch, w * 4, w * 4);
        upload.Unmap(0);

        Dev.ExecuteUpload(cl => {
            cl.CopyTextureRegion(new TextureCopyLocation(fontTexture, 0), 0, 0, 0,
                new TextureCopyLocation(upload, fp), null);
            cl.ResourceBarrierTransition(fontTexture, ResourceStates.CopyDest, ResourceStates.PixelShaderResource);
        });

        // SRV in the shared UI heap (reuse the same slot across DPI rebuilds so the handle stays stable).
        if (fontUiSlot < 0) fontUiSlot = Dx12Backend.UiHeap.Allocate();
        Dev.Device.CreateShaderResourceView(fontTexture, new ShaderResourceViewDescription {
            Format = Format.R8G8B8A8_UNorm,
            ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Texture2D = new Texture2DShaderResourceView { MipLevels = 1, MostDetailedMip = 0 },
        }, Dx12Backend.UiHeap.Cpu(fontUiSlot));
        fontHandle = (nint)Dx12Backend.UiHeap.Gpu(fontUiSlot).Ptr;

        io.Fonts.SetTexID(new ImTextureID((ulong)fontHandle));
        io.Fonts.ClearTexData();
    }

    public void Render(ImDrawDataPtr drawData) {
        if (drawData.CmdListsCount == 0)
            return;
        ID3D12GraphicsCommandList4 cl = currentList();
        if (cl == null)
            return;

        ImGuiIOPtr io = ImGui.GetIO();
        int fbW = (int)(io.DisplaySize.X * io.DisplayFramebufferScale.X);
        int fbH = (int)(io.DisplaySize.Y * io.DisplayFramebufferScale.Y);
        if (fbW <= 0 || fbH <= 0)
            return;

        int totalVtx = drawData.TotalVtxCount;
        int totalIdx = drawData.TotalIdxCount;
        if (totalVtx == 0 || totalIdx == 0)
            return;
        EnsureBuffers(totalVtx, totalIdx);

        // Ortho projection: ImGui screen space (top-left origin) -> DX12 NDC. Transposed on upload so the
        // shader's mul(M, v) is correct (codebase convention).
        var ortho = System.Numerics.Matrix4x4.CreateOrthographicOffCenter(
            0f, io.DisplaySize.X, io.DisplaySize.Y, 0f, 0f, 1f);
        ortho = System.Numerics.Matrix4x4.Transpose(ortho);
        *(System.Numerics.Matrix4x4*)orthoMapped = ortho;

        drawData.ScaleClipRects(io.DisplayFramebufferScale);

        // Render state. The backbuffer RTV is already bound + cleared by Dx12SwapChain.BeginFrame.
        cl.SetGraphicsRootSignature(rootSig);
        cl.SetPipelineState(pso);
        cl.SetDescriptorHeaps(Dx12Backend.UiHeap.Heap);
        cl.SetGraphicsRootConstantBufferView(0, orthoCb.GPUVirtualAddress);
        cl.IASetVertexBuffers(0, new VertexBufferView(vtxBuffer.GPUVirtualAddress, (uint)(totalVtx * VtxStride), (uint)VtxStride));
        cl.IASetIndexBuffer(new IndexBufferView(idxBuffer.GPUVirtualAddress, (uint)(totalIdx * sizeof(ushort)), Format.R16_UInt));
        cl.IASetPrimitiveTopology(Vortice.Direct3D.PrimitiveTopology.TriangleList);
        cl.RSSetViewport(0, 0, fbW, fbH);

        int vtxGlobal = 0, idxGlobal = 0;   // running base offsets (in elements) across cmd lists
        for (int n = 0; n < drawData.CmdListsCount; n++) {
            ImDrawListPtr list = drawData.CmdLists[n];
            int vCount = list.VtxBuffer.Size, iCount = list.IdxBuffer.Size;

            System.Buffer.MemoryCopy(list.VtxBuffer.Data, vtxMapped + (long)vtxGlobal * VtxStride,
                (long)(vtxCapacity - vtxGlobal) * VtxStride, (long)vCount * VtxStride);
            System.Buffer.MemoryCopy(list.IdxBuffer.Data, idxMapped + (long)idxGlobal * sizeof(ushort),
                (long)(idxCapacity - idxGlobal) * sizeof(ushort), (long)iCount * sizeof(ushort));

            for (int c = 0; c < list.CmdBuffer.Size; c++) {
                ImDrawCmd cmd = list.CmdBuffer[c];
                System.Numerics.Vector4 clip = cmd.ClipRect;
                int left = Math.Max(0, (int)clip.X), top = Math.Max(0, (int)clip.Y);
                int right = Math.Min(fbW, (int)clip.Z), bottom = Math.Min(fbH, (int)clip.W);
                if (right <= left || bottom <= top)
                    continue;
                cl.RSSetScissorRect(new Vortice.RawRect(left, top, right, bottom));
                cl.SetGraphicsRootDescriptorTable(1, new GpuDescriptorHandle { Ptr = (ulong)cmd.TextureId.Handle });
                cl.DrawIndexedInstanced((uint)cmd.ElemCount, 1u,
                    (uint)(idxGlobal + (int)cmd.IdxOffset), vtxGlobal + (int)cmd.VtxOffset, 0u);
            }
            vtxGlobal += vCount;
            idxGlobal += iCount;
        }
    }

    void EnsureBuffers(int vtxCount, int idxCount) {
        // GROW HAZARD (EF3): these are single upload buffers the GPU reads while a prior frame is still in
        // flight. A window resize/maximize can sharply increase the ImGui vertex count (more/larger panels) and
        // trip a grow MID-STREAM — disposing the old buffer while the previous frame's draw still references it
        // is a use-after-free → DXGI_ERROR_DEVICE_HUNG. Drain the GPU BEFORE disposing so no in-flight frame
        // holds the buffer being freed. The grow is rare (only on a capacity increase), so the Flush is cheap;
        // the steady-state (no grow) path is untouched. The default editor frame is already synchronous
        // (FramesInFlight==1, EndFrame waits), but the drain makes correctness independent of that.
        bool grow = (vtxBuffer == null || vtxCount > vtxCapacity) || (idxBuffer == null || idxCount > idxCapacity);
        if (grow && (vtxBuffer != null || idxBuffer != null)) Dev.Flush();
        if (vtxBuffer == null || vtxCount > vtxCapacity) {
            if (vtxBuffer != null) { vtxBuffer.Unmap(0); vtxBuffer.Dispose(); }
            vtxCapacity = Math.Max(vtxCount + 5000, vtxCapacity * 2);
            vtxBuffer = Dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
                ResourceDescription.Buffer((ulong)((long)vtxCapacity * VtxStride)), ResourceStates.GenericRead);
            vtxBuffer.Name = "ImGui VtxBuffer";
            vtxMapped = vtxBuffer.Map<byte>(0);
        }
        if (idxBuffer == null || idxCount > idxCapacity) {
            if (idxBuffer != null) { idxBuffer.Unmap(0); idxBuffer.Dispose(); }
            idxCapacity = Math.Max(idxCount + 10000, idxCapacity * 2);
            idxBuffer = Dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
                ResourceDescription.Buffer((ulong)((long)idxCapacity * sizeof(ushort))), ResourceStates.GenericRead);
            idxBuffer.Name = "ImGui IdxBuffer";
            idxMapped = idxBuffer.Map<byte>(0);
        }
    }

    public void Dispose() {
        if (vtxBuffer != null) { vtxBuffer.Unmap(0); vtxBuffer.Dispose(); }
        if (idxBuffer != null) { idxBuffer.Unmap(0); idxBuffer.Dispose(); }
        if (orthoCb != null) { orthoCb.Unmap(0); orthoCb.Dispose(); }
        fontTexture?.Dispose();
        pso?.Dispose();
        rootSig?.Dispose();
    }
}
