
namespace BallisticEngine.UI;

public interface IUIRenderer
{
    void Begin(Vector2 canvasSize, float scale);
    void End();

    void DrawShadow(Rect rect, Vector4 radius, float ox, float oy, float blur, float spread, Color color);

    void DrawBackdropBlur(Rect rect, Vector4 radius, float radiusPx);

    void DrawRect(Rect rect, Color fill, Vector4 radius, float borderWidth, Color borderColor);

    void DrawGradient(Rect rect, Gradient gradient, Vector4 radius, float opacity);

    void DrawText(Rect rect, string text, in TextStyle style);

    void DrawImage(Rect rect, object texture, Color tint, ScaleMode scaleMode);

    void PushClip(Rect rect);
    void PopClip();
}
