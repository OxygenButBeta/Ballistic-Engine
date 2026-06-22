using System.Runtime.CompilerServices;

namespace Facebook.Yoga
{
    /// <summary>
    /// Public API constants and helper for YGValue.
    /// The YGValue struct itself is defined in StyleLength.cs.
    /// </summary>
    public static class YogaValue
    {
        public static readonly YGValue YGValueZero = new YGValue(0, Unit.Point);
        public static readonly YGValue YGValueUndefined = new YGValue(YogaConstants.Undefined, Unit.Undefined);
        public static readonly YGValue YGValueAuto = new YGValue(YogaConstants.Undefined, Unit.Auto);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool YGFloatIsUndefined(float value)
        {
            return float.IsNaN(value);
        }
    }
}
