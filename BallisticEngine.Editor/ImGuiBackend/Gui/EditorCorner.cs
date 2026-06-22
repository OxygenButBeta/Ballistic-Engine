namespace BallisticEngine.Editor;

[System.Flags]
public enum EditorCorner {
    None = 0, TopLeft = 1, TopRight = 2, BottomLeft = 4, BottomRight = 8,
    Left = TopLeft | BottomLeft, Right = TopRight | BottomRight, All = Left | Right,
}
