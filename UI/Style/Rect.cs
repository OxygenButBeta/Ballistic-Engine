
namespace BallisticEngine.UI;

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

    public bool Contains(Vector2 p) => p.X >= X && p.X < Right && p.Y >= Y && p.Y < Bottom;

    public override string ToString() => $"Rect({X}, {Y}, {Width}x{Height})";
}
