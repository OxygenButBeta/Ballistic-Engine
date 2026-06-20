
namespace BallisticEngine.UI;

// Drives a layout solve over a VisualElement tree and copies the solved boxes back into each
// element's ResolvedRect. Yoga reports each node's box RELATIVE to its parent's content box; this
// pass accumulates the parent origin so ResolvedRect ends up in absolute PANEL space (top-left
// origin, +Y down) — which is what the renderer and pointer hit-testing want.
//
// Call Solve(root, panelWidth, panelHeight) once per frame after the tree/styles settle (cheap when
// nothing is dirty — Yoga skips clean subtrees internally).
public static class LayoutPass
{
    public static void Solve(VisualElement root, float panelWidth, float panelHeight)
    {
        root.Layout.CalculateLayout(panelWidth, panelHeight);
        Propagate(root, 0f, 0f);
    }

    static void Propagate(VisualElement el, float parentX, float parentY)
    {
        // Yoga's Left/Top are relative to the parent content box; add the parent's absolute origin.
        float x = parentX + el.Layout.LayoutLeft;
        float y = parentY + el.Layout.LayoutTop;
        el.ResolvedRect = new Rect(x, y, el.Layout.LayoutWidth, el.Layout.LayoutHeight);

        var children = el.Children;
        for (int i = 0; i < children.Count; i++)
            Propagate(children[i], x, y);
    }

    // Pointer hit-test: the topmost pickable element whose resolved box contains the point. Walks the
    // tree back-to-front (last child = drawn on top = checked first) so overlapping siblings resolve
    // like the visual stacking order. Skips display:none subtrees and elements with PickingEnabled=false.
    //
    // CLIP-AWARE (P3.5): a point OUTSIDE an overflow:hidden/scroll element's box cannot hit that element's
    // CHILDREN — they're visually clipped, so they must not be clickable either (fixes "click an invisible
    // clipped row"). TRANSFORM-AWARE (P3.5): CSS translate shifts an element + subtree visually; the hit
    // point is shifted by the inverse so a translated button picks where it's drawn, not its layout box.
    public static VisualElement HitTest(VisualElement root, Vector2 point)
    {
        if (root == null || root.Style.Display == DisplayStyle.None) return null;

        // Undo this element's render-time translate so children are tested in the same space they're drawn.
        var s = root.Style;
        Vector2 p = point;
        if (s.TranslateX != 0f || s.TranslateY != 0f)
            p = new Vector2(point.X - s.TranslateX, point.Y - s.TranslateY);

        bool insideSelf = root.ResolvedRect.Contains(p);

        // overflow != visible: a point outside the box clips the whole subtree away.
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
