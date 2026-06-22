namespace Facebook.Yoga
{
    /// <summary>
    /// Public C-style API wrapper for pixel grid rounding.
    /// Delegates to the internal PixelGrid class.
    /// </summary>
    public static class YGPixelGridAPI
    {
        public static float YGRoundValueToPixelGrid(
            double value,
            double pointScaleFactor,
            bool forceCeil,
            bool forceFloor)
        {
            return PixelGrid.RoundValueToPixelGrid(
                value, pointScaleFactor, forceCeil, forceFloor);
        }
    }
}
