using System;
using System.Collections;
using System.Collections.Generic;

namespace BallisticEngine.UI;

// A virtualized list (P5.7) — UITK's ListView, THE control for large feeds. Only the rows visible in the
// viewport (plus a small buffer) are realized; as you scroll, off-screen rows are RECYCLED and rebound to
// new data indices. With 10,000 items only ~viewport/rowHeight element instances exist.
//
// Usage (UITK parity):
//   var list = new ListView { ItemHeight = 40 };
//   list.MakeItem = () => new Label();
//   list.BindItem = (el, i) => ((Label)el).Text = data[i];
//   list.ItemsSource = data;            // IList
//
// Fixed row height (the common, fast case). Variable height is a later refinement.
public class ListView : VisualElement, IPostLayout
{
    public Func<VisualElement> MakeItem;
    public Action<VisualElement, int> BindItem;
    public float ItemHeight { get; set; } = 30f;
    public int BufferRows { get; set; } = 2;        // extra rows above/below the viewport

    IList _items;
    public IList ItemsSource
    {
        get => _items;
        set { _items = value; _spacer.Style.Height = Length.Points(TotalHeight); _scroll.ScrollOffset = 0; Rebuild(); }
    }

    public event Action<int> ItemClicked;
    public int SelectedIndex { get; private set; } = -1;

    readonly ScrollView _scroll;
    readonly VisualElement _spacer;           // sized to the full content height so the scrollbar is right
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

        // The spacer gives the content its full virtual height; realized rows are absolutely positioned
        // within it at row*ItemHeight.
        _spacer = new Panel();
        _spacer.Style.Position = PositionType.Relative;
        _spacer.PickingEnabled = true;
        _scroll.Add(_spacer);
    }

    // Recompute which rows should be realized for the current scroll offset; bind new ones, recycle gone.
    void Rebuild()
    {
        if (MakeItem == null || BindItem == null || _items == null) return;

        float viewport = _scroll.ResolvedRect.Height;
        if (viewport <= 0) viewport = ResolvedRect.Height;
        if (viewport <= 0) viewport = 400f;             // pre-layout fallback

        float scroll = _scroll.ScrollOffset;
        int first = Math.Max(0, (int)(scroll / ItemHeight) - BufferRows);
        int visible = (int)Math.Ceiling(viewport / ItemHeight) + BufferRows * 2;
        int last = Math.Min(Count - 1, first + visible);

        // Recycle rows now outside [first..last].
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

        // Realize + bind rows in range that aren't realized yet.
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

    // Re-virtualize after layout (viewport size known) and whenever the scroll offset changed.
    float _lastScroll = -1;
    public void OnAfterLayout()
    {
        if (Math.Abs(_scroll.ScrollOffset - _lastScroll) > 0.01f || _realized.Count == 0)
        {
            _lastScroll = _scroll.ScrollOffset;
            Rebuild();
        }
    }

    // Force a rebind of all realized rows (e.g. underlying data mutated).
    public void RefreshItems()
    {
        foreach (var kv in _realized) BindItem(kv.Value, kv.Key);
    }
}
