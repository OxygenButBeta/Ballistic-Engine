using System.Collections.Generic;

namespace BallisticEngine.UI;

// A headless IUIRenderer that records every primitive instead of drawing. It exists so the render
// WALK (draw order, opacity inheritance, radius clamping, clipping, text/image emission) can be
// asserted without a GPU — the AI-first verification path. Also handy as a debugging dump of exactly
// what a UI would draw. Not used in the real engine render loop.
public sealed class RecordingUIRenderer : IUIRenderer
{
    public enum Op { Begin, End, Rect, Gradient, Text, Image, PushClip, PopClip }

    public readonly struct Command
    {
        public readonly Op Op;
        public readonly Rect Rect;
        public readonly Color Color;
        public readonly Vector4 Radius;
        public readonly float Scalar;     // borderWidth (Rect) / fontSize (Text) / scale (Begin)
        public readonly string Text;
        public readonly object Texture;

        public Command(Op op, Rect rect = default, Color color = default, Vector4 radius = default,
                       float scalar = 0f, string text = null, object texture = null)
        {
            Op = op; Rect = rect; Color = color; Radius = radius; Scalar = scalar; Text = text; Texture = texture;
        }
    }

    public readonly List<Command> Commands = new();

    // Convenience counters for tests.
    public int CountOf(Op op)
    {
        int n = 0;
        foreach (var c in Commands) if (c.Op == op) n++;
        return n;
    }

    public void Begin(Vector2 canvasSize, float scale) =>
        Commands.Add(new Command(Op.Begin, new Rect(0, 0, canvasSize.X, canvasSize.Y), scalar: scale));

    public void End() => Commands.Add(new Command(Op.End));

    public void DrawRect(Rect rect, Color fill, Vector4 radius, float borderWidth, Color borderColor) =>
        Commands.Add(new Command(Op.Rect, rect, fill, radius, borderWidth));

    public void DrawGradient(Rect rect, Gradient gradient, Vector4 radius, float opacity) =>
        Commands.Add(new Command(Op.Gradient, rect, default, radius, opacity));

    public void DrawText(Rect rect, string text, in TextStyle style) =>
        Commands.Add(new Command(Op.Text, rect, style.Color, scalar: style.FontSize, text: text));

    public void DrawImage(Rect rect, object texture, Color tint, ScaleMode scaleMode) =>
        Commands.Add(new Command(Op.Image, rect, tint, texture: texture));

    public void PushClip(Rect rect) => Commands.Add(new Command(Op.PushClip, rect));
    public void PopClip() => Commands.Add(new Command(Op.PopClip));
}
