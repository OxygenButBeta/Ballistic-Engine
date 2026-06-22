using BallisticEngine.DX12;
using BallisticEngine.DX12.UI;
using BallisticEngine.UI;
using Vortice.Direct3D12;
using Rect = BallisticEngine.UI.Rect;

namespace BallisticEngine.Editor;

public sealed class UIBuilderCanvas : IDisposable
{
    readonly Dx12UIRenderer _ui;
    Dx12OffscreenTarget _rt;
    int _rtW, _rtH;
    int _uiSlot = -1;
    nint _imguiHandle;
    bool _rtInShaderState;
    int _renderedVersion = -1;
    int _renderedW, _renderedH;
    bool _renderValid;

    float _zoom = 1f;
    Vector2 _pan;
    Vector2 _imgOrigin;
    float _imgScale = 1f;

    public enum PreviewState { Normal, Hover, Focus, Active }
    public PreviewState Preview { get; set; } = PreviewState.Normal;

    enum DragMode { None, Move, ResizeE, ResizeS, ResizeSE, ResizeW, ResizeN, ResizeNW, ResizeNE, ResizeSW }
    DragMode _drag;
    Vector2 _dragStartMouse;
    Rect _dragStartRect;
    Vector2 _grabInsetOffset;
    bool _undoPushed;
    bool _panning;

    public UIBuilderCanvas() => _ui = new Dx12UIRenderer(Dx12Backend.Device);

    void EnsureTarget(int w, int h)
    {
        w = Math.Max(16, w); h = Math.Max(16, h);
        if (_rt != null && _rtW == w && _rtH == h) return;

        ReleaseTarget();
        _rt = new Dx12OffscreenTarget(Dx12Backend.Device, w, h, withDepth: false,
            colorFormat: Dx12OffscreenTarget.ColorFormat, colorReadable: false);
        _rtW = w; _rtH = h;

        _uiSlot = Dx12Backend.UiHeap.Allocate();
        Dx12Backend.Device.Device.CreateShaderResourceView(_rt.RenderTarget, new ShaderResourceViewDescription
        {
            Format = Dx12OffscreenTarget.ColorFormat,
            ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Texture2D = new Texture2DShaderResourceView { MipLevels = 1, MostDetailedMip = 0 },
        }, Dx12Backend.UiHeap.Cpu(_uiSlot));
        _imguiHandle = (nint)Dx12Backend.UiHeap.Gpu(_uiSlot).Ptr;
        _rtInShaderState = false;
        _renderedVersion = -1;
        _renderValid = false;
    }

    void ReleaseTarget()
    {
        if (_uiSlot >= 0) { Dx12Backend.UiHeap.Free(_uiSlot); _uiSlot = -1; }
        _rt?.Dispose(); _rt = null;
        _rtW = _rtH = 0;
        _renderValid = false;
    }

    void RenderDocument(UIBuilderDocument doc, int physW, int physH)
    {
        float lw = doc.CanvasWidth, lh = doc.CanvasHeight;
        EnsureTarget(physW, physH);

        try
        {
            if (_rtInShaderState) { _rt.ColorToRenderTarget(); _rtInShaderState = false; }

            ApplyPreviewState(doc, true);
            doc.ResolveForRender();
            ApplyPreviewState(doc, false);
            LayoutPass.Solve(doc.Root, lw, lh);

            _rt.RenderColorOnlyCleared(_ => { });
            UIRenderWalker.Draw(doc.Root, _ui, 1f);
            _ui.Flush(_rt);

            _renderedVersion = doc.Version;
            _renderedW = physW; _renderedH = physH;
            _renderValid = true;
        }
        catch (Exception e)
        {
            Debugging.LogError($"UI Builder canvas render failed: {e.Message}");
        }
        finally
        {
            try { _rt.ColorToShaderResource(); } catch { }
            _rtInShaderState = true;
        }
    }

    void ApplyPreviewState(UIBuilderDocument doc, bool on)
    {
        if (Preview == PreviewState.Normal || doc.Selection == null) return;
        string cls = Preview switch { PreviewState.Hover => "hover", PreviewState.Focus => "focus", _ => "active" };
        if (on) doc.Selection.AddToClassList(cls);
        else doc.Selection.RemoveFromClassList(cls);
    }

    public void Draw(IEditorGui gui, UIBuilderDocument doc, Vector2 size)
    {
        float lw = doc.CanvasWidth, lh = doc.CanvasHeight;

        float fit = MathF.Min(size.X / MathF.Max(1f, lw), size.Y / MathF.Max(1f, lh));
        if (fit <= 0f || float.IsNaN(fit) || float.IsInfinity(fit)) fit = 1f;
        fit = MathF.Min(fit, 1f);
        _imgScale = MathF.Max(0.05f, fit * _zoom);
        Vector2 imgSize = new(lw * _imgScale, lh * _imgScale);

        Vector2 area = gui.CursorScreenPos;
        IEditorDrawList dl = gui.WindowDrawList;
        dl.AddRectFilled(area, area + size, gui.ColorU32(new Vector4(0.05f, 0.05f, 0.06f, 1f)));

        _imgOrigin = area + (size - imgSize) * 0.5f + _pan;

        int physW = (int)MathF.Round(Math.Clamp(imgSize.X, 16, 8192));
        int physH = (int)MathF.Round(Math.Clamp(imgSize.Y, 16, 8192));

        bool interacting = _drag != DragMode.None;
        if (!_renderValid || _renderedVersion != doc.Version || _renderedW != physW || _renderedH != physH
            || interacting || _previewDirty)
        {
            RenderDocument(doc, physW, physH);
            _previewDirty = false;
        }

        gui.SetCursorScreenPos(_imgOrigin);
        if (_renderValid) gui.Image(_imguiHandle, imgSize);

        gui.SetCursorScreenPos(area);
        gui.Input.InvisibleButton("##uicanvas", size);
        bool hovered = gui.IsItemHovered();
        Vector2 mouseLogical = ScreenToLogical(gui.Input.MousePos);
        bool inCanvas = mouseLogical.X >= 0 && mouseLogical.Y >= 0 && mouseLogical.X <= lw && mouseLogical.Y <= lh;

        HandleZoomPan(gui, hovered);
        HandleInteraction(gui, doc, hovered, inCanvas, mouseLogical);
        DrawOverlay(gui, doc);
    }

    bool _previewDirty;
    public void SetPreview(PreviewState s) { if (Preview != s) { Preview = s; _previewDirty = true; } }

    Vector2 ScreenToLogical(Vector2 screen) => (screen - _imgOrigin) / MathF.Max(1e-4f, _imgScale);
    Vector2 LogicalToScreen(Vector2 logical) => _imgOrigin + logical * _imgScale;

    public bool TryDropTarget(Vector2 screenPos, UIBuilderDocument doc, out Vector2 logical, out VisualElement parent)
    {
        logical = ScreenToLogical(screenPos);
        parent = null;
        if (logical.X < 0 || logical.Y < 0 || logical.X > doc.CanvasWidth || logical.Y > doc.CanvasHeight)
            return false;
        VisualElement hit = LayoutPass.HitTest(doc.Root, logical);
        parent = ContainerOf(hit) ?? doc.Root;
        return true;
    }

    static VisualElement ContainerOf(VisualElement el)
    {
        for (var n = el; n != null; n = n.Parent)
            if (n is Panel or ScrollView) return n;
        return null;
    }

    public void DrawDropPreview(IEditorGui gui, UIBuilderDocument doc, Vector2 screenPos)
    {
        if (!TryDropTarget(screenPos, doc, out _, out var parent)) return;
        IEditorDrawList dl = gui.WindowDrawList;
        uint accent = gui.ColorU32(new Vector4(1f, 0.62f, 0.16f, 0.9f));
        if (parent != null)
        {
            var r = parent.ResolvedRect;
            dl.AddRect(LogicalToScreen(new Vector2(r.X, r.Y)), LogicalToScreen(new Vector2(r.X + r.Width, r.Y + r.Height)), accent, 0f, 2f);
        }
        dl.AddCircleFilled(screenPos, 4f, accent);
    }

    void HandleZoomPan(IEditorGui gui, bool hovered)
    {
        if (!hovered) return;
        float wheel = gui.Input.MouseWheel;
        if (wheel != 0f)
        {
            float old = _zoom;
            _zoom = Math.Clamp(_zoom * MathF.Pow(1.1f, wheel), 0.1f, 8f);
            Vector2 m = gui.Input.MousePos;
            Vector2 logicalAtMouse = (m - _imgOrigin) / MathF.Max(1e-4f, _imgScale);
            float newScale = _imgScale / old * _zoom;
            _pan += (m - _imgOrigin) - logicalAtMouse * newScale;
        }

        if (gui.Input.MouseDragging(2)) { _pan += gui.Input.MouseDelta; _panning = true; }
        if (gui.Input.MouseReleased(2)) _panning = false;
    }

    public void ResetView() { _zoom = 1f; _pan = Vector2.Zero; }

    void HandleInteraction(IEditorGui gui, UIBuilderDocument doc, bool hovered, bool inCanvas, Vector2 mouseLogical)
    {
        if (_panning) return;

        if (hovered && gui.Input.MouseClicked(0) && _drag == DragMode.None)
        {
            DragMode handle = HandleHitTest(doc, gui.Input.MousePos);
            if (handle != DragMode.None && doc.Selection != null && doc.Selection != doc.Root)
            {
                BeginDrag(doc, handle, mouseLogical);
            }
            else if (inCanvas)
            {
                VisualElement hit = LayoutPass.HitTest(doc.Root, mouseLogical);
                if (hit != null && hit == doc.Selection && hit.Parent != null && !gui.KeyShift)
                    doc.Selection = hit.Parent;
                else if (hit != null && gui.KeyCtrl && hit.Parent != null)
                    doc.Selection = hit.Parent;
                else
                    doc.Selection = hit;

                if (doc.Selection != null && doc.Selection != doc.Root)
                    BeginDrag(doc, DragMode.Move, mouseLogical);
            }
        }

        if (_drag != DragMode.None && doc.Selection != null && gui.Input.MouseDown(0))
        {
            Vector2 delta = mouseLogical - _dragStartMouse;
            if ((MathF.Abs(delta.X) > 0.5f || MathF.Abs(delta.Y) > 0.5f) && !_undoPushed)
            {
                doc.PushUndo();
                _undoPushed = true;
            }
            ApplyDrag(doc, doc.Selection, delta);
        }

        if (gui.Input.MouseReleased(0))
        {
            if (_drag != DragMode.None && _undoPushed) doc.SyncInline(doc.Selection);
            _drag = DragMode.None; _undoPushed = false;
        }
    }

    void BeginDrag(UIBuilderDocument doc, DragMode mode, Vector2 mouseLogical)
    {
        _drag = mode;
        _dragStartMouse = mouseLogical;
        _dragStartRect = doc.Selection.ResolvedRect;
        _undoPushed = false;
        var el = doc.Selection;
        Vector2 parentContent = ContentOrigin(el.Parent);
        _grabInsetOffset = new Vector2(_dragStartRect.X - parentContent.X, _dragStartRect.Y - parentContent.Y);
    }

    static Vector2 ContentOrigin(VisualElement parent)
    {
        if (parent == null) return Vector2.Zero;
        var r = parent.ResolvedRect;
        var s = parent.Style;
        float bl = Math.Max(0, s.GetBorderWidth(Edge.Left));
        float bt = Math.Max(0, s.GetBorderWidth(Edge.Top));
        float pl = Nz(s.GetPadding(Edge.Left));
        float pt = Nz(s.GetPadding(Edge.Top));
        return new Vector2(r.X + bl + pl, r.Y + bt + pt);
    }
    static float Nz(float v) => float.IsNaN(v) ? 0f : v;

    void ApplyDrag(UIBuilderDocument doc, VisualElement el, Vector2 delta)
    {
        var s = el.Style;
        switch (_drag)
        {
            case DragMode.Move:
                s.Position = PositionType.Absolute;
                s.Left = _grabInsetOffset.X + delta.X;
                s.Top = _grabInsetOffset.Y + delta.Y;
                break;
            case DragMode.ResizeE: s.Width = W(_dragStartRect.Width + delta.X); break;
            case DragMode.ResizeS: s.Height = H(_dragStartRect.Height + delta.Y); break;
            case DragMode.ResizeSE: s.Width = W(_dragStartRect.Width + delta.X); s.Height = H(_dragStartRect.Height + delta.Y); break;
            case DragMode.ResizeW: s.Width = W(_dragStartRect.Width - delta.X); break;
            case DragMode.ResizeN: s.Height = H(_dragStartRect.Height - delta.Y); break;
            case DragMode.ResizeNW: s.Width = W(_dragStartRect.Width - delta.X); s.Height = H(_dragStartRect.Height - delta.Y); break;
            case DragMode.ResizeNE: s.Width = W(_dragStartRect.Width + delta.X); s.Height = H(_dragStartRect.Height - delta.Y); break;
            case DragMode.ResizeSW: s.Width = W(_dragStartRect.Width - delta.X); s.Height = H(_dragStartRect.Height + delta.Y); break;
        }
        doc.MarkDirty();
    }
    static Length W(float v) => Length.Points(MathF.Max(4f, v));
    static Length H(float v) => Length.Points(MathF.Max(4f, v));

    const float HandleR = 5f;

    DragMode HandleHitTest(UIBuilderDocument doc, Vector2 mouseScreen)
    {
        if (doc.Selection == null || doc.Selection == doc.Root) return DragMode.None;
        Rect r = doc.Selection.ResolvedRect;
        foreach (var (mode, p) in Handles(r))
            if (Near(mouseScreen, p)) return mode;
        return DragMode.None;
    }

    System.Collections.Generic.IEnumerable<(DragMode, Vector2)> Handles(Rect r)
    {
        float x0 = r.X, y0 = r.Y, x1 = r.X + r.Width, y1 = r.Y + r.Height, cx = r.X + r.Width * 0.5f, cy = r.Y + r.Height * 0.5f;
        yield return (DragMode.ResizeNW, LogicalToScreen(new Vector2(x0, y0)));
        yield return (DragMode.ResizeN, LogicalToScreen(new Vector2(cx, y0)));
        yield return (DragMode.ResizeNE, LogicalToScreen(new Vector2(x1, y0)));
        yield return (DragMode.ResizeW, LogicalToScreen(new Vector2(x0, cy)));
        yield return (DragMode.ResizeE, LogicalToScreen(new Vector2(x1, cy)));
        yield return (DragMode.ResizeSW, LogicalToScreen(new Vector2(x0, y1)));
        yield return (DragMode.ResizeS, LogicalToScreen(new Vector2(cx, y1)));
        yield return (DragMode.ResizeSE, LogicalToScreen(new Vector2(x1, y1)));
    }

    static bool Near(Vector2 a, Vector2 b) => (a - b).LengthSquared() <= (HandleR + 3f) * (HandleR + 3f);

    void DrawOverlay(IEditorGui gui, UIBuilderDocument doc)
    {
        if (doc.Selection == null) return;
        Rect r = doc.Selection.ResolvedRect;
        Vector2 min = LogicalToScreen(new Vector2(r.X, r.Y));
        Vector2 max = LogicalToScreen(new Vector2(r.X + r.Width, r.Y + r.Height));
        IEditorDrawList dl = gui.WindowDrawList;

        uint accent = gui.ColorU32(new Vector4(1f, 0.62f, 0.16f, 1f));
        dl.AddRect(min, max, accent, 0f, 1.5f);
        if (doc.Selection == doc.Root) return;

        foreach (var (_, p) in Handles(r))
        {
            dl.AddRectFilled(p - new Vector2(HandleR), p + new Vector2(HandleR), accent, 2f);
            dl.AddRect(p - new Vector2(HandleR), p + new Vector2(HandleR), gui.ColorU32(new Vector4(0, 0, 0, 0.6f)), 2f, 1f);
        }

        if (_drag != DragMode.None)
        {
            string txt = $"{(int)r.Width} x {(int)r.Height}";
            dl.AddText(min + new Vector2(2, -16), gui.ColorU32(new Vector4(1, 1, 1, 0.9f)), txt);
        }
    }

    public void RenderToBmp(UIBuilderDocument doc, string bmpPath)
    {
        int pw = (int)MathF.Round(Math.Clamp(doc.CanvasWidth, 16, 4096));
        int ph = (int)MathF.Round(Math.Clamp(doc.CanvasHeight, 16, 4096));
        RenderDocument(doc, pw, ph);
        _rt.SaveBmp(bmpPath);
        _rtInShaderState = false;
    }

    public void Dispose()
    {
        ReleaseTarget();
        _ui.Dispose();
    }
}
