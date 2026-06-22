namespace BallisticEngine.UI;

public sealed class Gradient
{
    public enum Kind { Linear, Radial }

    public struct Stop
    {
        public Color Color;
        public float Position;
        public Stop(Color color, float position) { Color = color; Position = position; }
    }

    public Kind Type;

    public float AngleDegrees;

    public float CenterX = 0.5f, CenterY = 0.5f;
    public float RadiusX = 0.5f, RadiusY = 0.5f;

    public readonly List<Stop> Stops = new();

    public static Gradient Linear(float angleDeg, params Stop[] stops)
    {
        var g = new Gradient { Type = Kind.Linear, AngleDegrees = angleDeg };
        g.Stops.AddRange(stops);
        return g;
    }

    public static Gradient Radial(params Stop[] stops)
    {
        var g = new Gradient { Type = Kind.Radial };
        g.Stops.AddRange(stops);
        return g;
    }
}
