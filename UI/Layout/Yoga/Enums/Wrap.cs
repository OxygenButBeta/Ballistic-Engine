namespace Facebook.Yoga;

public enum Wrap : byte
{
    NoWrap = 0,
    Wrap = 1,
    WrapReverse = 2,
}

public static class WrapExtensions
{
    public const int OrdinalCount = 3;

    public static string ToDebugString(this Wrap e)
    {
        return e switch
        {
            Wrap.NoWrap => "NoWrap",
            Wrap.Wrap => "Wrap",
            Wrap.WrapReverse => "WrapReverse",
            _ => throw new ArgumentOutOfRangeException(nameof(e), e, "Invalid Wrap value")
        };
    }
}

