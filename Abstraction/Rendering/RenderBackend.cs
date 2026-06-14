namespace BallisticEngine;

// Which graphics backend the host should bring up. Selected by BALLISTIC_BACKEND (mirrors the
// existing BALLISTIC_GPUDRIVEN / BALLISTIC_SDFGI env-flag pattern). GL is the default and only
// implemented backend today; the DX12 migration adds Dx12 as a second host runtime (DX12Migration.md).
public enum RenderBackend {
    OpenGL,
    Dx12,
}

public static class RenderBackendSelector {
    // Reads BALLISTIC_BACKEND ("gl" | "opengl" | "dx12" | "directx12"); defaults to OpenGL. This is the
    // single seam where the host picks a backend — the DX12 host runtime plugs in here when it exists.
    public static RenderBackend Selected {
        get {
            string s = System.Environment.GetEnvironmentVariable("BALLISTIC_BACKEND")?.Trim().ToLowerInvariant();
            return s switch {
                "dx12" or "directx12" or "d3d12" => RenderBackend.Dx12,
                _ => RenderBackend.OpenGL,
            };
        }
    }
}
