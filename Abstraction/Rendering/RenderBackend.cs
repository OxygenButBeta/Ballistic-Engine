namespace BallisticEngine;

// Which graphics backend the host should bring up. Selected by BALLISTIC_BACKEND (mirrors the
// existing BALLISTIC_GPUDRIVEN / BALLISTIC_SDFGI env-flag pattern). DX12 is now the DEFAULT (the OpenGL
// path is being deleted in ENDGAME 3 — DX12Migration.md); GL is reachable via BALLISTIC_BACKEND=gl only
// while the GL code still exists, for A/B verification during the migration.
public enum RenderBackend {
    OpenGL,
    Dx12,
}

public static class RenderBackendSelector {
    // Reads BALLISTIC_BACKEND ("gl" | "opengl" | "dx12" | "directx12"); defaults to Dx12. This is the
    // single seam where the host picks a backend. Once GL is fully deleted this collapses to Dx12 always.
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
