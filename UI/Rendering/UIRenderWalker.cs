using System;

namespace BallisticEngine.UI;

// Walks a laid-out VisualElement tree and turns it into IUIRenderer calls. This is the engine-side
// render LOGIC, shared by the real GL backend and the headless recording stub — so painter ordering,
// opacity inheritance, radius clamping, border emission, text/image dispatch, and overflow clipping
// are all verified once, without a GPU.
//
// Painter's algorithm: an element draws its own box, then its children in order (last child on top) —
// matching the hit-test's back-to-front order so what you click is what's visually on top. Opacity
// multiplies down the tree (a parent at 0.5 dims its whole subtree). Run AFTER LayoutPass.Solve so
// ResolvedRect is valid.
public static class UIRenderWalker
{
    // Draws one document's tree into `r`. Caller supplies the document so we can read ResolvedScale.
    public static void Draw(UIDocument doc, IUIRenderer r)
    {
        if (doc?.Root == null) return;
        Draw(doc.Root, r, doc.ResolvedScale);
    }

    // Draws an arbitrary laid-out subtree into `r` (Begin/End wrapped). Useful for drawing a detached
    // tree without a UIDocument, and the clean entry point for headless render-walk tests.
    public static void Draw(VisualElement root, IUIRenderer r, float scale = 1f)
    {
        if (root == null) return;
        var canvas = new Vector2(root.ResolvedRect.Width, root.ResolvedRect.Height);
        r.Begin(canvas, scale);
        DrawElement(root, r, 1f);
        r.End();
    }

    static void DrawElement(VisualElement el, IUIRenderer r, float inheritedOpacity) =>
        DrawElement(el, r, inheritedOpacity, 0f, 0f);

    static void DrawElement(VisualElement el, IUIRenderer r, float inheritedOpacity, float offsetX, float offsetY)
    {
        // display:none removes the element AND its subtree from rendering (layout already gave it a
        // zero box via Yoga, but skip defensively so nothing draws).
        if (el.Style.Display == DisplayStyle.None) return;

        float opacity = inheritedOpacity * Math.Clamp(el.Style.Opacity, 0f, 1f);
        if (opacity <= 0f) return; // fully transparent subtree — nothing to emit

        var s = el.Style;

        // CSS transform: translate — a render-time shift that does NOT affect layout. Accumulates down
        // the tree so a translated parent moves its whole subtree. (Rotation/scale are applied only to
        // the small rotated gem markers via dedicated handling; full transform matrices are a future
        // step — translate covers the menu's selection slides + entrance motion.)
        offsetX += s.TranslateX;
        offsetY += s.TranslateY;
        Rect rect = el.ResolvedRect;
        if (offsetX != 0f || offsetY != 0f)
            rect = new Rect(rect.X + offsetX, rect.Y + offsetY, rect.Width, rect.Height);

        Vector4 radius = ClampRadius(rect, s);

        // --- box-shadow (P6.1): drawn BEHIND the element ---
        if (s.HasBoxShadow && s.BoxShadowColor.A > 0f)
            r.DrawShadow(rect, radius, s.BoxShadowOffsetX, s.BoxShadowOffsetY, s.BoxShadowBlur,
                         s.BoxShadowSpread, Premultiply(s.BoxShadowColor, opacity));

        // --- backdrop blur (P6.2): frost what's behind, before the element's own fill ---
        if (s.BackdropBlur > 0f)
            r.DrawBackdropBlur(rect, radius, s.BackdropBlur);

        // --- self: background + border (skip when nothing is visible) ---
        Color border = Premultiply(s.BorderColor, opacity);
        float borderWidth = ResolveBorderWidth(el);

        if (s.BackgroundGradient != null)
        {
            // Gradient background: draw the gradient fill, then the border as a separate stroke-only
            // rect on top (transparent fill) so the border still shows.
            r.DrawGradient(rect, s.BackgroundGradient, radius, opacity);
            if (border.A > 0f && borderWidth > 0f)
                r.DrawRect(rect, Color.Transparent, radius, borderWidth, border);
        }
        else
        {
            Color fill = Premultiply(s.BackgroundColor, opacity);
            if (fill.A > 0f || (border.A > 0f && borderWidth > 0f))
                r.DrawRect(rect, fill, radius, borderWidth, border);
        }

        // --- self: content (image, then text — image is a background, text sits on top) ---
        if (el is Image img && img.Texture != null)
            r.DrawImage(rect, img.Texture, Premultiply(img.Tint, opacity), img.ScaleMode);

        if (el is Label label && !string.IsNullOrEmpty(label.Text))
        {
            var ts = new TextStyle
            {
                Color = Premultiply(s.TextColor, opacity),
                FontSize = s.FontSize,
                Align = s.TextAlign ?? label.TextAlign,
                FontFamily = s.FontFamily,
                LetterSpacing = s.LetterSpacing,
                Bold = s.Bold,
                Italic = s.Italic,
                HasShadow = s.HasTextShadow,
                ShadowOffsetX = s.TextShadowOffsetX,
                ShadowOffsetY = s.TextShadowOffsetY,
                ShadowBlur = s.TextShadowBlur,
                ShadowColor = Premultiply(s.TextShadowColor, opacity),
            };
            r.DrawText(rect, label.Text, in ts);
        }

        // --- clip subtree if overflow is not visible ---
        bool clip = s.Overflow != Overflow.Visible;
        if (clip) r.PushClip(rect);

        // --- children, in order (painter's algorithm) — carry the accumulated translate ---
        var children = el.Children;
        for (int i = 0; i < children.Count; i++)
            DrawElement(children[i], r, opacity, offsetX, offsetY);

        if (clip) r.PopClip();
    }

    // Folds opacity into the alpha channel so the backend gets a single premultiplied color and never
    // needs the tree's opacity context.
    static Color Premultiply(Color c, float opacity) => c.WithAlpha(c.A * opacity);

    // Clamps each corner radius to half the box's shorter side. This is what makes border-radius:999px
    // render as a clean pill instead of an over-arced oval — the port skill's most common CSS gotcha,
    // handled centrally instead of per-port.
    static Vector4 ClampRadius(Rect rect, Style s)
    {
        float max = Math.Max(0f, Math.Min(rect.Width, rect.Height) * 0.5f);
        return new Vector4(
            Math.Min(s.BorderRadiusTopLeft, max),
            Math.Min(s.BorderRadiusTopRight, max),
            Math.Min(s.BorderRadiusBottomRight, max),
            Math.Min(s.BorderRadiusBottomLeft, max));
    }

    // Border width is mirrored on Style for rendering (Yoga has no readback). v1 draws a uniform
    // border (the StyleApplier sets all four edges together via Edge.All).
    static float ResolveBorderWidth(VisualElement el) => el.Style.BorderWidthVisual;
}
