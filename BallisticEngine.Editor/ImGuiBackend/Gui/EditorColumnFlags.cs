namespace BallisticEngine.Editor;

[System.Flags]
public enum EditorColumnFlags {
    None = 0,
    WidthStretch = 1 << 0,
    WidthFixed = 1 << 1,
    DefaultSort = 1 << 2,
}
