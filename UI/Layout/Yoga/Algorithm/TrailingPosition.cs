namespace Facebook.Yoga
{
    public static class TrailingPosition
    {
        public static float GetPositionOfOppositeEdge(
            float position,
            FlexDirection axis,
            Node containingNode,
            Node node)
        {
            return containingNode.Layout.MeasuredDimension(axis.Dimension()) -
                node.Layout.MeasuredDimension(axis.Dimension()) - position;
        }

        public static void SetChildTrailingPosition(
            Node node,
            Node child,
            FlexDirection axis)
        {
            child.SetLayoutPosition(
                GetPositionOfOppositeEdge(
                    child.Layout.Position(axis.FlexStartEdge()),
                    axis,
                    node,
                    child),
                axis.FlexEndEdge());
        }

        public static bool NeedsTrailingPosition(FlexDirection axis)
        {
            return axis == FlexDirection.RowReverse ||
                axis == FlexDirection.ColumnReverse;
        }
    }
}

