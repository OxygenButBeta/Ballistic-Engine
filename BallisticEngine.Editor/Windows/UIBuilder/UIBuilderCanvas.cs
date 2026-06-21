using System;
using System.Numerics;
using BallisticEngine.DX12;
using BallisticEngine.DX12.UI;
using BallisticEngine.UI;
using Vortice.Direct3D12;
using Rect = BallisticEngine.UI.Rect;

namespace BallisticEngine.Editor;

// The design SURFACE of the UI Builder: renders the authored VisualElement tree pixel-perfect through
// the REAL engine UI backend (Dx12UIRenderer) into an offscreen RT, shows that RT in the editor via
// gui.Image(), then paints a selection box + drag/resize handles ON TOP with the editor draw-list and
// routes mouse interaction back into the document.
//
// Why the real backend (vs ImGui draw-list approximations): rounded corners, gradients, SDF text,
// box-shadow all render EXACTLY as the player will draw them — the builder is WYSIWYG, not an
// approximation. The RT's SRV lives in Dx12Backend.UiHeap (the same heap the ImGui backend samples as
// ImTextureID), so handing the RT to gui.Image is zero-copy.
//
// PERF: the RT is re-rendered ONLY when the document Version changes, the canvas size/zoom changes, or an
// interaction is in progress — otherwise gui.Image re-samples the cached RT with zero GPU work (the
// per-frame-flush fix). ROBUSTNESS: RenderDocument is exception-guarded so a bad element never escapes
// OnGui (which would unbalance ImGui's begin/end and corrupt the whole editor frame), and the RT always
// lands in PixelShaderResource so ImGui's sample is valid.
public sealed class UIBuilderCanvas : IDisposable
{
    readonly Dx12UIRenderer _ui;
    Dx12OffscreenTarget _rt;
    int _rtW, _rtH;
    int _uiSlot = -1;            // UiHeap slot holding the RT's SRV (the ImGui texture id)
    nint _imguiHandle;
    bool _rtInShaderState;       // true after a render: RT is in PixelShaderResource (sampled by ImGui)
    int _renderedVersion = -1;   // doc.Version the RT currently reflects (-1 = never)
    int _renderedW, _renderedH;  // physical RT size the last render used (re-render when the fit changes)
    bool _renderValid;           // false until the first successful render (don't Image() a blank RT)

    // View transform (zoom + pan), in addition to the fit-to-area base scale.
    float _zoom = 1f;
    Vector2 _pan;                // screen-px offset
    Vector2 _imgOrigin;          // top-left of the canvas image in screen space (after fit+pan)
    float _imgScale = 1f;        // logical-px -> screen-px (fit * zoom)

    // Live preview of an interaction pseudo-state on the selection (so :hover/:focus/:active rules show).
    public enum PreviewState { Normal, Hover, Focus, Active }
    public PreviewState Preview { get; set; } = PreviewState.Normal;

    // Interaction state.
    enum DragMode { None, Move, ResizeE, ResizeS, ResizeSE, ResizeW, ResizeN, ResizeNW, ResizeNE, ResizeSW }
    DragMode _drag;
    Vector2 _dragStartMouse;     // logical px at drag start
    Rect _dragStartRect;         // selection's resolved rect at grab (logical, absolute)
    Vector2 _grabInsetOffset;    // (rendered pos) - (computed absolute inset) at grab, so move is jump-free
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
        _renderedVersion = -1;   // RT recreated → force a re-render
        _renderValid = false;
    }

    void ReleaseTarget()
    {
        if (_uiSlot >= 0) { Dx12Backend.UiHeap.Free(_uiSlot); _uiSlot = -1; }
        _rt?.Dispose(); _rt = null;
        _rtW = _rtH = 0;
        _renderValid = false;
    }

    // Render the document tree into the RT. Resolves the USS cascade (so classes style the canvas) at the
    // canvas's LOGICAL size, draws through the real UI backend, transitions the RT to a shader resource.
    // Exception-guarded: any failure logs and still leaves the RT in PixelShaderResource so ImGui's later
    // gui.Image sample is valid and OnGui never throws (which would corrupt the editor's ImGui frame).
    // `physW/physH` is the on-SCREEN pixel size the image will occupy (fit * zoom * DPI). We render the RT
    // at THAT resolution — not the logical canvas size — so gui.Image presents it 1:1 with no downscale (a
    // logical-sized RT shown smaller was being bilinear-downsampled, which shredded small SDF text). The UI
    // backend maps logical geometry across the full physical target (Flush scales by target/canvas), so
    // text rasterizes at the real pixel density and stays crisp at any zoom.
    void RenderDocument(UIBuilderDocument doc, int physW, int physH)
    {
        float lw = doc.CanvasWidth, lh = doc.CanvasHeight;
        EnsureTarget(physW, physH);

        try
        {
            if (_rtInShaderState) { _rt.ColorToRenderTarget(); _rtInShaderState = false; }

            // Apply USS classes + inline overrides for real (the cascade), set the preview pseudo-state,
            // then solve layout at the LOGICAL size (the RT's physical size is handled by Flush's scissor
            // mapping = target/canvas, so geometry stays in logical px and the shader's InvCanvas matches).
            ApplyPreviewState(doc, true);
            doc.ResolveForRender();
            ApplyPreviewState(doc, false);   // resolver may have reset classes; re-apply preview on top
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
            // Leave _renderValid as-is (show the last good frame); fall through to PSR transition so the
            // RT is samplable regardless.
        }
        finally
        {
            // Always end in PixelShaderResource so gui.Image samples a valid resource (never RENDER_TARGET).
            try { _rt.ColorToShaderResource(); } catch { }
            _rtInShaderState = true;
        }
    }

    // Toggle the preview pseudo-state class on the selection (so :hover/:focus/:active USS rules apply in
    // the canvas). `on` adds it before resolve; we call with false after resolve to leave the tree clean
    // for serialization (the class would otherwise be written to disk).
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

        // Base fit (never auto-upscale past 1:1), times the user zoom.
        float fit = MathF.Min(size.X / MathF.Max(1f, lw), size.Y / MathF.Max(1f, lh));
        if (fit <= 0f || float.IsNaN(fit) || float.IsInfinity(fit)) fit = 1f;
        fit = MathF.Min(fit, 1f);
        _imgScale = MathF.Max(0.05f, fit * _zoom);
        Vector2 imgSize = new(lw * _imgScale, lh * _imgScale);

        Vector2 area = gui.CursorScreenPos;
        IEditorDrawList dl = gui.WindowDrawList;
        dl.AddRectFilled(area, area + size, gui.ColorU32(new Vector4(0.05f, 0.05f, 0.06f, 1f)));

        _imgOrigin = area + (size - imgSize) * 0.5f + _pan;

        // Render the RT at the on-screen PIXEL size (so gui.Image is 1:1 — no downscale blur on SDF text).
        int physW = (int)MathF.Round(Math.Clamp(imgSize.X, 16, 8192));
        int physH = (int)MathF.Round(Math.Clamp(imgSize.Y, 16, 8192));

        // Re-render only when something changed (version / physical size / interacting / preview), else
        // re-sample the cached RT — zero GPU work.
        bool interacting = _drag != DragMode.None;
        if (!_renderValid || _renderedVersion != doc.Version || _renderedW != physW || _renderedH != physH
            || interacting || _previewDirty)
        {
            RenderDocument(doc, physW, physH);
            _previewDirty = false;
        }

        gui.SetCursorScreenPos(_imgOrigin);
        if (_renderValid) gui.Image(_imguiHandle, imgSize);

        // Full-area invisible button captures clicks/drags/wheel.
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

    // ---- palette drop (drag from the palette, drop onto the canvas at the cursor) ----------------------
    // True when `screenPos` is over the canvas image, with the logical drop point + the container the point
    // is inside (a Panel/ScrollView under the cursor, else the root). The window uses this to drop a NEW
    // element at the cursor: parented to that container, absolutely positioned at the local drop point — so
    // "where you drop is where it lands", not a guessy flex append.
    public bool TryDropTarget(Vector2 screenPos, UIBuilderDocument doc, out Vector2 logical, out VisualElement parent)
    {
        logical = ScreenToLogical(screenPos);
        parent = null;
        if (logical.X < 0 || logical.Y < 0 || logical.X > doc.CanvasWidth || logical.Y > doc.CanvasHeight)
            return false;
        // The deepest CONTAINER under the point (skip leaf controls — you drop INTO panels, not into a label).
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

    // Draw a drop indicator (a marker at the cursor + a highlight on the target container) while a palette
    // drag hovers the canvas. Called by the window during the drag-drop target block.
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
            // Zoom toward the cursor: keep the logical point under the mouse fixed.
            Vector2 m = gui.Input.MousePos;
            Vector2 logicalAtMouse = (m - _imgOrigin) / MathF.Max(1e-4f, _imgScale);
            float newScale = _imgScale / old * _zoom;
            _pan += (m - _imgOrigin) - logicalAtMouse * newScale;
        }
        // Middle-drag pan.
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
                // Repeat-click on the already-selected element selects its PARENT (click-through to
                // containers), Unity-style. Alt-click also selects parent directly.
                if (hit != null && hit == doc.Selection && hit.Parent != null && !gui.KeyShift)
                    doc.Selection = hit.Parent;
                else if (hit != null && gui.KeyCtrl && hit.Parent != null)
                    doc.Selection = hit.Parent;
                else
                    doc.Selection = hit;

                if (doc.Selection != null && doc.Selection != doc.Root)
                    BeginDrag(doc, DragMode.Move, mouseLogical);
            }
            // click in letterbox margin = inert (no deselect)
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
            if (_drag != DragMode.None && _undoPushed) doc.SyncInline(doc.Selection);  // commit inline overrides
            _drag = DragMode.None; _undoPushed = false;
        }
    }

    void BeginDrag(UIBuilderDocument doc, DragMode mode, Vector2 mouseLogical)
    {
        _drag = mode;
        _dragStartMouse = mouseLogical;
        _dragStartRect = doc.Selection.ResolvedRect;
        _undoPushed = false;
        // Capture the offset between where the element is RENDERED and where its absolute inset would place
        // it, so switching relative->absolute on first move doesn't jump (accounts for parent padding/border).
        var el = doc.Selection;
        Vector2 parentContent = ContentOrigin(el.Parent);
        _grabInsetOffset = new Vector2(_dragStartRect.X - parentContent.X, _dragStartRect.Y - parentContent.Y);
    }

    // The parent's CONTENT-box origin (border-box origin + border + padding left/top) — the reference an
    // absolutely-positioned child's left/top is measured from. Falls back to (0,0) for a null parent.
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
        if (doc.Selection == doc.Root) return;   // no resize handles on the root canvas

        foreach (var (_, p) in Handles(r))
        {
            dl.AddRectFilled(p - new Vector2(HandleR), p + new Vector2(HandleR), accent, 2f);
            dl.AddRect(p - new Vector2(HandleR), p + new Vector2(HandleR), gui.ColorU32(new Vector4(0, 0, 0, 0.6f)), 2f, 1f);
        }

        // Size readout during a drag.
        if (_drag != DragMode.None)
        {
            string txt = $"{(int)r.Width} x {(int)r.Height}";
            dl.AddText(min + new Vector2(2, -16), gui.ColorU32(new Vector4(1, 1, 1, 0.9f)), txt);
        }
    }

    // Headless verification hook: render + save BMP without an editor window.
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
