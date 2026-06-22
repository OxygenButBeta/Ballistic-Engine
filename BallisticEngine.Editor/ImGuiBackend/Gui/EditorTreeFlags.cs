namespace BallisticEngine.Editor;

[System.Flags]
public enum EditorTreeFlags {
    None = 0,
    DefaultOpen = 1 << 0,
    Framed = 1 << 1,
    SpanAvailWidth = 1 << 2,
    OpenOnArrow = 1 << 3,
    AllowOverlap = 1 << 4,
    Selected = 1 << 5,
    Leaf = 1 << 6,
    NoTreePushOnOpen = 1 << 7,
}
