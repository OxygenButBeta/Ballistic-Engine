namespace Facebook.Yoga
{
    internal static class AlignHelper
    {
        public static Align ResolveChildAlignment(Node node, Node child)
    {
        Align align = child.Style.AlignSelf == Align.Auto
            ? node.Style.AlignItems
            : child.Style.AlignSelf;

        if (node.Style.Display == Display.Flex && align == Align.Baseline &&
            node.Style.FlexDirection.IsColumn())
        {
            return Align.FlexStart;
        }

        return align;
    }

    public static Justify ResolveChildJustification(Node node, Node child)
    {
        return child.Style.JustifySelf == Justify.Auto
            ? node.Style.JustifyItems
            : child.Style.JustifySelf;
    }

    /// <summary>
    /// Fallback alignment to use on overflow
    /// https://www.w3.org/TR/css-align-3/#distribution-values
    /// </summary>
    public static Align FallbackAlignment(Align align)
    {
        switch (align)
        {
            case Align.SpaceBetween:
            case Align.Stretch:
                return Align.FlexStart;

            case Align.SpaceAround:
            case Align.SpaceEvenly:
                return Align.FlexStart;
            default:
                return align;
        }
    }

    /// <summary>
    /// Fallback alignment to use on overflow
    /// https://www.w3.org/TR/css-align-3/#distribution-values
    /// </summary>
    public static Justify FallbackAlignment(Justify align)
    {
        switch (align)
        {
            case Justify.SpaceBetween:
                return Justify.FlexStart;

            case Justify.SpaceAround:
            case Justify.SpaceEvenly:
                return Justify.FlexStart;
            default:
                return align;
        }
    }
}
}

