namespace BallisticEngine.Editor;

public interface IEditorDrawList {
    void AddLine(Vector2 a, Vector2 b, uint col, float thickness = 1f);
    void AddRect(Vector2 min, Vector2 max, uint col, float rounding = 0f, float thickness = 1f);
    void AddRectFilled(Vector2 min, Vector2 max, uint col, float rounding = 0f);
    void AddRectFilled(Vector2 min, Vector2 max, uint col, float rounding, EditorCorner corners);
    void AddCircle(Vector2 center, float radius, uint col, int segments = 0, float thickness = 1f);
    void AddCircleFilled(Vector2 center, float radius, uint col);
    void AddText(Vector2 pos, uint col, string text);
    void AddText(EditorFont font, float size, Vector2 pos, uint col, string text);
    void AddBezierCubic(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, uint col, float thickness);

    void ChannelsSplit(int count);
    void ChannelsSetCurrent(int channel);
    void ChannelsMerge();
    void PushClipRect(Vector2 min, Vector2 max, bool intersectWithCurrent);
    void PopClipRect();
}
