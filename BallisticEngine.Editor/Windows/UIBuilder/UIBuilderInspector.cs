using System;
using System.Linq;
using System.Numerics;
using BallisticEngine.UI;
using Color = BallisticEngine.UI.Color;

namespace BallisticEngine.Editor;

// The right-hand properties pane of the UI Builder: edits the selected element's identity (name, classes),
// text, and the full Style surface through the IEditorGui seam. Edits write onto the live VisualElement's
// Style for instant WYSIWYG, then reconcile the element's INLINE OVERRIDE store (doc.SyncInline) so a
// class-provided value is never frozen into the saved inline style (the inline-shadows-class fix). One undo
// entry is pushed per edit gesture.
//
// Hand-authored (not the attribute DrawerRegistry): Style is a CSS-semantics type (Length units, per-edge
// box model, premultiplied colors) the component-member pipeline doesn't model; the widgets here are
// CSS-shaped, which is the right vocabulary for a stylesheet editor.
internal sealed class UIBuilderInspector
{
    bool _gesturePushed;
    string _newClass = "";

    public void Draw(IEditorGui gui, UIBuilderDocument doc)
    {
        VisualElement el = doc.Selection;
        if (el == null)
        {
            gui.TextDisabled("No element selected.");
            gui.Spacing();
            gui.TextWrapped("Click an element on the canvas, drag one from the palette, or pick one in the Hierarchy.");
            return;
        }

        gui.PushFont(EditorFont.Bold); gui.Text(el.TypeName); gui.PopFont();
        if (el == doc.Root) { gui.SameLine(); gui.TextDisabled("(root)"); }
        gui.Separator();

        DrawIdentity(gui, doc, el);
        DrawClasses(gui, doc, el);
        gui.Spacing();

        if (gui.CollapsingHeader("Layout", defaultOpen: true)) DrawLayout(gui, doc, el);
        if (gui.CollapsingHeader("Box (margin / padding / border)")) DrawBox(gui, doc, el);
        if (gui.CollapsingHeader("Appearance", defaultOpen: true)) DrawAppearance(gui, doc, el);
        if (gui.CollapsingHeader("Transform")) DrawTransform(gui, doc, el);
        if (gui.CollapsingHeader("Text", defaultOpen: el is Label)) DrawText(gui, doc, el);
        if (el is Image && gui.CollapsingHeader("Image", defaultOpen: true)) DrawImage(gui, doc, (Image)el);
    }

    // ---- identity ------------------------------------------------------------
    void DrawIdentity(IEditorGui gui, UIBuilderDocument doc, VisualElement el)
    {
        string name = el.Name ?? "";
        gui.SetNextItemWidth(-1);
        if (Edit(gui, doc, el, () => gui.InputText("Name", ref name, 64), reconcile: false))
            el.Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();

        if (el is Label lbl)
        {
            string text = lbl.Text ?? "";
            gui.SetNextItemWidth(-1);
            if (Edit(gui, doc, el, () => gui.InputText("Text", ref text, 1024), reconcile: false))
                lbl.Text = text;
        }
    }

    // ---- classes (assign / remove; the USS class-list editor side) -----------
    void DrawClasses(IEditorGui gui, UIBuilderDocument doc, VisualElement el)
    {
        gui.TextDisabled("Classes");
        foreach (var c in UIBuilderDocument.UserClasses(el).ToList())
        {
            gui.PushId(c);
            if (gui.SmallButton(EditorIcons.Cancel)) doc.RemoveClass(el, c);
            gui.SameLine(); gui.Text("." + c);
            gui.PopId();
        }
        gui.SetNextItemWidth(gui.ContentRegionAvail.X * 0.6f);
        bool add = gui.InputTextEnter("##newclass", ref _newClass, 64);
        gui.SameLine();
        if ((gui.SmallButton("+ Add class") || add) && !string.IsNullOrWhiteSpace(_newClass))
        {
            doc.AddClass(el, _newClass.Trim());
            _newClass = "";
        }

        var matched = doc.MatchedSelectors(el).ToList();
        if (matched.Count > 0)
        {
            gui.TextDisabled("Matched: " + string.Join("  ", matched));
        }
    }

    // ---- layout --------------------------------------------------------------
    void DrawLayout(IEditorGui gui, UIBuilderDocument doc, VisualElement el)
    {
        var s = el.Style;
        LengthField(gui, doc, el, "Width", s.Width, v => s.Width = v);
        LengthField(gui, doc, el, "Height", s.Height, v => s.Height = v);

        FloatField(gui, doc, el, "Min Width", s.MinWidth, v => s.MinWidth = v, 0, 9999);
        FloatField(gui, doc, el, "Min Height", s.MinHeight, v => s.MinHeight = v, 0, 9999);
        NanFloatField(gui, doc, el, "Max Width", s.MaxWidth, v => s.MaxWidth = v);
        NanFloatField(gui, doc, el, "Max Height", s.MaxHeight, v => s.MaxHeight = v);

        EnumCombo(gui, doc, el, "Position", s.Position, v => s.Position = v);
        if (s.Position != PositionType.Relative || HasInset(s))
        {
            InsetField(gui, doc, el, "Left", Edge.Left, s.GetInset(Edge.Left), v => s.Left = v);
            InsetField(gui, doc, el, "Top", Edge.Top, s.GetInset(Edge.Top), v => s.Top = v);
            InsetField(gui, doc, el, "Right", Edge.Right, s.GetInset(Edge.Right), v => s.Right = v);
            InsetField(gui, doc, el, "Bottom", Edge.Bottom, s.GetInset(Edge.Bottom), v => s.Bottom = v);
        }

        EnumCombo(gui, doc, el, "Display", s.Display, v => s.Display = v);
        EnumCombo(gui, doc, el, "Overflow", s.Overflow, v => s.Overflow = v);
        EnumCombo(gui, doc, el, "Flex Direction", s.FlexDirection, v => s.FlexDirection = v);
        EnumCombo(gui, doc, el, "Flex Wrap", s.FlexWrap, v => s.FlexWrap = v);
        EnumCombo(gui, doc, el, "Justify", s.JustifyContent, v => s.JustifyContent = v);
        EnumCombo(gui, doc, el, "Align Items", s.AlignItems, v => s.AlignItems = v);
        EnumCombo(gui, doc, el, "Align Content", s.AlignContent, v => s.AlignContent = v);
        EnumCombo(gui, doc, el, "Align Self", s.AlignSelf, v => s.AlignSelf = v);

        FloatField(gui, doc, el, "Flex Grow", s.FlexGrow, v => s.FlexGrow = v, 0, 100, 0.1f);
        FloatField(gui, doc, el, "Flex Shrink", s.FlexShrink, v => s.FlexShrink = v, 0, 100, 0.1f);
        LengthField(gui, doc, el, "Flex Basis", s.FlexBasis, v => s.FlexBasis = v);
        FloatField(gui, doc, el, "Gap", s.Gap, v => s.Gap = v, 0, 999);
        NanFloatField(gui, doc, el, "Aspect Ratio", s.AspectRatio, v => s.AspectRatio = v);
    }

    static bool HasInset(Style s) =>
        !float.IsNaN(s.GetInset(Edge.Left)) || !float.IsNaN(s.GetInset(Edge.Top)) ||
        !float.IsNaN(s.GetInset(Edge.Right)) || !float.IsNaN(s.GetInset(Edge.Bottom));

    // ---- box model (per-edge margin / padding + border width) ----------------
    void DrawBox(IEditorGui gui, UIBuilderDocument doc, VisualElement el)
    {
        var s = el.Style;
        gui.TextDisabled("Margin");
        EdgeField(gui, doc, el, "M", s.GetMargin, (e, v) => s.SetMargin(e, v));
        gui.TextDisabled("Padding");
        EdgeField(gui, doc, el, "P", s.GetPadding, (e, v) => s.SetPadding(e, v));
        float bw = s.BorderWidthVisual;
        if (Edit(gui, doc, el, () => gui.DragFloat("Border Width", ref bw, 0.2f, 0, 64)))
            s.SetBorderWidth(Edge.All, bw);
    }

    // ---- appearance ----------------------------------------------------------
    void DrawAppearance(IEditorGui gui, UIBuilderDocument doc, VisualElement el)
    {
        var s = el.Style;
        ColorField(gui, doc, el, "Background", s.BackgroundColor, c => s.BackgroundColor = c);
        ColorField(gui, doc, el, "Border Color", s.BorderColor, c => s.BorderColor = c);

        float radius = s.BorderRadiusTopLeft;
        if (Edit(gui, doc, el, () => gui.DragFloat("Corner Radius", ref radius, 0.5f, 0, 999)))
            s.BorderRadius = radius;
        // per-corner
        Corner(gui, doc, el, "  TL", s.BorderRadiusTopLeft, v => s.BorderRadiusTopLeft = v);
        Corner(gui, doc, el, "  TR", s.BorderRadiusTopRight, v => s.BorderRadiusTopRight = v);
        Corner(gui, doc, el, "  BR", s.BorderRadiusBottomRight, v => s.BorderRadiusBottomRight = v);
        Corner(gui, doc, el, "  BL", s.BorderRadiusBottomLeft, v => s.BorderRadiusBottomLeft = v);

        float op = s.Opacity;
        if (Edit(gui, doc, el, () => gui.SliderFloat("Opacity", ref op, 0f, 1f, "%.2f"))) s.Opacity = op;

        // box-shadow
        bool sh = s.HasBoxShadow;
        if (Edit(gui, doc, el, () => gui.Checkbox("Box Shadow", ref sh))) s.HasBoxShadow = sh;
        if (s.HasBoxShadow)
        {
            FloatField(gui, doc, el, "  Offset X", s.BoxShadowOffsetX, v => s.BoxShadowOffsetX = v, -200, 200);
            FloatField(gui, doc, el, "  Offset Y", s.BoxShadowOffsetY, v => s.BoxShadowOffsetY = v, -200, 200);
            FloatField(gui, doc, el, "  Blur", s.BoxShadowBlur, v => s.BoxShadowBlur = v, 0, 200);
            FloatField(gui, doc, el, "  Spread", s.BoxShadowSpread, v => s.BoxShadowSpread = v, 0, 200);
            ColorField(gui, doc, el, "  Shadow Color", s.BoxShadowColor, c => s.BoxShadowColor = c);
        }
    }

    void DrawTransform(IEditorGui gui, UIBuilderDocument doc, VisualElement el)
    {
        var s = el.Style;
        FloatField(gui, doc, el, "Translate X", s.TranslateX, v => s.TranslateX = v, -9999, 9999);
        FloatField(gui, doc, el, "Translate Y", s.TranslateY, v => s.TranslateY = v, -9999, 9999);
        FloatField(gui, doc, el, "Rotation", s.RotationDegrees, v => s.RotationDegrees = v, -360, 360);
        FloatField(gui, doc, el, "Scale", s.Scale, v => s.Scale = v, 0.01f, 10, 0.01f);
    }

    void DrawText(IEditorGui gui, UIBuilderDocument doc, VisualElement el)
    {
        var s = el.Style;
        ColorField(gui, doc, el, "Color", s.TextColor, c => s.TextColor = c);
        FloatField(gui, doc, el, "Font Size", s.FontSize, v => s.FontSize = v, 1, 256);
        string fam = s.FontFamily ?? "";
        if (Edit(gui, doc, el, () => gui.InputText("Font Family", ref fam, 64)))
            s.FontFamily = string.IsNullOrWhiteSpace(fam) ? null : fam.Trim();
        FloatField(gui, doc, el, "Letter Spacing", s.LetterSpacing, v => s.LetterSpacing = v, -20, 100);

        var align = s.TextAlign ?? (el is Label l ? l.TextAlign : TextAlign.MiddleLeft);
        if (EnumComboValue(gui, doc, el, "Align", align, out var na)) s.TextAlign = na;
        EnumCombo(gui, doc, el, "White Space", s.WhiteSpace, v => s.WhiteSpace = v);

        bool bold = s.Bold;
        if (Edit(gui, doc, el, () => gui.Checkbox("Bold", ref bold))) s.Bold = bold;
        gui.SameLine();
        bool italic = s.Italic;
        if (Edit(gui, doc, el, () => gui.Checkbox("Italic", ref italic))) s.Italic = italic;

        // text-shadow
        bool ts = s.HasTextShadow;
        if (Edit(gui, doc, el, () => gui.Checkbox("Text Shadow", ref ts))) s.HasTextShadow = ts;
        if (s.HasTextShadow)
        {
            FloatField(gui, doc, el, "  TS Offset X", s.TextShadowOffsetX, v => s.TextShadowOffsetX = v, -50, 50);
            FloatField(gui, doc, el, "  TS Offset Y", s.TextShadowOffsetY, v => s.TextShadowOffsetY = v, -50, 50);
            FloatField(gui, doc, el, "  TS Blur", s.TextShadowBlur, v => s.TextShadowBlur = v, 0, 50);
            ColorField(gui, doc, el, "  TS Color", s.TextShadowColor, c => s.TextShadowColor = c);
        }
    }

    void DrawImage(IEditorGui gui, UIBuilderDocument doc, Image img)
    {
        string src = img.Texture as string ?? "";
        gui.SetNextItemWidth(-1);
        if (Edit(gui, doc, img, () => gui.InputTextWithHint("Source", "Assets/...png", ref src, 256), reconcile: false))
            img.Texture = string.IsNullOrWhiteSpace(src) ? null : src.Trim();
        // accept an asset drag-drop onto the field
        if (gui.BeginDragDropTarget())
        {
            string dropped = gui.AcceptDragDropPayloadString("ASSET");
            if (dropped != null) { doc.PushUndo(); img.Texture = dropped; doc.MarkDirty(); }
            gui.EndDragDropTarget();
        }
        EnumCombo(gui, doc, img, "Scale Mode", img.ScaleMode, v => img.ScaleMode = v, reconcile: false);
    }

    // ---- widget helpers ------------------------------------------------------

    void LengthField(IEditorGui gui, UIBuilderDocument doc, VisualElement el, string label, Length cur, Action<Length> apply)
    {
        float val = cur.Unit == Length.Kind.Auto ? 0 : cur.Value;
        int unit = cur.Unit switch { Length.Kind.Points => 0, Length.Kind.Percent => 1, _ => 2 };
        gui.PushId(label);
        gui.SetNextItemWidth(gui.ContentRegionAvail.X * 0.5f);
        gui.BeginDisabled(unit == 2);
        bool cv = Edit(gui, doc, el, () => gui.DragFloat("##v", ref val, 0.5f, -9999, 9999));
        gui.EndDisabled();
        gui.SameLine();
        gui.SetNextItemWidth(-1);
        bool cu = Edit(gui, doc, el, () => gui.Combo(label, ref unit, _unitNames));
        gui.PopId();
        if (cv || cu) apply(unit switch { 0 => Length.Points(val), 1 => Length.Percent(val), _ => Length.Auto });
    }
    static readonly string[] _unitNames = { "px", "%", "auto" };

    void FloatField(IEditorGui gui, UIBuilderDocument doc, VisualElement el, string label, float cur, Action<float> apply, float min = 0, float max = 0, float speed = 0.5f)
    {
        float v = cur;
        if (Edit(gui, doc, el, () => gui.DragFloat(label, ref v, speed, min, max))) apply(v);
    }

    // A float field where NaN means "unset" (max-width, aspect-ratio). Shows 0 for NaN; a checkbox toggles set.
    void NanFloatField(IEditorGui gui, UIBuilderDocument doc, VisualElement el, string label, float cur, Action<float> apply)
    {
        bool set = !float.IsNaN(cur);
        gui.PushId(label);
        bool ck = Edit(gui, doc, el, () => gui.Checkbox("##set", ref set));
        gui.SameLine();
        float v = set ? cur : 0;
        gui.BeginDisabled(!set);
        gui.SetNextItemWidth(-1);
        bool cv = Edit(gui, doc, el, () => gui.DragFloat(label, ref v, 0.5f, 0, 9999));
        gui.EndDisabled();
        gui.PopId();
        if (ck || cv) apply(set ? v : float.NaN);
    }

    // An inset field (NaN = unset).
    void InsetField(IEditorGui gui, UIBuilderDocument doc, VisualElement el, string label, Edge edge, float cur, Action<float> apply)
    {
        float v = float.IsNaN(cur) ? 0 : cur;
        if (Edit(gui, doc, el, () => gui.DragFloat(label, ref v, 0.5f, -9999, 9999))) apply(v);
    }

    void Corner(IEditorGui gui, UIBuilderDocument doc, VisualElement el, string label, float cur, Action<float> apply)
    {
        float v = cur;
        if (Edit(gui, doc, el, () => gui.DragFloat(label, ref v, 0.5f, 0, 999))) apply(v);
    }

    // A 4-field per-edge composite (L T R B) for margin/padding.
    void EdgeField(IEditorGui gui, UIBuilderDocument doc, VisualElement el, string id, Func<Edge, float> get, Action<Edge, float> set)
    {
        gui.PushId(id);
        float w = gui.ContentRegionAvail.X / 4f - gui.ItemSpacing.X;
        Edge[] edges = { Edge.Left, Edge.Top, Edge.Right, Edge.Bottom };
        string[] lbl = { "L", "T", "R", "B" };
        for (int i = 0; i < 4; i++)
        {
            if (i > 0) gui.SameLine();
            float v = get(edges[i]); if (float.IsNaN(v)) v = 0;
            gui.SetNextItemWidth(w);
            int idx = i; Edge e = edges[i];
            Edit(gui, doc, el, () => gui.DragFloat(lbl[idx], ref v, 0.5f, 0, 999));
            if (gui.IsItemDeactivatedAfterEdit() || gui.IsItemActive()) set(e, v);
        }
        gui.PopId();
    }

    void ColorField(IEditorGui gui, UIBuilderDocument doc, VisualElement el, string label, Color cur, Action<Color> apply)
    {
        Vector4 v = new(cur.R, cur.G, cur.B, cur.A);
        if (Edit(gui, doc, el, () => gui.ColorEdit4(label, ref v))) apply(new Color(v.X, v.Y, v.Z, v.W));
    }

    void EnumCombo<TEnum>(IEditorGui gui, UIBuilderDocument doc, VisualElement el, string label, TEnum cur, Action<TEnum> apply, bool reconcile = true)
        where TEnum : struct, Enum
    {
        if (EnumComboValue(gui, doc, el, label, cur, out var nv, reconcile)) apply(nv);
    }

    bool EnumComboValue<TEnum>(IEditorGui gui, UIBuilderDocument doc, VisualElement el, string label, TEnum cur, out TEnum result, bool reconcile = true)
        where TEnum : struct, Enum
    {
        var names = Enum.GetNames<TEnum>();
        var values = Enum.GetValues<TEnum>();
        int idx = Array.IndexOf(values, cur); if (idx < 0) idx = 0;
        result = cur;
        if (Edit(gui, doc, el, () => gui.Combo(label, ref idx, names), reconcile)) { result = values[idx]; return true; }
        return false;
    }

    // Wrap a widget so a real change marks dirty, pushes ONE undo step per gesture, and reconciles the
    // element's inline-override store (so class values aren't frozen into inline). `reconcile` is false for
    // identity edits (name/text/src) that aren't style.
    bool Edit(IEditorGui gui, UIBuilderDocument doc, VisualElement el, Func<bool> widget, bool reconcile = true)
    {
        bool changed = widget();
        if (gui.IsItemActivated() && !_gesturePushed) { doc.PushUndo(); _gesturePushed = true; }
        if (gui.IsItemDeactivatedAfterEdit()) { _gesturePushed = false; if (reconcile && el != null) doc.SyncInline(el); }
        if (changed) { doc.MarkDirty(); if (reconcile && el != null) doc.SyncInline(el); }
        return changed;
    }
}
