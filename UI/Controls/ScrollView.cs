using System;

namespace BallisticEngine.UI;

// A scrollable viewport (P5.1) — UITK's ScrollView. Structure: ScrollView (overflow:hidden viewport) →
// `ContentContainer` (the element children are added to; it grows to fit content and is translated to
// scroll). Wheel scrolls vertically; pointer-drag on the content also scrolls (capture-based). The
// scrollbar is a thin thumb drawn as a child, draggable.
//
// Add content via Add()/the ContentContainer; ScrollView reparents children into the content container so
// `scroll.Add(row)` "just works" like Unity.
public class ScrollView : VisualElement, IPostLayout
{
    public VisualElement ContentContainer { get; }
    readonly VisualElement _vScrollbar;
    readonly VisualElement _vThumb;

    float _scrollY;
    public float ScrollOffset
    {
        get => _scrollY;
        set { _scrollY = value; ClampAndApply(); }
    }

    public float ScrollSpeed { get; set; } = 40f;   // pixels per wheel notch
    public bool ShowScrollbar { get; set; } = true;

    public ScrollView()
    {
        Style.Overflow = Overflow.Hidden;          // viewport clips content
        AddToClassList("scroll-view");

        ContentContainer = new Panel();
        ContentContainer.AddToClassList("scroll-content");
        ContentContainer.Style.Position = PositionType.Relative;
        // content lays out top-down by default
        ContentContainer.Style.FlexDirection = FlexDirection.Column;
        base.Add(ContentContainer);

        _vScrollbar = new Panel();
        _vScrollbar.AddToClassList("scrollbar");
        _vScrollbar.Style.Position = PositionType.Absolute;
        _vScrollbar.Style.Right = 0;
        _vScrollbar.Style.Top = 0;
        _vScrollbar.Style.Width = Length.Points(8);
        _vScrollbar.PickingEnabled = true;
        _vThumb = new Panel();
        _vThumb.AddToClassList("scrollbar-thumb");
        _vThumb.Style.Position = PositionType.Absolute;
        _vThumb.Style.Width = Length.Points(8);
        _vThumb.Style.BackgroundColor = Color.Rgba(255, 255, 255, 0.4f);
        _vThumb.Style.BorderRadius = 4;
        _vScrollbar.Add(_vThumb);
        base.Add(_vScrollbar);

        // Wheel scroll (P3.6 feeds this).
        PointerWheel += e =>
        {
            ScrollOffset -= e.ScrollDelta.Y * ScrollSpeed;
            e.Handled = true;
        };

        // Drag-to-scroll on the content (pointer capture keeps it tracking outside the viewport).
        ContentContainer.PointerMove += e =>
        {
            if (_dragging) { ScrollOffset -= e.Delta.Y; e.Handled = true; }
        };
        PointerDown += e => { _dragging = true; };
        PointerUp += e => { _dragging = false; };

        // Thumb drag.
        _vThumb.PointerDown += e => { _thumbDragging = true; e.Handled = true; };
        _vThumb.PointerMove += e =>
        {
            if (!_thumbDragging) return;
            float track = ResolvedRect.Height;
            float content = ContentContainer.ResolvedRect.Height;
            if (content > 0) ScrollOffset += e.Delta.Y * (content / Math.Max(1f, track));
            e.Handled = true;
        };
        _vThumb.PointerUp += e => { _thumbDragging = false; };
    }

    bool _dragging, _thumbDragging;

    // Children go into the content container, not the viewport itself (Unity parity).
    public new void Add(VisualElement child) => ContentContainer.Add(child);
    public new void Insert(int index, VisualElement child) => ContentContainer.Insert(index, child);
    public new void Clear() => ContentContainer.Clear();

    void ClampAndApply()
    {
        float viewport = ResolvedRect.Height;
        float content = ContentContainer.ResolvedRect.Height;
        float max = Math.Max(0f, content - viewport);
        _scrollY = Math.Clamp(_scrollY, 0f, max);

        // Scroll by translating the content up (render-time, doesn't affect layout).
        ContentContainer.Style.TranslateY = -_scrollY;

        // Position the scrollbar thumb proportionally.
        if (ShowScrollbar && content > viewport && viewport > 0)
        {
            _vScrollbar.Style.Height = Length.Points(viewport);
            float thumbH = Math.Max(20f, viewport * (viewport / content));
            float thumbY = max > 0 ? (_scrollY / max) * (viewport - thumbH) : 0f;
            _vThumb.Style.Height = Length.Points(thumbH);
            _vThumb.Style.Top = thumbY;
            _vScrollbar.Style.Display = DisplayStyle.Flex;
        }
        else
        {
            _vScrollbar.Style.Display = DisplayStyle.None;
        }
    }

    // Re-apply clamp after layout (content size known). The document calls this via the post-layout hook;
    // also safe to call manually after adding rows.
    public void OnAfterLayout() => ClampAndApply();
}
