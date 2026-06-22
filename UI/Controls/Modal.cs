namespace BallisticEngine.UI;

public sealed class Modal
{
    readonly VisualElement _scrim;

    Modal(UIDocument doc, VisualElement content, bool dismissOnScrimClick)
    {
        _scrim = new Panel();
        _scrim.AddToClassList("modal-scrim");
        _scrim.Style.Position = PositionType.Absolute;
        _scrim.Style.Left = 0; _scrim.Style.Top = 0; _scrim.Style.Right = 0; _scrim.Style.Bottom = 0;
        _scrim.Style.BackgroundColor = Color.Rgba(0, 0, 0, 0.5f);
        _scrim.Style.JustifyContent = Justify.Center;
        _scrim.Style.AlignItems = Align.Center;
        _scrim.PickingEnabled = true;

        var box = new Panel();
        box.AddToClassList("modal-box");
        box.Style.BackgroundColor = Color.Rgb(40, 40, 45);
        box.Style.BorderRadius = 8;
        box.Style.SetPadding(Edge.All, 16);
        box.Add(content);
        _scrim.Add(box);

        if (dismissOnScrimClick)
            _scrim.PointerClick += e => { if (e.Target == _scrim) Close(); };

        doc.OverlayLayer?.Add(_scrim);
    }

    public static Modal Show(UIDocument doc, VisualElement content, bool dismissOnScrimClick = true)
        => new Modal(doc, content, dismissOnScrimClick);

    public void Close() => _scrim.RemoveFromHierarchy();
}
