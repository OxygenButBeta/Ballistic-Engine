using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using BallisticEngine.UI;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.Dxc;
using Vortice.DXGI;

namespace BallisticEngine.DX12.UI;

// DX12 backend for the engine's UI Toolkit (implements UI/Rendering/IUIRenderer.cs). A single batched
// 2D overlay pass: the render-walk issues Draw* calls, this collects them into one vertex/index buffer
// in LOGICAL pixels, then one ortho-projected draw flushes the whole UI document onto a render target.
//
// ----- PORTABILITY (this lives in a worktree split off a branch with in-flight renderer changes) -----
// Everything UI-render-specific is in THIS file. It leans only on stable DX12 helpers (Dx12Device,
// Dx12ShaderCompiler, EmbeddedShaderSource) and never touches the renderer's frame command list — it
// records its own ExecuteSync onto an arbitrary target RTV. When the renderer rework merges, the only
// integration point is one call after composite (see RenderInto/the player hook). Plan + port checklist:
// Docs/Plans/ui-toolkit-dx12-renderer-portable.md.
//
// SLICE STATUS: R1 = solid rects only (no radius/border/text/gradient/image yet). The vertex format
// and shader already carry the fields for those so later slices add code, not format churn.
public sealed class Dx12UIRenderer : IUIRenderer, IDisposable
{
    // Vertex mirrors VSIn in Shaders/UI/UI.hlsl. Blittable (System.Numerics + uint), uploaded as-is.
    [StructLayout(LayoutKind.Sequential)]
    struct UIVertex
    {
        public Vector2 Pos;     // logical pixels (top-left origin)        offset 0
        public Vector2 Uv;      //                                        offset 8
        public Vector4 Color;   // fill RGBA, opacity premultiplied        offset 16
        public Vector4 Rect;    // (centerX, centerY, halfW, halfH)        offset 32
        public Vector4 Radius;  // per-corner TL,TR,BR,BL (pixels)         offset 48
        public uint Mode;       // 0 solid, 1 gradient, 2 text, 3 image    offset 64
        public Vector4 Border;  // border RGBA (premultiplied opacity)     offset 68
        public float BorderWidth; // border width in pixels; 0 = none      offset 84
    }

    [StructLayout(LayoutKind.Sequential)]
    struct UIConstants
    {
        public Vector2 InvCanvas;  // 2/W, 2/H
        public float SdfPx;        // FontAtlas.SdfPadding (AA hint)
        public float Pad;
    }

    const int ModeSolid = 0;

    readonly Dx12Device dev;
    readonly ID3D12RootSignature rootSig;
    readonly ID3D12PipelineState pso;

    // Per-frame CPU geometry, flushed on End().
    readonly List<UIVertex> verts = new(4096);
    readonly List<uint> indices = new(8192);

    // Clip handling. Rounded clip needs more than a scissor rect, so R2 does rectangular scissor only
    // (overflow:hidden's common case). The geometry is split into segments whenever the active clip
    // changes; each segment is one DrawIndexedInstanced with its own RSSetScissorRect. PushClip stores
    // the INTERSECTION of the stack so nested clips compose.
    struct ClipSegment { public int FirstIndex, IndexCount; public Rect Scissor; public bool Clipped; public int SrvSlot; }
    readonly List<ClipSegment> segments = new(32);
    readonly List<Rect> clipStack = new(16);
    Rect currentClip;          // intersection of the whole stack
    bool currentClipped;       // false = no clip (full canvas)
    int segStartIndex;         // first index of the in-progress segment
    int currentSrvSlot = -1;   // texture slot bound for the in-progress segment (-1 = none / solid)

    Vector2 canvasSize;
    float scale = 1f;

    // GPU upload buffers, recreated when the geometry outgrows them (R1 keeps it simple — a default
    // buffer per flush would be the naive path; we reuse an upload-heap buffer and grow it instead so
    // there's no per-frame allocation in steady state).
    ID3D12Resource vbUpload, ibUpload, cbUpload;
    int vbCapacityBytes, ibCapacityBytes;
    unsafe byte* cbMapped;

    // One shader-visible SRV heap holding every texture a frame binds: the glyph atlas (slot 0, persistent),
    // plus per-frame gradient ramps and images. Each draw segment points the root table at its slot. Classic
    // descriptor-table model (NOT SM6.6 bindless) so the heap-order hang gotcha doesn't strictly apply, but
    // SetDescriptorHeaps is still issued before the root signature out of habit/safety.
    // The heap is split into a PERSISTENT region (slot 0 = white seed; slots 1.. = one per font atlas,
    // uploaded lazily once per UIFonts.Version) and a TRANSIENT region (gradient ramps + images, refilled
    // every frame). currentSrvSlot/segments carry which slot a draw samples, so multiple fonts in one frame
    // each bind their OWN atlas — no more "everything is slot 0". Classic descriptor-table model (NOT SM6.6
    // bindless); SetDescriptorHeaps is still issued before the root signature out of habit/safety.
    const int SrvHeapCapacity = 1024;
    ID3D12DescriptorHeap srvHeap;        // shader-visible
    uint srvIncrement;
    int persistentCount;                 // slots [0..persistentCount) are persistent; transient starts here
    int srvCursor;                       // next free TRANSIENT slot this frame; reset to persistentCount in Begin

    // One GPU resource per FontAtlas (R8 SDF). Keyed by the atlas instance; rebuilt when UIFonts.Version
    // bumps. Each holds a persistent heap slot.
    sealed class FontGpu
    {
        public ID3D12Resource Tex;
        public int Slot;
        public int W, H;
        public float SdfPadding;
        public int FontVersion;
    }
    readonly Dictionary<FontAtlas, FontGpu> fonts = new();
    FontGpu lastFontForCb;               // the atlas whose SdfPadding seeds the CB (per-flush, best-effort)

    // Gradient ramp cache (P1.3): a 256x1 ramp per Gradient instance, kept PERSISTENT so a static UI does
    // zero per-frame texture allocations/uploads. Keyed by the Gradient object (Style holds it stably).
    sealed class RampGpu { public ID3D12Resource Tex; public int Slot; }
    readonly Dictionary<Gradient, RampGpu> ramps = new();

    // Per-frame transient textures (gradient ramps + images) — disposed at the start of the next frame.
    readonly List<ID3D12Resource> transientTextures = new(16);

    public Dx12UIRenderer(Dx12Device device)
    {
        dev = device;

        string hlsl = EmbeddedShaderSource.ReadHlsl("UI/UI.hlsl");
        byte[] vs = Dx12ShaderCompiler.Compile(DxcShaderStage.Vertex, hlsl, "VSMain", "UI/UI.hlsl");
        byte[] ps = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, hlsl, "PSMain", "UI/UI.hlsl");

        // Root signature: b0 = ortho/SDF constants (All — VS reads InvCanvas, PS reads SdfPx); a single
        // SRV descriptor table (t0) for the glyph atlas, sampled in the pixel shader; one static linear-
        // clamp sampler (s0). Non-text draws leave the table pointed at the atlas but never sample it.
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

        // Input layout matches UIVertex / VSIn.
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
            RasterizerState = RasterizerDescription.CullNone,        // 2D, no culling
            BlendState = BlendDescription.AlphaBlend,                // UI is alpha-composited over the scene
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

        // Slot 0 must ALWAYS hold a valid descriptor — solid segments point the table there as a no-sample
        // placeholder, and an empty slot 0 is a GPU device-removal. Seed it with a 1×1 white texture.
        // Persistent region grows as fonts are uploaded (slot 1..); transient region starts after it.
        UploadRgba8TextureToSlot(new byte[] { 255, 255, 255, 255 }, 1, 1, "UISlot0White", 0);
        persistentCount = 1;
    }

    CpuDescriptorHandle SrvCpu(int slot) => new(srvHeap.GetCPUDescriptorHandleForHeapStart(), slot, srvIncrement);
    GpuDescriptorHandle SrvGpu(int slot) => new(srvHeap.GetGPUDescriptorHandleForHeapStart(), slot, srvIncrement);

    // ---------------- IUIRenderer: geometry collection ----------------

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

        // Release last frame's transient textures (ramps/images) and reclaim their heap slots. The
        // persistent region (slot 0 seed + font atlases) is kept; transient textures start after it.
        foreach (var t in transientTextures) t.Dispose();
        transientTextures.Clear();
        srvCursor = persistentCount;
    }

    // Switch the texture bound for subsequent quads; closes the current segment so the new texture takes
    // effect on a fresh draw. slot -1 = solid (no sampling).
    void SetTextureSlot(int slot)
    {
        if (slot == currentSrvSlot) return;
        CloseSegment();
        currentSrvSlot = slot;
    }

    public void DrawRect(Rect rect, Color fill, Vector4 radius, float borderWidth, Color borderColor)
    {
        if (!DrawableRect(rect)) return;
        // Nothing to draw if both fill and border are invisible.
        bool hasBorder = borderWidth > 0f && borderColor.A > 0f;
        if (fill.A <= 0f && !hasBorder) return;
        SetTextureSlot(-1);   // solid path samples no texture
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

        // Per-corner gradient parameter t goes into each vertex's uv.x; the shader samples the ramp at t.
        // Linear: project the corner onto the CSS direction. Radial: distance from center over the radii.
        var col = new Vector4(0, 0, 0, opacity);   // only .a is used by the shader (ramp supplies rgb)
        PushGradientQuad(rect, gradient, radius, col);
    }

    const uint ModeShadow = 4;

    public void DrawShadow(Rect rect, Vector4 radius, float ox, float oy, float blur, float spread, Color color)
    {
        if (color.A <= 0f) return;
        // Expand by spread + blur so the soft edge has room; offset by (ox,oy). The shader fades over the
        // outer `blur` px of the SDF, which sits in the padded quad.
        float pad = MathF.Max(0f, spread) + MathF.Max(0f, blur);
        var r = new Rect(rect.X + ox - pad, rect.Y + oy - pad,
                         rect.Width + pad * 2f, rect.Height + pad * 2f);
        if (!DrawableRect(r)) return;
        SetTextureSlot(-1);
        // The SDF box inside the quad must be the spread-expanded element box; encode its half-size via
        // Rect (center,half) and the blur via BorderWidth. radius mirrors the element corners (+spread).
        var rectParams = new Vector4(r.X + r.Width * 0.5f, r.Y + r.Height * 0.5f,
                                     rect.Width * 0.5f + spread, rect.Height * 0.5f + spread);
        PushQuadRaw(r, color.ToVector4(), rectParams, radius, ModeShadow, Vector4.Zero, blur);
    }

    public void DrawBackdropBlur(Rect rect, Vector4 radius, float radiusPx)
    {
        // Real backdrop blur samples the already-composited target within rect and blurs it. That needs a
        // readable copy of the target (read+write the same RT is illegal in DX12). The portable hook: when
        // the host provides a "backdrop SRV" (a copy of the frame pre-UI), sample it here. Until that's
        // wired (renderer-merge), approximate frost with a faint translucent fill so the layout reads
        // correctly and the call is never a no-op surprise. Tracked: P6.2 backdrop source.
        if (_backdropSrvSlot >= 0)
        {
            SetTextureSlot(_backdropSrvSlot);
            PushQuadUV(rect, new Vector4(1, 1, 1, 1), 0, 0, 1, 1, ModeImage); // sampled copy (blur kernel TODO in shader)
        }
        else
        {
            SetTextureSlot(-1);
            PushQuad(rect, new Vector4(1f, 1f, 1f, 0.06f), radius, ModeSolid, Vector4.Zero, 0f);
        }
    }

    // Optional: the host can register a backdrop source (a copy of the frame before UI) for real blur.
    int _backdropSrvSlot = -1;
    public void SetBackdropSource(ID3D12Resource copyOfFrame)
    {
        _backdropSrvSlot = copyOfFrame != null ? RegisterImage(copyOfFrame) : -1;
    }

    public void DrawText(Rect rect, string text, in TextStyle style)
    {
        if (string.IsNullOrEmpty(text)) return;
        var atlas = UIFonts.Resolve(style.FontFamily, style.Bold, style.Italic);
        if (atlas == null || atlas.Pixels == null) return;   // no font baked yet → nothing to draw

        var font = EnsureFont(atlas);                          // its OWN persistent slot (multi-font safe)
        SetTextureSlot(font.Slot);
        lastFontForCb = font;

        float fontSize = style.FontSize > 0 ? style.FontSize : atlas.BakePixelHeight;
        float s = atlas.BakePixelHeight > 0 ? fontSize / atlas.BakePixelHeight : 1f;
        float letter = style.LetterSpacing;
        float lineH = atlas.LineHeight * s;
        float ascent = atlas.Ascent * s;
        Vector4 col = style.Color.ToVector4();
        float invAW = 1f / font.W, invAH = 1f / font.H;

        // Split into lines on '\n'. Block height = lineCount * lineH for vertical alignment.
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
                if (!atlas.TryGetGlyph(line[ci], out var g))
                {
                    // Unknown glyph: advance by a space if we have one, else skip without stalling.
                    if (atlas.TryGetGlyph(' ', out var sp)) penX += sp.Advance * s;
                    continue;
                }

                float gw = g.Width * s, gh = g.Height * s;
                float gx = penX + g.OffsetX * s;
                float gy = baseline + g.OffsetY * s;   // OffsetY is from baseline to glyph-quad top

                if (gw > 0 && gh > 0 && g.AtlasX1 > g.AtlasX0)
                {
                    float u0 = g.AtlasX0 * invAW, v0 = g.AtlasY0 * invAH;
                    float u1 = g.AtlasX1 * invAW, v1 = g.AtlasY1 * invAH;

                    // Drop shadow / glow: a second glyph pass behind, offset + tinted (P1.4).
                    if (style.HasShadow && style.ShadowColor.A > 0f)
                    {
                        Vector4 sc = style.ShadowColor.ToVector4();
                        PushGlyphQuad(gx + style.ShadowOffsetX, gy + style.ShadowOffsetY, gw, gh, u0, v0, u1, v1, sc);
                    }
                    PushGlyphQuad(gx, gy, gw, gh, u0, v0, u1, v1, col);
                }
                penX += g.Advance * s + (ci < line.Length - 1 ? letter : 0f);
            }
        }
    }

    public void DrawImage(Rect rect, object texture, Color tint, ScaleMode scaleMode)
    {
        if (!DrawableRect(rect)) return;
        if (texture == null || tint.A <= 0f) return;

        // The backend accepts an already-GPU-resident handle: a Dx12Texture2D or a raw ID3D12Resource.
        // String paths (UXML src="...") are the caller's responsibility to resolve into a texture (UI
        // stays asset-free). Anything else is skipped — logged once would be ideal; keep it silent for R4.
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

        // ScaleMode → UV rect. StretchToFill = full [0..1]. ScaleToFit/Crop need the source aspect, which
        // we don't have from a bare resource cheaply; R4 treats them as StretchToFill and refines later.
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

    // Flush the index range accumulated since the last clip change into a segment carrying the current
    // scissor. Empty ranges are skipped so a PushClip immediately after another doesn't emit a no-op draw.
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

        // two triangles (TL,TR,BR / TL,BR,BL) — CCW irrelevant since culling is off
        indices.Add(baseIdx + 0); indices.Add(baseIdx + 1); indices.Add(baseIdx + 2);
        indices.Add(baseIdx + 0); indices.Add(baseIdx + 2); indices.Add(baseIdx + 3);
    }

    const uint ModeGradient = 1;
    const uint ModeText = 2;
    const uint ModeImage = 3;

    // Quad with an EXPLICIT rectParams (SDF center/half) — used by box-shadow, whose SDF box (the spread-
    // expanded element box) differs from the padded geometry quad.
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

    // A textured quad with an explicit UV rect (image: mode 3). Carries the rounded box in Rect/Radius so
    // the shader can clip image corners. Color = tint (premultiplied opacity).
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

    // Gradient quad (mode 1): rounded box from Rect/Radius (corners clip). uv = local [0..1]; the shader
    // computes t per-fragment from gradient params packed into Border/BorderWidth (radial needs per-fragment
    // t — see the shader). color.a = opacity; rgb comes from the bound ramp.
    void PushGradientQuad(Rect r, Gradient g, Vector4 radius, Vector4 color)
    {
        float l = r.X, t = r.Y, right = r.X + r.Width, b = r.Y + r.Height;
        var rectParams = new Vector4(r.X + r.Width * 0.5f, r.Y + r.Height * 0.5f,
                                     r.Width * 0.5f, r.Height * 0.5f);
        uint baseIdx = (uint)verts.Count;

        Vector4 gp;       // packed gradient params (→ shader Border)
        float kind;       // → shader BorderWidth: 0 linear, 1 radial
        if (g.Type == Gradient.Kind.Radial)
        {
            gp = new Vector4(g.CenterX, g.CenterY, g.RadiusX, g.RadiusY);
            kind = 1f;
        }
        else
        {
            // CSS angle: 0deg=to-top, 90=to-right, 180=down. Local dir (+Y down): top=(0,-1), right=(1,0).
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

    // A single glyph quad (mode=2). Position in logical pixels, UV in atlas [0..1]. The pixel shader
    // samples the SDF atlas at uv and thresholds it; rect/radius/border are unused for text.
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

    // Ensure the font's R8 SDF atlas is resident in its OWN persistent heap slot. Cached per FontAtlas;
    // rebuilt (in place, same slot) when UIFonts.Version bumps. Multiple fonts in one frame each keep a
    // distinct slot so their glyph runs sample the correct atlas — fixes the multi-font corruption + the
    // per-glyph-run re-upload stall.
    unsafe FontGpu EnsureFont(FontAtlas atlas)
    {
        if (fonts.TryGetValue(atlas, out var cached) && cached.FontVersion == UIFonts.Version && cached.Tex != null)
            return cached;

        var fg = cached ?? new FontGpu();
        if (cached == null)
        {
            // New atlas → new persistent slot (grows the persistent region; transient cursor follows it).
            if (persistentCount >= SrvHeapCapacity) return fonts.Count > 0 ? GetAnyFont() : null;
            fg.Slot = persistentCount++;
            fonts[atlas] = fg;
        }
        fg.Tex?.Dispose();   // version bumped → re-upload into the same slot

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

    // Resolve a gradient to its cached 256x1 ramp slot, baking+uploading once per Gradient instance and
    // keeping it persistent (P1.3 — no per-frame ramp alloc/upload for static UI). Returns -1 if the heap
    // is full.
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
        int slot = persistentCount++;   // persistent ramp slot
        var tex = UploadRgba8ResourceToSlot(ramp, N, 1, "UIGradientRamp", slot);
        ramps[g] = new RampGpu { Tex = tex, Slot = slot };
        return slot;
    }

    // Linear-interpolate the stop list at parameter t (stops assumed sorted by Position; clamps at ends).
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

    // Register an already-resident RGBA texture resource into a free heap slot, returning the slot. The
    // resource is NOT owned here (it belongs to the caller / asset system) — only the descriptor is made.
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

    // Create+upload a small RGBA8 texture into a FREE TRANSIENT slot (freed next frame). For images.
    unsafe int UploadRgba8Texture(byte[] rgba, int w, int h, string name)
    {
        if (srvCursor >= SrvHeapCapacity) return -1;
        int slot = srvCursor++;
        var tex = UploadRgba8ResourceToSlot(rgba, w, h, name, slot);
        transientTextures.Add(tex);
        return slot;
    }

    // Initial slot-0 white seed (persistent placeholder for solid segments).
    unsafe void UploadRgba8TextureToSlot(byte[] rgba, int w, int h, string name, int slot)
    {
        slot0Seed = UploadRgba8ResourceToSlot(rgba, w, h, name, slot);
    }

    // Core: create an RGBA8 texture, upload, and write its SRV into `slot`. Returns the resource; the
    // caller decides lifetime (transient list / persistent cache / seed). No ownership tracking here.
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
        // Close the trailing (unclipped or final-clip) segment so Flush draws everything.
        CloseSegment();
    }

    // ---------------- GPU flush ----------------

    // Record the batched UI geometry onto `target`'s RTV. Call after Begin/walk/End. Separate from End()
    // so the caller controls WHICH target and WHEN (the player draws onto the LDR composite; the editor
    // canvas draws onto its own RT). The target must already be in RenderTarget state.
    public unsafe void Flush(Dx12OffscreenTarget target)
    {
        if (segments.Count == 0) return;

        EnsureBuffers();

        // Upload vertices/indices into the persistent upload-heap buffers.
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

        // Scissor must use the SAME logical→physical mapping the vertex shader uses: geometry is emitted in
        // logical pixels and mapped across the FULL target via InvCanvas = 2/canvasSize, so the on-target
        // scale is target/canvas — NOT the independent ResolvedScale field (they diverge when the root size
        // differs from the logical canvas, or the flush target differs from the update viewport). (P1.6)
        float sx = tw / MathF.Max(1f, canvasSize.X);
        float sy = th / MathF.Max(1f, canvasSize.Y);

        // RenderColorOnly: bind only the color RTV (no DSV — UI has no depth) and DON'T clear, so the UI
        // composites over whatever the scene already wrote into `target`. One draw per clip segment, each
        // with its own scissor.
        target.RenderColorOnly(cl =>
        {
            // Bind the shader-visible SRV heap BEFORE the root signature (heap-before-rootsig discipline;
            // cheap safety even outside the SM6.6 bindless path).
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
                // Point the SRV table at this segment's texture. Solid segments (slot -1) get slot 0 as a
                // harmless placeholder — the shader never samples for mode 0, but the table must be valid.
                int wantSlot = seg.SrvSlot < 0 ? 0 : seg.SrvSlot;
                if (wantSlot != boundSlot)
                {
                    cl.SetGraphicsRootDescriptorTable(1, SrvGpu(wantSlot));
                    boundSlot = wantSlot;
                }

                if (seg.Clipped)
                {
                    var cr = seg.Scissor;
                    // NaN/Inf guard (P1.8): a non-finite clip rect would convert to garbage ints.
                    if (!IsFinite(cr.X) || !IsFinite(cr.Y) || !IsFinite(cr.Width) || !IsFinite(cr.Height))
                        continue;
                    int sl = (int)MathF.Floor(cr.X * sx);
                    int st = (int)MathF.Floor(cr.Y * sy);
                    int sr = (int)MathF.Ceiling((cr.X + cr.Width) * sx);
                    int sb = (int)MathF.Ceiling((cr.Y + cr.Height) * sy);
                    sl = Math.Clamp(sl, 0, tw); st = Math.Clamp(st, 0, th);
                    sr = Math.Clamp(sr, 0, tw); sb = Math.Clamp(sb, 0, th);
                    if (sr <= sl || sb <= st) continue;   // fully clipped — draw nothing
                    cl.RSSetScissorRect(new Vortice.RawRect(sl, st, sr, sb));
                }
                else
                {
                    cl.RSSetScissorRect(tw, th);          // full target
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
            cbUpload = CreateUploadBuffer((sizeof(UIConstants) + 255) & ~255); // 256-byte CBV alignment
            cbMapped = MapUpload(cbUpload);                                    // persistently mapped
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

    // A rect is drawable if finite and has positive area (skips zero-size + NaN quads — P1.8/P1.11).
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
