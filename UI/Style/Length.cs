namespace BallisticEngine.UI;

// A CSS length: either a pixel value (10px), a percentage (50%), or "auto". Lets the style API and
// the USS parser carry the distinction the way CSS does — "width: 50%" vs "width: 200px" vs
// "width: auto" — instead of collapsing everything to floats and losing percent/auto semantics.
public readonly struct Length
{
    public enum Kind { Auto, Points, Percent }

    public readonly Kind Unit;
    public readonly float Value;

    Length(Kind unit, float value) { Unit = unit; Value = value; }

    public static readonly Length Auto = new(Kind.Auto, 0f);
    public static Length Points(float v) => new(Kind.Points, v);
    public static Length Percent(float v) => new(Kind.Percent, v);

    public bool IsAuto => Unit == Kind.Auto;

    // Implicit from float = pixels, so call sites can write `Style.Width = 200` for the common case.
    public static implicit operator Length(float points) => Points(points);

    public override string ToString() => Unit switch
    {
        Kind.Auto => "auto",
        Kind.Percent => $"{Value}%",
        _ => $"{Value}px",
    };
}
