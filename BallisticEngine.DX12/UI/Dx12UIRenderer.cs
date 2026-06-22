using System.Runtime.InteropServices;
using BallisticEngine.UI;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.Dxc;
using Vortice.DXGI;

namespace BallisticEngine.DX12.UI;

public sealed class Dx12UIRenderer : IUIRenderer, IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    struct UIVertex
    {
        public Vector2 Pos;
        public Vector2 Uv;
        public Vector4 Color;
        public Vector4 Rect;
        public Vector4 Radius;
        public uint Mode;
        public Vector4 Border;
        public float BorderWidth;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct UIConstants
    {
        public Vector2 InvCanvas;
        public float SdfPx;
        public float Pad;
    }

    const int ModeSolid = 0;

    readonly Dx12Device dev;
    readonly ID3D12RootSignature rootSig;
    readonly ID3D12PipelineState pso;

    readonly List<UIVertex> verts = new(4096);
    readonly List<uint> indices = new(8192);

    struct ClipSegment { public int FirstIndex, IndexCount; public Rect Scissor; public bool Clipped; public int SrvSlot; }
    readonly List<ClipSegment> segments = new(32);
    readonly List<Rect> clipStack = new(16);
    Rect currentClip;
    bool currentClipped;
    int segStartIndex;
    int currentSrvSlot = -1;

    Vector2 canvasSize;
    float scale = 1f;

    ID3D12Resource vbUpload, ibUpload, cbUpload;
    int vbCapacityBytes, ibCapacityBytes;
    unsafe byte* cbMapped;

    const int SrvHeapCapacity = 1024;
    ID3D12DescriptorHeap srvHeap;
    uint srvIncrement;
    int persistentCount;
    int srvCursor;

    sealed class FontGpu
    {
        public ID3D12Resource Tex;
        public int Slot;
        public int W, H;
        public float SdfPadding;
        public int FontVersion;
    }
    readonly Dictionary<FontAtlas, FontGpu> fonts = new();
    FontGpu lastFontForCb;

    sealed class RampGpu { public ID3D12Resource Tex; public int Slot; }
    readonly Dictionary<Gradient, RampGpu> ramps = new();

    readonly List<ID3D12Resource> transientTextures = new(16);

    public Dx12UIRenderer(Dx12Device device)
    {
        dev = device;

        string hlsl = EmbeddedShaderSource.ReadHlsl("UI/UI.hlsl");
        byte[] vs = Dx12ShaderCompiler.Compile(DxcShaderStage.Vertex, hlsl, "VSMain", "UI/UI.hlsl");
        byte[] ps = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, hlsl, "PSMain", "UI/UI.hlsl");

        var srvRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 1, 0);
        var rootParams = new[]
        {
            new RootParameter1(RootParameterType.ConstantBufferView,
                new RootDescriptor1(0, 0), ShaderVisibility.All),
            new RootParameter1(new RootDescriptorTable1(srvRange), ShaderVisibility.Pixel),
        };
        var staticSampler = new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0)
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
        };
        var rsDesc = new RootSignatureDescription1(
            RootSignatureFlags.AllowInputAssemblerInputLayout, rootParams, new[] { staticSampler });
        rootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(rsDesc));

        var inputElements = new[]
        {
            new InputElementDescription("POSITION", 0, Format.R32G32_Float,       0,  0),
            new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float,       8,  0),
            new InputElementDescription("COLOR",    0, Format.R32G32B32A32_Float, 16, 0),
            new InputElementDescription("TEXCOORD", 1, Format.R32G32B32A32_Float, 32, 0),
            new InputElementDescription("TEXCOORD", 2, Format.R32G32B32A32_Float, 48, 0),
            new InputElementDescription("TEXCOORD", 3, Format.R32_UInt,           64, 0),
            new InputElementDescription("TEXCOORD", 4, Format.R32G32B32A32_Float, 68, 0),
            new InputElementDescription("TEXCOORD", 5, Format.R32_Float,          84, 0),
        };

        var psoDesc = new GraphicsPipelineStateDescription
        {
            RootSignature = rootSig,
            VertexShader = vs,
            PixelShader = ps,
            InputLayout = new InputLayoutDescription(inputElements),
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            SampleMask = uint.MaxValue,
            RasterizerState = RasterizerDescription.CullNone,
            BlendState = BlendDescription.AlphaBlend,
            DepthStencilState = DepthStencilDescription.None,
            RenderTargetFormats = new[] { Dx12OffscreenTarget.ColorFormat },
            DepthStencilFormat = Format.Unknown,
            SampleDescription = new SampleDescription(1, 0),
        };
        pso = dev.Device.CreateGraphicsPipelineState(psoDesc);

        srvHeap = dev.Device.CreateDescriptorHeap(new DescriptorHeapDescription(
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
            SrvHeapCapacity, DescriptorHeapFlags.ShaderVisible));
        srvIncrement = dev.Device.GetDescriptorHandleIncrementSize(
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);

        UploadRgba8TextureToSlot(new byte[] { 255, 255, 255, 255 }, 1, 1, "UISlot0White", 0);
        persistentCount = 1;
    }

    CpuDescriptorHandle SrvCpu(int slot) => new(srvHeap.GetCPUDescriptorHandleForHeapStart(), slot, srvIncrement);
    GpuDescriptorHandle SrvGpu(int slot) => new(srvHeap.GetGPUDescriptorHandleForHeapStart(), slot, srvIncrement);

    public void Begin(Vector2 canvas, float documentScale)
    {
        canvasSize = canvas;
        scale = documentScale;
        verts.Clear();
        indices.Clear();
        segments.Clear();
        clipStack.Clear();
        currentClipped = false;
        segStartIndex = 0;
        currentSrvSlot = -1;

        foreach (var t in transientTextures) t.Dispose();
        transientTextures.Clear();
        srvCursor = persistentCount;
    }

    void SetTextureSlot(int slot)
    {
        if (slot == currentSrvSlot) return;
        CloseSegment();
        currentSrvSlot = slot;
    }

    public void DrawRect(Rect rect, Color fill, Vector4 radius, float borderWidth, Color borderColor)
    {
        if (!DrawableRect(rect)) return;
        bool hasBorder = borderWidth > 0f && borderColor.A > 0f;
        if (fill.A <= 0f && !hasBorder) return;
        SetTextureSlot(-1);
        PushQuad(rect, fill.ToVector4(), radius, ModeSolid,
                 hasBorder ? borderColor.ToVector4() : Vector4.Zero,
                 hasBorder ? borderWidth : 0f);
    }

    public void DrawGradient(Rect rect, Gradient gradient, Vector4 radius, float opacity)
    {
        if (!DrawableRect(rect)) return;
        if (gradient == null || gradient.Stops.Count == 0 || opacity <= 0f) return;

        int slot = BakeGradientRamp(gradient);
        if (slot < 0) return;
        SetTextureSlot(slot);

        var col = new Vector4(0, 0, 0, opacity);
        PushGradientQuad(rect, gradient, radius, col);
    }

    const uint ModeShadow = 4;

    public void DrawShadow(Rect rect, Vector4 radius, float ox, float oy, float blur, float spread, Color color)
    {
        if (color.A <= 0f) return;
        float pad = MathF.Max(0f, spread) + MathF.Max(0f, blur);
        var r = new Rect(rect.X + ox - pad, rect.Y + oy - pad,
                         rect.Width + pad * 2f, rect.Height + pad * 2f);
        if (!DrawableRect(r)) return;
        SetTextureSlot(-1);
        var rectParams = new Vector4(r.X + r.Width * 0.5f, r.Y + r.Height * 0.5f,
                                     rect.Width * 0.5f + spread, rect.Height * 0.5f + spread);
        PushQuadRaw(r, color.ToVector4(), rectParams, radius, ModeShadow, Vector4.Zero, blur);
    }

    public void DrawBackdropBlur(Rect rect, Vector4 radius, float radiusPx)
    {
        if (_backdropSrvSlot >= 0)
        {
            SetTextureSlot(_backdropSrvSlot);
            PushQuadUV(rect, new Vector4(1, 1, 1, 1), 0, 0, 1, 1, ModeImage);
        }
        else
        {
            SetTextureSlot(-1);
            PushQuad(rect, new Vector4(1f, 1f, 1f, 0.06f), radius, ModeSolid, Vector4.Zero, 0f);
        }
    }

    int _backdropSrvSlot = -1;
    public void SetBackdropSource(ID3D12Resource copyOfFrame)
    {
        _backdropSrvSlot = copyOfFrame != null ? RegisterImage(copyOfFrame) : -1;
    }

    public void DrawText(Rect rect, string text, in TextStyle style)
    {
        if (string.IsNullOrEmpty(text)) return;
        var atlas = UIFonts.Resolve(style.FontFamily, style.Bold, style.Italic);
        if (atlas == null || atlas.Pixels == null) return;

        var font = EnsureFont(atlas);
        SetTextureSlot(font.Slot);
        lastFontForCb = font;

        float fontSize = style.FontSize > 0 ? style.FontSize : atlas.BakePixelHeight;
        float s = atlas.BakePixelHeight > 0 ? fontSize / atlas.BakePixelHeight : 1f;
        float letter = style.LetterSpacing;
        float lineH = atlas.LineHeight * s;
        float ascent = atlas.Ascent * s;
        Vector4 col = style.Color.ToVector4();
        float invAW = 1f / font.W, invAH = 1f / font.H;

        string[] lines = text.Split('\n');
        float blockH = lines.Length * lineH;

        float blockTop;
        switch (style.Align)
        {
            case TextAlign.MiddleLeft: case TextAlign.MiddleCenter: case TextAlign.MiddleRight:
                blockTop = rect.Y + (rect.Height - blockH) * 0.5f; break;
            case TextAlign.LowerLeft: case TextAlign.LowerCenter: case TextAlign.LowerRight:
                blockTop = rect.Y + (rect.Height - blockH); break;
            default:
                blockTop = rect.Y; break;
        }

        for (int li = 0; li < lines.Length; li++)
        {
            string line = lines[li];
            var (lineW, _) = atlas.Measure(line, fontSize, letter);

            float penX = rect.X;
            switch (style.Align)
            {
                case TextAlign.UpperCenter: case TextAlign.MiddleCenter: case TextAlign.LowerCenter:
                    penX = rect.X + (rect.Width - lineW) * 0.5f; break;
                case TextAlign.UpperRight: case TextAlign.MiddleRight: case TextAlign.LowerRight:
                    penX = rect.X + (rect.Width - lineW); break;
            }
            float baseline = blockTop + li * lineH + ascent;

            for (int ci = 0; ci < line.Length; ci++)
            {
                char ch = line[ci];
                var glyphAtlas = UIFonts.AtlasForGlyph(atlas, ch);
                if (!glyphAtlas.TryGetGlyph(ch, out var g))
                {
                    if (atlas.TryGetGlyph(' ', out var sp)) penX += sp.Advance * s;
                    continue;
                }
                var gfont = (glyphAtlas == atlas) ? font : EnsureFont(glyphAtlas);
                SetTextureSlot(gfont.Slot);
                float gInvAW = 1f / gfont.W, gInvAH = 1f / gfont.H;
                float gs = glyphAtlas.BakePixelHeight > 0 ? fontSize / glyphAtlas.BakePixelHeight : s;

                float gw = g.Width * gs, gh = g.Height * gs;
                float gx = penX + g.OffsetX * gs;
                float gy = baseline + g.OffsetY * gs;

                if (gw > 0 && gh > 0 && g.AtlasX1 > g.AtlasX0)
                {
                    float u0 = g.AtlasX0 * gInvAW, v0 = g.AtlasY0 * gInvAH;
                    float u1 = g.AtlasX1 * gInvAW, v1 = g.AtlasY1 * gInvAH;

                    if (style.HasShadow && style.ShadowColor.A > 0f)
                    {
                        Vector4 sc = style.ShadowColor.ToVector4();
                        PushGlyphQuad(gx + style.ShadowOffsetX, gy + style.ShadowOffsetY, gw, gh, u0, v0, u1, v1, sc);
                    }
                    PushGlyphQuad(gx, gy, gw, gh, u0, v0, u1, v1, col);
                }
                penX += g.Advance * gs + (ci < line.Length - 1 ? letter : 0f);
            }
        }
    }

    public void DrawImage(Rect rect, object texture, Color tint, ScaleMode scaleMode)
    {
        if (!DrawableRect(rect)) return;
        if (texture == null || tint.A <= 0f) return;

        ID3D12Resource res = texture switch
        {
            Dx12Texture2D t when t.HasSrv => t.Resource,
            ID3D12Resource r => r,
            _ => null,
        };
        if (res == null) return;

        int slot = RegisterImage(res);
        if (slot < 0) return;
        SetTextureSlot(slot);

        PushQuadUV(rect, tint.ToVector4(), 0f, 0f, 1f, 1f, ModeImage);
    }

    public void PushClip(Rect rect)
    {
        CloseSegment();
        clipStack.Add(rect);
        RecomputeClip();
    }

    public void PopClip()
    {
        if (clipStack.Count == 0) return;
        CloseSegment();
        clipStack.RemoveAt(clipStack.Count - 1);
        RecomputeClip();
    }

    void CloseSegment()
    {
        int count = indices.Count - segStartIndex;
        if (count > 0)
            segments.Add(new ClipSegment
            {
                FirstIndex = segStartIndex,
                IndexCount = count,
                Scissor = currentClip,
                Clipped = currentClipped,
                SrvSlot = currentSrvSlot,
            });
        segStartIndex = indices.Count;
    }

    void RecomputeClip()
    {
        if (clipStack.Count == 0) { currentClipped = false; return; }
        Rect r = clipStack[0];
        for (int i = 1; i < clipStack.Count; i++)
            r = Intersect(r, clipStack[i]);
        currentClip = r;
        currentClipped = true;
    }

    static Rect Intersect(Rect a, Rect b)
    {
        float l = MathF.Max(a.X, b.X);
        float t = MathF.Max(a.Y, b.Y);
        float rr = MathF.Min(a.X + a.Width, b.X + b.Width);
        float bb = MathF.Min(a.Y + a.Height, b.Y + b.Height);
        return new Rect(l, t, MathF.Max(0, rr - l), MathF.Max(0, bb - t));
    }

    void PushQuad(Rect r, Vector4 color, Vector4 radius, uint mode, Vector4 border, float borderWidth)
    {
        float l = r.X, t = r.Y, right = r.X + r.Width, b = r.Y + r.Height;
        var rectParams = new Vector4(r.X + r.Width * 0.5f, r.Y + r.Height * 0.5f,
                                     r.Width * 0.5f, r.Height * 0.5f);
        uint baseIdx = (uint)verts.Count;

        UIVertex V(float px, float py, float u, float v) => new()
        {
            Pos = new(px, py), Uv = new(u, v), Color = color, Rect = rectParams,
            Radius = radius, Mode = mode, Border = border, BorderWidth = borderWidth,
        };
        verts.Add(V(l, t, 0, 0));
        verts.Add(V(right, t, 1, 0));
        verts.Add(V(right, b, 1, 1));
        verts.Add(V(l, b, 0, 1));

        indices.Add(baseIdx + 0); indices.Add(baseIdx + 1); indices.Add(baseIdx + 2);
        indices.Add(baseIdx + 0); indices.Add(baseIdx + 2); indices.Add(baseIdx + 3);
    }

    const uint ModeGradient = 1;
    const uint ModeText = 2;
    const uint ModeImage = 3;

    void PushQuadRaw(Rect r, Vector4 color, Vector4 rectParams, Vector4 radius, uint mode, Vector4 border, float borderWidth)
    {
        float l = r.X, t = r.Y, right = r.X + r.Width, b = r.Y + r.Height;
        uint baseIdx = (uint)verts.Count;
        UIVertex V(float px, float py, float u, float v) => new()
        {
            Pos = new(px, py), Uv = new(u, v), Color = color, Rect = rectParams,
            Radius = radius, Mode = mode, Border = border, BorderWidth = borderWidth,
        };
        verts.Add(V(l, t, 0, 0));
        verts.Add(V(right, t, 1, 0));
        verts.Add(V(right, b, 1, 1));
        verts.Add(V(l, b, 0, 1));
        indices.Add(baseIdx + 0); indices.Add(baseIdx + 1); indices.Add(baseIdx + 2);
        indices.Add(baseIdx + 0); indices.Add(baseIdx + 2); indices.Add(baseIdx + 3);
    }

    void PushQuadUV(Rect r, Vector4 color, float u0, float v0, float u1, float v1, uint mode)
    {
        float l = r.X, t = r.Y, right = r.X + r.Width, b = r.Y + r.Height;
        var rectParams = new Vector4(r.X + r.Width * 0.5f, r.Y + r.Height * 0.5f,
                                     r.Width * 0.5f, r.Height * 0.5f);
        uint baseIdx = (uint)verts.Count;
        UIVertex V(float px, float py, float u, float v) => new()
        {
            Pos = new(px, py), Uv = new(u, v), Color = color, Rect = rectParams,
            Radius = Vector4.Zero, Mode = mode, Border = Vector4.Zero, BorderWidth = 0f,
        };
        verts.Add(V(l, t, u0, v0));
        verts.Add(V(right, t, u1, v0));
        verts.Add(V(right, b, u1, v1));
        verts.Add(V(l, b, u0, v1));
        indices.Add(baseIdx + 0); indices.Add(baseIdx + 1); indices.Add(baseIdx + 2);
        indices.Add(baseIdx + 0); indices.Add(baseIdx + 2); indices.Add(baseIdx + 3);
    }

    void PushGradientQuad(Rect r, Gradient g, Vector4 radius, Vector4 color)
    {
        float l = r.X, t = r.Y, right = r.X + r.Width, b = r.Y + r.Height;
        var rectParams = new Vector4(r.X + r.Width * 0.5f, r.Y + r.Height * 0.5f,
                                     r.Width * 0.5f, r.Height * 0.5f);
        uint baseIdx = (uint)verts.Count;

        Vector4 gp;
        float kind;
        if (g.Type == Gradient.Kind.Radial)
        {
            gp = new Vector4(g.CenterX, g.CenterY, g.RadiusX, g.RadiusY);
            kind = 1f;
        }
        else
        {
            float a = g.AngleDegrees * (MathF.PI / 180f);
            gp = new Vector4(MathF.Sin(a), -MathF.Cos(a), 0f, 0f);
            kind = 0f;
        }

        UIVertex V(float px, float py, float u, float v) => new()
        {
            Pos = new(px, py), Uv = new(u, v), Color = color, Rect = rectParams,
            Radius = radius, Mode = ModeGradient, Border = gp, BorderWidth = kind,
        };
        verts.Add(V(l, t, 0, 0));
        verts.Add(V(right, t, 1, 0));
        verts.Add(V(right, b, 1, 1));
        verts.Add(V(l, b, 0, 1));
        indices.Add(baseIdx + 0); indices.Add(baseIdx + 1); indices.Add(baseIdx + 2);
        indices.Add(baseIdx + 0); indices.Add(baseIdx + 2); indices.Add(baseIdx + 3);
    }

    void PushGlyphQuad(float x, float y, float w, float h, float u0, float v0, float u1, float v1, Vector4 color)
    {
        uint baseIdx = (uint)verts.Count;
        UIVertex V(float px, float py, float u, float v) => new()
        {
            Pos = new(px, py), Uv = new(u, v), Color = color, Rect = Vector4.Zero,
            Radius = Vector4.Zero, Mode = ModeText, Border = Vector4.Zero, BorderWidth = 0f,
        };
        verts.Add(V(x, y, u0, v0));
        verts.Add(V(x + w, y, u1, v0));
        verts.Add(V(x + w, y + h, u1, v1));
        verts.Add(V(x, y + h, u0, v1));
        indices.Add(baseIdx + 0); indices.Add(baseIdx + 1); indices.Add(baseIdx + 2);
        indices.Add(baseIdx + 0); indices.Add(baseIdx + 2); indices.Add(baseIdx + 3);
    }

    unsafe FontGpu EnsureFont(FontAtlas atlas)
    {
        if (fonts.TryGetValue(atlas, out var cached) && cached.FontVersion == UIFonts.Version && cached.Tex != null)
            return cached;

        var fg = cached ?? new FontGpu();
        if (cached == null)
        {
            if (persistentCount >= SrvHeapCapacity) return fonts.Count > 0 ? GetAnyFont() : null;
            fg.Slot = persistentCount++;
            fonts[atlas] = fg;
        }
        fg.Tex?.Dispose();

        fg.W = atlas.AtlasWidth;
        fg.H = atlas.AtlasHeight;
        fg.SdfPadding = atlas.SdfPadding;
        fg.FontVersion = UIFonts.Version;

        var desc = ResourceDescription.Texture2D(Format.R8_UNorm, (uint)fg.W, (uint)fg.H, arraySize: 1, mipLevels: 1);
        fg.Tex = dev.Device.CreateCommittedResource(
            HeapProperties.DefaultHeapProperties, HeapFlags.None, desc, ResourceStates.CopyDest);
        fg.Tex.Name = "UIFontAtlas";

        var footprints = new PlacedSubresourceFootPrint[1];
        var rowCounts = new uint[1]; var rowSizes = new ulong[1];
        dev.Device.GetCopyableFootprints(desc, 0, 1, 0, footprints, rowCounts, rowSizes, out ulong total);
        PlacedSubresourceFootPrint fp = footprints[0];
        int dstPitch = (int)fp.Footprint.RowPitch;

        using var upload = dev.Device.CreateCommittedResource(
            HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer(total), ResourceStates.GenericRead);
        byte* dst = MapUpload(upload);
        fixed (byte* src = atlas.Pixels)
            for (int row = 0; row < fg.H; row++)
                Buffer.MemoryCopy(src + (long)row * fg.W, dst + (long)fp.Offset + (long)row * dstPitch,
                    dstPitch, fg.W);
        upload.Unmap(0);

        dev.ExecuteUpload(cl =>
        {
            cl.CopyTextureRegion(new TextureCopyLocation(fg.Tex, 0), 0, 0, 0, new TextureCopyLocation(upload, fp), null);
            cl.ResourceBarrierTransition(fg.Tex, ResourceStates.CopyDest, ResourceStates.PixelShaderResource);
        });

        var srvDesc = new ShaderResourceViewDescription
        {
            Format = Format.R8_UNorm,
            ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Texture2D = new Texture2DShaderResourceView { MipLevels = 1, MostDetailedMip = 0 },
        };
        dev.Device.CreateShaderResourceView(fg.Tex, srvDesc, SrvCpu(fg.Slot));
        return fg;
    }

    FontGpu GetAnyFont() { foreach (var v in fonts.Values) return v; return null; }

    unsafe int BakeGradientRamp(Gradient g)
    {
        if (ramps.TryGetValue(g, out var cached)) return cached.Slot;
        if (persistentCount >= SrvHeapCapacity) return -1;

        const int N = 256;
        var ramp = new byte[N * 4];
        var stops = g.Stops;
        for (int x = 0; x < N; x++)
        {
            float t = x / (float)(N - 1);
            Color c = SampleStops(stops, t);
            ramp[x * 4 + 0] = (byte)Math.Clamp(c.R * 255f + 0.5f, 0, 255);
            ramp[x * 4 + 1] = (byte)Math.Clamp(c.G * 255f + 0.5f, 0, 255);
            ramp[x * 4 + 2] = (byte)Math.Clamp(c.B * 255f + 0.5f, 0, 255);
            ramp[x * 4 + 3] = (byte)Math.Clamp(c.A * 255f + 0.5f, 0, 255);
        }
        int slot = persistentCount++;
        var tex = UploadRgba8ResourceToSlot(ramp, N, 1, "UIGradientRamp", slot);
        ramps[g] = new RampGpu { Tex = tex, Slot = slot };
        return slot;
    }

    static Color SampleStops(System.Collections.Generic.List<Gradient.Stop> stops, float t)
    {
        if (stops.Count == 1) return stops[0].Color;
        if (t <= stops[0].Position) return stops[0].Color;
        if (t >= stops[^1].Position) return stops[^1].Color;
        for (int i = 0; i < stops.Count - 1; i++)
        {
            var a = stops[i]; var b = stops[i + 1];
            if (t >= a.Position && t <= b.Position)
            {
                float span = MathF.Max(1e-5f, b.Position - a.Position);
                float k = (t - a.Position) / span;
                return new Color(
                    a.Color.R + (b.Color.R - a.Color.R) * k,
                    a.Color.G + (b.Color.G - a.Color.G) * k,
                    a.Color.B + (b.Color.B - a.Color.B) * k,
                    a.Color.A + (b.Color.A - a.Color.A) * k);
            }
        }
        return stops[^1].Color;
    }

    int RegisterImage(ID3D12Resource res)
    {
        if (srvCursor >= SrvHeapCapacity) return -1;
        int slot = srvCursor++;
        var srvDesc = new ShaderResourceViewDescription
        {
            Format = res.Description.Format,
            ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Texture2D = new Texture2DShaderResourceView { MipLevels = res.Description.MipLevels, MostDetailedMip = 0 },
        };
        dev.Device.CreateShaderResourceView(res, srvDesc, SrvCpu(slot));
        return slot;
    }

    unsafe int UploadRgba8Texture(byte[] rgba, int w, int h, string name)
    {
        if (srvCursor >= SrvHeapCapacity) return -1;
        int slot = srvCursor++;
        var tex = UploadRgba8ResourceToSlot(rgba, w, h, name, slot);
        transientTextures.Add(tex);
        return slot;
    }

    unsafe void UploadRgba8TextureToSlot(byte[] rgba, int w, int h, string name, int slot)
    {
        slot0Seed = UploadRgba8ResourceToSlot(rgba, w, h, name, slot);
    }

    unsafe ID3D12Resource UploadRgba8ResourceToSlot(byte[] rgba, int w, int h, string name, int slot)
    {
        var desc = ResourceDescription.Texture2D(Format.R8G8B8A8_UNorm, (uint)w, (uint)h, arraySize: 1, mipLevels: 1);
        var tex = dev.Device.CreateCommittedResource(
            HeapProperties.DefaultHeapProperties, HeapFlags.None, desc, ResourceStates.CopyDest);
        tex.Name = name;

        var footprints = new PlacedSubresourceFootPrint[1];
        var rowCounts = new uint[1]; var rowSizes = new ulong[1];
        dev.Device.GetCopyableFootprints(desc, 0, 1, 0, footprints, rowCounts, rowSizes, out ulong total);
        PlacedSubresourceFootPrint fp = footprints[0];
        int dstPitch = (int)fp.Footprint.RowPitch;
        int srcPitch = w * 4;

        using var upload = dev.Device.CreateCommittedResource(
            HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer(total), ResourceStates.GenericRead);
        byte* dst = MapUpload(upload);
        fixed (byte* src = rgba)
            for (int row = 0; row < h; row++)
                Buffer.MemoryCopy(src + (long)row * srcPitch, dst + (long)fp.Offset + (long)row * dstPitch,
                    srcPitch, srcPitch);
        upload.Unmap(0);

        dev.ExecuteUpload(cl =>
        {
            cl.CopyTextureRegion(new TextureCopyLocation(tex, 0), 0, 0, 0, new TextureCopyLocation(upload, fp), null);
            cl.ResourceBarrierTransition(tex, ResourceStates.CopyDest, ResourceStates.PixelShaderResource);
        });

        var srvDesc = new ShaderResourceViewDescription
        {
            Format = Format.R8G8B8A8_UNorm,
            ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Texture2D = new Texture2DShaderResourceView { MipLevels = 1, MostDetailedMip = 0 },
        };
        dev.Device.CreateShaderResourceView(tex, srvDesc, SrvCpu(slot));
        return tex;
    }
    ID3D12Resource slot0Seed;

    public void End()
    {
        CloseSegment();
    }

    public unsafe void Flush(Dx12OffscreenTarget target)
    {
        if (segments.Count == 0) return;

        EnsureBuffers();

        fixed (UIVertex* vp = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(verts))
        {
            byte* dst = MapUpload(vbUpload);
            Buffer.MemoryCopy(vp, dst, vbCapacityBytes, verts.Count * sizeof(UIVertex));
            vbUpload.Unmap(0);
        }
        fixed (uint* ip = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(indices))
        {
            byte* dst = MapUpload(ibUpload);
            Buffer.MemoryCopy(ip, dst, ibCapacityBytes, indices.Count * sizeof(uint));
            ibUpload.Unmap(0);
        }

        var consts = new UIConstants
        {
            InvCanvas = new Vector2(2f / MathF.Max(1f, canvasSize.X), 2f / MathF.Max(1f, canvasSize.Y)),
            SdfPx = lastFontForCb?.SdfPadding ?? 4f,
        };
        *(UIConstants*)cbMapped = consts;

        var vbv = new VertexBufferView(vbUpload.GPUVirtualAddress, (uint)(verts.Count * sizeof(UIVertex)), (uint)sizeof(UIVertex));
        var ibv = new IndexBufferView(ibUpload.GPUVirtualAddress, (uint)(indices.Count * sizeof(uint)), Format.R32_UInt);
        int tw = target.Width, th = target.Height;

        float sx = tw / MathF.Max(1f, canvasSize.X);
        float sy = th / MathF.Max(1f, canvasSize.Y);

        target.RenderColorOnly(cl =>
        {
            cl.SetDescriptorHeaps(srvHeap);
            cl.SetPipelineState(pso);
            cl.SetGraphicsRootSignature(rootSig);
            cl.SetGraphicsRootConstantBufferView(0, cbUpload.GPUVirtualAddress);
            cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            cl.IASetVertexBuffers(0, vbv);
            cl.IASetIndexBuffer(ibv);

            int boundSlot = -2;
            foreach (var seg in segments)
            {
                int wantSlot = seg.SrvSlot < 0 ? 0 : seg.SrvSlot;
                if (wantSlot != boundSlot)
                {
                    cl.SetGraphicsRootDescriptorTable(1, SrvGpu(wantSlot));
                    boundSlot = wantSlot;
                }

                if (seg.Clipped)
                {
                    var cr = seg.Scissor;
                    if (!IsFinite(cr.X) || !IsFinite(cr.Y) || !IsFinite(cr.Width) || !IsFinite(cr.Height))
                        continue;
                    int sl = (int)MathF.Floor(cr.X * sx);
                    int st = (int)MathF.Floor(cr.Y * sy);
                    int sr = (int)MathF.Ceiling((cr.X + cr.Width) * sx);
                    int sb = (int)MathF.Ceiling((cr.Y + cr.Height) * sy);
                    sl = Math.Clamp(sl, 0, tw); st = Math.Clamp(st, 0, th);
                    sr = Math.Clamp(sr, 0, tw); sb = Math.Clamp(sb, 0, th);
                    if (sr <= sl || sb <= st) continue;
                    cl.RSSetScissorRect(new Vortice.RawRect(sl, st, sr, sb));
                }
                else
                {
                    cl.RSSetScissorRect(tw, th);
                }
                cl.DrawIndexedInstanced((uint)seg.IndexCount, 1, (uint)seg.FirstIndex, 0, 0);
            }
        });
    }

    unsafe void EnsureBuffers()
    {
        int needVb = Math.Max(1, verts.Count) * sizeof(UIVertex);
        int needIb = Math.Max(1, indices.Count) * sizeof(uint);

        if (vbUpload == null || needVb > vbCapacityBytes)
        {
            vbUpload?.Dispose();
            vbCapacityBytes = NextPow2(needVb);
            vbUpload = CreateUploadBuffer(vbCapacityBytes);
        }
        if (ibUpload == null || needIb > ibCapacityBytes)
        {
            ibUpload?.Dispose();
            ibCapacityBytes = NextPow2(needIb);
            ibUpload = CreateUploadBuffer(ibCapacityBytes);
        }
        if (cbUpload == null)
        {
            cbUpload = CreateUploadBuffer((sizeof(UIConstants) + 255) & ~255);
            cbMapped = MapUpload(cbUpload);
        }
    }

    ID3D12Resource CreateUploadBuffer(int bytes)
    {
        var res = dev.Device.CreateCommittedResource(
            HeapProperties.UploadHeapProperties,
            HeapFlags.None,
            ResourceDescription.Buffer((ulong)bytes),
            ResourceStates.GenericRead);
        res.Name = "UIRendererUpload";
        return res;
    }

    static unsafe byte* MapUpload(ID3D12Resource res)
    {
        void* p;
        res.Map(0, &p);
        return (byte*)p;
    }

    static int NextPow2(int v)
    {
        int p = 1;
        while (p < v) p <<= 1;
        return p;
    }

    static bool IsFinite(float f) => !float.IsNaN(f) && !float.IsInfinity(f);

    static bool DrawableRect(Rect r) =>
        IsFinite(r.X) && IsFinite(r.Y) && IsFinite(r.Width) && IsFinite(r.Height) &&
        r.Width > 0f && r.Height > 0f;

    public unsafe void Dispose()
    {
        if (cbUpload != null && cbMapped != null) cbUpload.Unmap(0);
        cbUpload?.Dispose();
        vbUpload?.Dispose();
        ibUpload?.Dispose();
        slot0Seed?.Dispose();
        foreach (var f in fonts.Values) f.Tex?.Dispose();
        fonts.Clear();
        foreach (var r in ramps.Values) r.Tex?.Dispose();
        ramps.Clear();
        foreach (var t in transientTextures) t.Dispose();
        transientTextures.Clear();
        srvHeap?.Dispose();
        pso.Dispose();
        rootSig.Dispose();
    }
}
