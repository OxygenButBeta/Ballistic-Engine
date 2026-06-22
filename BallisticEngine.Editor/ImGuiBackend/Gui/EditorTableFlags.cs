namespace BallisticEngine.Editor;

[System.Flags]
public enum EditorTableFlags {
    None = 0,
    RowBg = 1 << 0,
    BordersInnerV = 1 << 1,
    BordersOuter = 1 << 2,
    ScrollY = 1 << 3,
    SizingStretchProp = 1 << 4,
    Resizable = 1 << 5,
    PadOuterX = 1 << 6,
    SizingFixedFit = 1 << 7,
    Sortable = 1 << 8,
}
