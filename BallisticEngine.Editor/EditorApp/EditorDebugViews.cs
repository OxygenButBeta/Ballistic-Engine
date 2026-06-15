namespace BallisticEngine.Editor;

// EDITOR-ONLY extra renderer debug views (AO / lit-no-tonemap / GI). This file lives in the editor project
// so it NEVER ships in a player build. The GL fullscreen-quad compositor was deleted with the GL renderer;
// the mode catalog + the shading-mode dropdown plumbing (HDRenderer.EditorExtraDebugMode) stay so the UI is
// intact, and the engine-side built-in views (Shaded/Wireframe/Normals/Depth) still work on DX12.
//
// TODO(dx12): port the extra-view compositor to DX12 (sample the requested DebugFrame buffer into the
// destination via an HLSL fullscreen pass) and re-wire HDRenderer.EditorDebugComposite in Install().
internal static class EditorDebugViews {
    // Extra mode indices — kept in sync with the dropdown. 0 means "no extra view" (engine path).
    public const int None = 0;
    public const int AmbientOcclusion = 1;
    public const int Lit = 2;          // the HDR lit colour with no tonemap/bloom/grade
    public const int Ssgi = 4;         // the isolated indirect light (denoised GI, pre-combine)

    public static readonly (int mode, string label)[] Modes = [
        (AmbientOcclusion, "Ambient Occlusion"),
        (Ssgi, "Global Illumination (SSGI)"),
        (Lit, "Lit (no post)"),
    ];

    // No-op on DX12 (the extra-view compositor is not yet ported — see TODO above). Kept so the editor's
    // one-time wiring call site is unchanged; the engine simply never invokes EditorDebugComposite.
    public static void Install() { }
}
