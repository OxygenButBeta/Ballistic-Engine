namespace BallisticEngine;

// Bridge for editor gizmo DEPTH OCCLUSION: the renderer publishes a coarse Scene-view depth grid
// (window-depth [0,1]) here each frame; the editor's gizmo drawer samples it so a gizmo point BEHIND
// scene geometry draws dimmer — giving depth cues (otherwise a gizmo behind a wall looks in front).
// Plain statics so the OpenGL renderer and the editor share it without a reference either way.
//
// Editor-only feature; Enabled defaults off and the editor turns it on (the player never reads gizmos).
public static class GizmoDepthOcclusion {
    public static bool Enabled;
    public static float[] Grid;     // window-depth [0,1], row-major, Width x Height (GL bottom-up)
    public static int Width, Height;

    // Sample the depth grid at a viewport-relative UV (0..1, top-left origin like screen pixels).
    // Returns the stored window-depth, or 1 (far) when the grid isn't ready. The grid is GL bottom-up,
    // so V is flipped here.
    public static float SampleWindowDepth(float u, float v) {
        var grid = Grid;
        if (grid is null || Width <= 0 || Height <= 0)
            return 1f;
        int x = (int)(System.Math.Clamp(u, 0f, 0.99999f) * Width);
        int y = (int)(System.Math.Clamp(1f - v, 0f, 0.99999f) * Height); // flip: GL depth is bottom-up
        int i = y * Width + x;
        return i >= 0 && i < grid.Length ? grid[i] : 1f;
    }
}
