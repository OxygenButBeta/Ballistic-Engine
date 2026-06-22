namespace BallisticEngine.UI;

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

    public float ScrollSpeed { get; set; } = 40f;
    public bool ShowScrollbar { get; set; } = true;

    public ScrollView()
    {
        Style.Overflow = Overflow.Hidden;
        AddToClassList("scroll-view");

        ContentContainer = new Panel();
        ContentContainer.AddToClassList("scroll-content");
        ContentContainer.Style.Position = PositionType.Relative;
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

        PointerWheel += e =>
        {
            ScrollOffset -= e.ScrollDelta.Y * ScrollSpeed;
            e.Handled = true;
        };

        ContentContainer.PointerMove += e =>
        {
            if (_dragging) { ScrollOffset -= e.Delta.Y; e.Handled = true; }
        };
        PointerDown += e => { _dragging = true; };
        PointerUp += e => { _dragging = false; };

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

    public new void Add(VisualElement child) => ContentContainer.Add(child);
    public new void Insert(int index, VisualElement child) => ContentContainer.Insert(index, child);
    public new void Clear() => ContentContainer.Clear();

    void ClampAndApply()
    {
        float viewport = ResolvedRect.Height;
        float content = ContentContainer.ResolvedRect.Height;
        float max = Math.Max(0f, content - viewport);
        _scrollY = Math.Clamp(_scrollY, 0f, max);

        ContentContainer.Style.TranslateY = -_scrollY;

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

    public void OnAfterLayout() => ClampAndApply();
}
