namespace BallisticEngine.UI;

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

    public static implicit operator Length(float points) => Points(points);

    public override string ToString() => Unit switch
    {
        Kind.Auto => "auto",
        Kind.Percent => $"{Value}%",
        _ => $"{Value}px",
    };
}
