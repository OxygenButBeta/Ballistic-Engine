namespace BallisticEngine;

public static class GizmoDepthOcclusion {
    public static bool Enabled;
    public static float[] Grid;
    public static int Width, Height;

    public static float SampleWindowDepth(float u, float v) {
        var grid = Grid;
        if (grid is null || Width <= 0 || Height <= 0)
            return 1f;
        int x = (int)(System.Math.Clamp(u, 0f, 0.99999f) * Width);
        int y = (int)(System.Math.Clamp(1f - v, 0f, 0.99999f) * Height);
        int i = y * Width + x;
        return i >= 0 && i < grid.Length ? grid[i] : 1f;
    }
}
