using System;
using System.Collections.Generic;

namespace BallisticEngine.UI;

// A tabbed view (P5.6) — UITK's TabView. A row of tab buttons over a content area; clicking a tab shows
// its page and hides the others. Add tabs via AddTab(title, content).
public class TabView : VisualElement, INotifyValueChanged<int>
{
    readonly VisualElement _tabBar;
    readonly VisualElement _pages;
    readonly List<Button> _tabs = new();
    readonly List<VisualElement> _contents = new();

    public event Action<int, int> ValueChanged;

    int _selected = -1;
    public int Value
    {
        get => _selected;
        set { if (value == _selected) return; int old = _selected; SetValueWithoutNotify(value); ValueChanged?.Invoke(old, _selected); }
    }
    public void SetValueWithoutNotify(int value)
    {
        _selected = value;
        for (int i = 0; i < _tabs.Count; i++)
        {
            bool sel = i == value;
            _tabs[i].EnableInClassList("selected", sel);
            _contents[i].Style.Display = sel ? DisplayStyle.Flex : DisplayStyle.None;
        }
    }

    public TabView()
    {
        AddToClassList("tab-view");
        Style.FlexDirection = FlexDirection.Column;

        _tabBar = new Panel();
        _tabBar.AddToClassList("tab-bar");
        _tabBar.Style.FlexDirection = FlexDirection.Row;
        _tabBar.Style.Gap = 2;
        base.Add(_tabBar);

        _pages = new Panel();
        _pages.AddToClassList("tab-pages");
        _pages.Style.FlexGrow = 1;
        base.Add(_pages);
    }

    public VisualElement AddTab(string title, VisualElement content = null)
    {
        int index = _tabs.Count;
        var btn = new Button(title);
        btn.AddToClassList("tab");
        btn.Clicked += () => Value = index;
        _tabBar.Add(btn);
        _tabs.Add(btn);

        content ??= new Panel();
        content.AddToClassList("tab-content");
        content.Style.Display = DisplayStyle.None;
        _pages.Add(content);
        _contents.Add(content);

        if (_selected < 0) Value = 0;   // first tab selected by default
        return content;
    }
}
