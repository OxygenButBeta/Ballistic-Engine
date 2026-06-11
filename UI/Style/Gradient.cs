using System.Collections.Generic;

namespace BallisticEngine.UI;

// A CSS-style gradient fill for an element background. Supports linear and radial gradients with up to
// N color stops — the two kinds the Black Hollow design (and most game UIs) lean on for dividers,
// slider fills, scrims, and vignettes. The renderer evaluates the stops per-fragment inside the
// element's (optionally rounded) box.
public sealed class Gradient
{
    public enum Kind { Linear, Radial }

    public struct Stop
    {
        public Color Color;
        public float Position; // 0..1 along the gradient axis
        public Stop(Color color, float position) { Color = color; Position = position; }
    }

    public Kind Type;

    // Linear: direction angle in DEGREES, CSS convention — 0deg = to top, 90deg = to right, 180 = down.
    // (CSS `linear-gradient(90deg, ...)` runs left→right.) Ignored for radial.
    public float AngleDegrees;

    // Radial: center as a 0..1 fraction of the box, and the radii as fractions of the box half-extent.
    // Defaults center the ellipse and fit it to the box.
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
