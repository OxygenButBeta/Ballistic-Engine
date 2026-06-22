namespace BallisticEngine.UI;

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
