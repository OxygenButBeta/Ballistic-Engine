using OpenTK.Mathematics;

namespace BallisticEngine.UI;

// A pixel rectangle in panel space (top-left origin, +Y down — UI convention, matching how the
// layout solver and the eventual screen-space UI pass address the panel). X/Y is the top-left
// corner; Width/Height the size. This is what a VisualElement's resolved box is reported as.
public readonly struct Rect
{
    public readonly float X, Y, Width, Height;

    public Rect(float x, float y, float width, float height)
    {
        X = x; Y = y; Width = width; Height = height;
    }

    public float Left => X;
    public float Top => Y;
    public float Right => X + Width;
    public float Bottom => Y + Height;
    public Vector2 Position => new(X, Y);
    public Vector2 Size => new(Width, Height);
    public Vector2 Center => new(X + Width * 0.5f, Y + Height * 0.5f);

    // Pointer hit-test (inclusive of the left/top edge, exclusive of right/bottom — standard
    // half-open rect so adjacent elements don't both claim a shared boundary pixel).
    public bool Contains(Vector2 p) => p.X >= X && p.X < Right && p.Y >= Y && p.Y < Bottom;

    public override string ToString() => $"Rect({X}, {Y}, {Width}x{Height})";
}
