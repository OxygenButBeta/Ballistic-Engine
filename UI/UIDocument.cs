namespace BallisticEngine.UI;

public class UIDocument : Behaviour
{
    static readonly List<UIDocument> _active = new();
    public static IReadOnlyList<UIDocument> Active => _active;

    public static Func<string, string> TextResolver;

    public string Uxml { get; set; }
    public string Uss { get; set; }

    public enum RenderMode
    {
        ScreenSpaceOverlay,

        WorldSpace,
    }
    public RenderMode Mode { get; set; } = RenderMode.ScreenSpaceOverlay;

    public enum ScaleMode
    {
        ConstantPixelSize,

        ScaleWithScreenSize,

        Expand,
        Shrink,
    }
    public ScaleMode Scale { get; set; } = ScaleMode.ScaleWithScreenSize;

    public Vector2 ReferenceResolution { get; set; } = new(1920, 1080);

    public float MatchWidthOrHeight { get; set; } = 0.5f;

    public Vector2 PanelSize { get; set; } = new(1920, 1080);

    public int SortOrder { get; set; }

    [NotSerialized] public VisualElement Root { get; private set; }
    [NotSerialized] public float ResolvedScale { get; private set; } = 1f;

    [NotSerialized] public VisualElement OverlayLayer { get; private set; }

    readonly UIInputModule _input = new();

    readonly System.Collections.Generic.List<StyleSheet> _sheets = new();

    public UIAnimator Animator { get; } = new();

    protected internal override void OnAttach()
    {
        _active.Add(this);
        Rebuild();
    }

    public static void RegisterForTest(UIDocument doc, VisualElement root)
    {
        if (doc == null || root == null) return;
        doc.Root = root;
        if (!_active.Contains(doc)) _active.Add(doc);
    }

    protected internal override void OnDetach()
    {
        _active.Remove(this);
        _input.Reset();
        Root = null;
    }

    public void Rebuild()
    {
        if (string.IsNullOrEmpty(Uxml))
        {
            Root = null;
            return;
        }

        string uxmlText = ResolveText(Uxml);
        if (uxmlText == null)
        {
            Debugging.LogWarning($"UIDocument: could not load UXML '{Uxml}'.");
            Root = null;
            return;
        }

        Root = UxmlLoader.LoadFromText(uxmlText);
        if (Root == null) return;

        AddOverlayLayer();

        _sheets.Clear();
        if (!string.IsNullOrEmpty(Uss))
        {
            foreach (var path in Uss.Split(new[] { ',', ';', '\n', '\r' }, System.StringSplitOptions.RemoveEmptyEntries))
            {
                string ussText = ResolveText(path.Trim());
                if (!string.IsNullOrEmpty(ussText))
                    _sheets.Add(StyleSheet.Parse(ussText));
            }
        }
        ApplyStyles();
    }

    public void ApplyStyles()
    {
        if (Root == null) return;
        AssignOwner(Root);
        StyleResolver.ResolveTree(Root, _sheets, Root.Parent?.Style);
        _restyleDirty.Clear();
    }

    void AddOverlayLayer()
    {
        OverlayLayer = new Panel();
        OverlayLayer.Name = "__overlay";
        OverlayLayer.PickingEnabled = false;
        OverlayLayer.Style.Position = PositionType.Absolute;
        OverlayLayer.Style.Left = 0; OverlayLayer.Style.Top = 0;
        OverlayLayer.Style.Right = 0; OverlayLayer.Style.Bottom = 0;
        Root.Add(OverlayLayer);
    }

    void AssignOwner(VisualElement el)
    {
        el.OwnerDocument = this;
        var children = el.Children;
        for (int i = 0; i < children.Count; i++) AssignOwner(children[i]);
    }

    static void RefreshMeasures(VisualElement el)
    {
        if (el is Label lbl) lbl.RefreshMeasureIfStale();
        var children = el.Children;
        for (int i = 0; i < children.Count; i++) RefreshMeasures(children[i]);
    }

    static void RunPostLayout(VisualElement el)
    {
        if (el is IPostLayout p) p.OnAfterLayout();
        var children = el.Children;
        for (int i = 0; i < children.Count; i++) RunPostLayout(children[i]);
    }

    public void RestyleElement(VisualElement el)
    {
        if (el == null) return;
        StyleResolver.ResolveTree(el, _sheets, el.Parent?.Style);
    }

    public void UpdateFrame(Rect viewport) => UpdateFrame(viewport, 0f);

    readonly System.Collections.Generic.HashSet<VisualElement> _restyleDirty = new();

    internal void MarkRestyleDirty(VisualElement el)
    {
        if (el != null) _restyleDirty.Add(el);
    }

    public void UpdateFrame(Rect viewport, float dt)
    {
        if (Root == null) return;

        if (_restyleDirty.Count > 0)
        {
            foreach (var el in _restyleDirty)
                StyleResolver.ResolveTree(el, _sheets, el.Parent?.Style);
            _restyleDirty.Clear();
        }

        if (dt > 0f) Animator.Tick(dt);

        Vector2 logical = ComputeLogicalSize(viewport.Size, out float scale);
        ResolvedScale = scale;
        LogicalSize = logical;

        RefreshMeasures(Root);

        LayoutPass.Solve(Root, logical.X, logical.Y);

        RunPostLayout(Root);
    }

    [NotSerialized] public Vector2 LogicalSize { get; private set; }

    public void ProcessInput(Rect panelScreenRect)
    {
        if (Root == null) return;
        _input.Update(Root, panelScreenRect, LogicalSize);
    }

    Vector2 ComputeLogicalSize(Vector2 viewportPx, out float scale)
    {
        if (Mode == RenderMode.WorldSpace || Scale == ScaleMode.ConstantPixelSize)
        {
            scale = 1f;
            return Mode == RenderMode.WorldSpace ? PanelSize : viewportPx;
        }

        Vector2 reference = ReferenceResolution;
        float scaleW = viewportPx.X / Math.Max(1f, reference.X);
        float scaleH = viewportPx.Y / Math.Max(1f, reference.Y);
        if (Scale == ScaleMode.Expand)
            scale = Math.Min(scaleW, scaleH);
        else if (Scale == ScaleMode.Shrink)
            scale = Math.Min(1f, Math.Min(scaleW, scaleH));
        else
        {
            float logW = MathF.Log(Math.Max(1e-4f, scaleW));
            float logH = MathF.Log(Math.Max(1e-4f, scaleH));
            scale = MathF.Exp(logW * (1f - MatchWidthOrHeight) + logH * MatchWidthOrHeight);
        }

        return new Vector2(viewportPx.X / scale, viewportPx.Y / scale);
    }

    static string ResolveText(string path) => TextResolver?.Invoke(path);
}
