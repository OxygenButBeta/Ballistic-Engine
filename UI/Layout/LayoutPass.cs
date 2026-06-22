
namespace BallisticEngine.UI;

public static class LayoutPass
{
    public static void Solve(VisualElement root, float panelWidth, float panelHeight)
    {
        root.Layout.CalculateLayout(panelWidth, panelHeight);
        Propagate(root, 0f, 0f);
    }

    static void Propagate(VisualElement el, float parentX, float parentY)
    {
        float x = parentX + el.Layout.LayoutLeft;
        float y = parentY + el.Layout.LayoutTop;
        el.ResolvedRect = new Rect(x, y, el.Layout.LayoutWidth, el.Layout.LayoutHeight);

        var children = el.Children;
        for (int i = 0; i < children.Count; i++)
            Propagate(children[i], x, y);
    }

    public static VisualElement HitTest(VisualElement root, Vector2 point)
    {
        if (root == null || root.Style.Display == DisplayStyle.None) return null;

        var s = root.Style;
        Vector2 p = point;
        if (s.TranslateX != 0f || s.TranslateY != 0f)
            p = new Vector2(point.X - s.TranslateX, point.Y - s.TranslateY);

        bool insideSelf = root.ResolvedRect.Contains(p);

        if (!insideSelf && s.Overflow != Overflow.Visible)
            return null;

        var children = root.Children;
        for (int i = children.Count - 1; i >= 0; i--)
        {
            var hit = HitTest(children[i], p);
            if (hit != null) return hit;
        }

        return insideSelf && root.PickingEnabled ? root : null;
    }
}
