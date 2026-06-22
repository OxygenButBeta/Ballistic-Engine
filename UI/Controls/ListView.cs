using System.Collections;

namespace BallisticEngine.UI;

public class ListView : VisualElement, IPostLayout
{
    public Func<VisualElement> MakeItem;
    public Action<VisualElement, int> BindItem;
    public float ItemHeight { get; set; } = 30f;
    public int BufferRows { get; set; } = 2;

    IList _items;
    Action _observableHandler;
    public IList ItemsSource
    {
        get => _items;
        set
        {
            if (_items is { } prev && _observableHandler != null && prev.GetType().GetEvent("Changed") is { } ev)
                ev.RemoveEventHandler(prev, _observableHandler);

            _items = value;

            _observableHandler = null;
            var evt = value?.GetType().GetEvent("Changed");
            if (evt != null)
            {
                _observableHandler = OnSourceChanged;
                evt.AddEventHandler(value, _observableHandler);
            }

            _spacer.Style.Height = Length.Points(TotalHeight);
            _scroll.ScrollOffset = 0;
            Rebuild();
        }
    }

    void OnSourceChanged()
    {
        foreach (var kv in _realized) { kv.Value.Style.Display = DisplayStyle.None; _pool.Push(kv.Value); }
        _realized.Clear();
        _spacer.Style.Height = Length.Points(TotalHeight);
        _lastScroll = -1;
        Rebuild();
    }

    public event Action<int> ItemClicked;
    public int SelectedIndex { get; private set; } = -1;

    readonly ScrollView _scroll;
    readonly VisualElement _spacer;
    readonly Dictionary<int, VisualElement> _realized = new();
    readonly Stack<VisualElement> _pool = new();

    int Count => _items?.Count ?? 0;
    float TotalHeight => Count * ItemHeight;

    public ListView()
    {
        AddToClassList("list-view");
        Style.Overflow = Overflow.Hidden;

        _scroll = new ScrollView();
        _scroll.Style.Width = Length.Percent(100);
        _scroll.Style.Height = Length.Percent(100);
        base.Add(_scroll);

        _spacer = new Panel();
        _spacer.Style.Position = PositionType.Relative;
        _spacer.PickingEnabled = true;
        _scroll.Add(_spacer);
    }

    void Rebuild()
    {
        if (MakeItem == null || BindItem == null || _items == null) return;

        float viewport = _scroll.ResolvedRect.Height;
        if (viewport <= 0) viewport = ResolvedRect.Height;
        if (viewport <= 0) viewport = 400f;

        float scroll = _scroll.ScrollOffset;
        int first = Math.Max(0, (int)(scroll / ItemHeight) - BufferRows);
        int visible = (int)Math.Ceiling(viewport / ItemHeight) + BufferRows * 2;
        int last = Math.Min(Count - 1, first + visible);

        var toRemove = new List<int>();
        foreach (var kv in _realized)
            if (kv.Key < first || kv.Key > last) toRemove.Add(kv.Key);
        foreach (var idx in toRemove)
        {
            var el = _realized[idx];
            _realized.Remove(idx);
            el.Style.Display = DisplayStyle.None;
            _pool.Push(el);
        }

        for (int i = first; i <= last; i++)
        {
            if (_realized.ContainsKey(i)) continue;
            VisualElement row = _pool.Count > 0 ? _pool.Pop() : NewRow();
            row.Style.Display = DisplayStyle.Flex;
            row.Style.Position = PositionType.Absolute;
            row.Style.Left = 0; row.Style.Right = 0;
            row.Style.Top = i * ItemHeight;
            row.Style.Height = Length.Points(ItemHeight);
            BindItem(row, i);
            row.UserIndex = i;
            _realized[i] = row;
        }
        _spacer.Style.Height = Length.Points(TotalHeight);
    }

    VisualElement NewRow()
    {
        var content = MakeItem();
        var row = new Panel();
        row.AddToClassList("list-item");
        row.Add(content);
        row.PointerClick += _ => { SelectedIndex = row.UserIndex; ItemClicked?.Invoke(row.UserIndex); };
        _spacer.Add(row);
        return row;
    }

    float _lastScroll = -1;
    public void OnAfterLayout()
    {
        if (Math.Abs(_scroll.ScrollOffset - _lastScroll) > 0.01f || _realized.Count == 0)
        {
            _lastScroll = _scroll.ScrollOffset;
            Rebuild();
        }
    }

    public void RefreshItems()
    {
        foreach (var kv in _realized) BindItem(kv.Value, kv.Key);
    }
}
