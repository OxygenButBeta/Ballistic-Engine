namespace BallisticEngine.UI;

public sealed class ContextMenu
{
    readonly VisualElement _panel;
    readonly UIDocument _doc;

    ContextMenu(UIDocument doc, float x, float y, IEnumerable<(string label, Action action)> items)
    {
        _doc = doc;
        _panel = new Panel();
        _panel.AddToClassList("context-menu");
        _panel.Style.Position = PositionType.Absolute;
        _panel.Style.Left = x; _panel.Style.Top = y;
        _panel.Style.FlexDirection = FlexDirection.Column;
        _panel.Style.BackgroundColor = Color.Rgb(45, 45, 45);
        _panel.Style.SetBorderWidth(Edge.All, 1);
        _panel.Style.BorderColor = Color.Rgb(110, 110, 110);
        _panel.Style.BorderRadius = 4;
        _panel.Style.SetPadding(Edge.All, 4);

        foreach (var (label, action) in items)
        {
            var item = new Button(label);
            item.AddToClassList("context-menu-item");
            item.TextAlign = TextAlign.MiddleLeft;
            item.Style.SetPadding(Edge.All, 6);
            var act = action;
            item.Clicked += () => { Close(); act?.Invoke(); };
            _panel.Add(item);
        }
        doc.OverlayLayer?.Add(_panel);
    }

    public static ContextMenu Show(UIDocument doc, float x, float y, params (string label, Action action)[] items)
        => new ContextMenu(doc, x, y, items);

    public void Close() => _panel.RemoveFromHierarchy();
}
