using System;
using System.Collections.Generic;

namespace BallisticEngine.UI;

// Tooltip, ContextMenu, and Modal (P5.8) — all popup-style controls that live on the document's overlay
// layer so they draw above content. Static helpers + small classes; designed to be one-call from game code.

// A hover tooltip. Attach to any element: Tooltip.Attach(element, "text"). On hover-enter it shows a small
// label near the pointer; on leave it hides. Uses the overlay layer.
public static class Tooltip
{
    public static void Attach(VisualElement target, string text)
    {
        VisualElement tip = null;
        target.PointerEnter += e =>
        {
            var overlay = target.OwnerDocument?.OverlayLayer;
            if (overlay == null) return;
            tip = new Panel();
            tip.AddToClassList("tooltip");
            tip.PickingEnabled = false;
            tip.Style.Position = PositionType.Absolute;
            tip.Style.Left = e.Position.X + 12;
            tip.Style.Top = e.Position.Y + 18;
            tip.Style.BackgroundColor = Color.Rgba(20, 20, 20, 0.95f);
            tip.Style.SetPadding(Edge.All, 6);
            tip.Style.BorderRadius = 4;
            var lbl = new Label(text); lbl.PickingEnabled = false; lbl.Style.TextColor = Color.White;
            tip.Add(lbl);
            overlay.Add(tip);
        };
        target.PointerLeave += _ => { tip?.RemoveFromHierarchy(); tip = null; };
    }
}

// A right-click / explicit context menu: a vertical list of items at a position, dismissed on item click
// or click-away. ContextMenu.Show(document, x, y, ("Copy", onCopy), ("Delete", onDelete)).
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

// A modal dialog: a full-screen dimmed scrim (blocks input to content) + a centered content panel.
// Modal.Show(document, contentElement). Returns the Modal so the caller can Close() it.
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
        _scrim.PickingEnabled = true;          // swallow clicks to content behind

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
