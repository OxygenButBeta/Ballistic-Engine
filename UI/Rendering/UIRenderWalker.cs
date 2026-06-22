namespace BallisticEngine.UI;

public static class UIRenderWalker
{
    public static void Draw(UIDocument doc, IUIRenderer r)
    {
        if (doc?.Root == null) return;
        Draw(doc.Root, r, doc.ResolvedScale);
    }

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
        if (el.Style.Display == DisplayStyle.None) return;

        float opacity = inheritedOpacity * Math.Clamp(el.Style.Opacity, 0f, 1f);
        if (opacity <= 0f) return;

        var s = el.Style;

        offsetX += s.TranslateX;
        offsetY += s.TranslateY;
        Rect rect = el.ResolvedRect;
        if (offsetX != 0f || offsetY != 0f)
            rect = new Rect(rect.X + offsetX, rect.Y + offsetY, rect.Width, rect.Height);

        Vector4 radius = ClampRadius(rect, s);

        if (s.HasBoxShadow && s.BoxShadowColor.A > 0f)
            r.DrawShadow(rect, radius, s.BoxShadowOffsetX, s.BoxShadowOffsetY, s.BoxShadowBlur,
                         s.BoxShadowSpread, Premultiply(s.BoxShadowColor, opacity));

        if (s.BackdropBlur > 0f)
            r.DrawBackdropBlur(rect, radius, s.BackdropBlur);

        Color border = Premultiply(s.BorderColor, opacity);
        float borderWidth = ResolveBorderWidth(el);

        if (s.BackgroundGradient != null)
        {
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

        bool clip = s.Overflow != Overflow.Visible;
        if (clip) r.PushClip(rect);

        var children = el.Children;
        for (int i = 0; i < children.Count; i++)
            DrawElement(children[i], r, opacity, offsetX, offsetY);

        if (clip) r.PopClip();
    }

    static Color Premultiply(Color c, float opacity) => c.WithAlpha(c.A * opacity);

    static Vector4 ClampRadius(Rect rect, Style s)
    {
        float max = Math.Max(0f, Math.Min(rect.Width, rect.Height) * 0.5f);
        return new Vector4(
            Math.Min(s.BorderRadiusTopLeft, max),
            Math.Min(s.BorderRadiusTopRight, max),
            Math.Min(s.BorderRadiusBottomRight, max),
            Math.Min(s.BorderRadiusBottomLeft, max));
    }

    static float ResolveBorderWidth(VisualElement el) => el.Style.BorderWidthVisual;
}
