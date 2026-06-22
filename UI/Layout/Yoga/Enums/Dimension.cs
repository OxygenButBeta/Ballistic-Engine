namespace Facebook.Yoga
{
    public enum Dimension : byte
    {
        Width = 0,
        Height = 1,
    }

    public static class DimensionExtensions
    {
        public const int OrdinalCount = 2;

        public static string ToString(Dimension dimension)
        {
            return dimension switch
            {
                Dimension.Width => "width",
                Dimension.Height => "height",
                _ => "unknown"
            };
        }
    }
}

