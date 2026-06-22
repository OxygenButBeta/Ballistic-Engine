namespace BallisticEngine;

public enum RenderBackend {
    OpenGL,
    Dx12,
}

public static class RenderBackendSelector {
    public static RenderBackend Selected {
        get {
            string s = System.Environment.GetEnvironmentVariable("BALLISTIC_BACKEND")?.Trim().ToLowerInvariant();
            return s switch {
                "gl" or "opengl" or "ogl" => RenderBackend.OpenGL,
                _ => RenderBackend.Dx12,
            };
        }
    }
}
