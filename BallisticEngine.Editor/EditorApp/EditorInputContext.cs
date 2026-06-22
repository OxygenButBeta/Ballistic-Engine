using Keys = OpenTK.Windowing.GraphicsLibraryFramework.Keys;

namespace BallisticEngine.Editor;

[Flags]
internal enum EditorInputContext {
    None            = 0,
    Global          = 1 << 0,
    SceneView       = 1 << 1,
    SceneViewHovered = 1 << 2,
    GameView        = 1 << 3,
}
