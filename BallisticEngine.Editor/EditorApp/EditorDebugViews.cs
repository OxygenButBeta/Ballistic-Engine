using OpenTK.Graphics.OpenGL4;

namespace BallisticEngine.Editor;

// EDITOR-ONLY extra renderer debug views (AO / lit-no-tonemap / luminance / ...). This whole file lives
// in the editor project, so it NEVER ships in a player build — the requirement was "the debug menu
// must not reach the build" while SSAO/SSGI/etc. themselves stay in the engine. The engine exposes the
// frame's buffers through HDRenderer.DebugFrame and calls HDRenderer.EditorDebugComposite (wired here);
// we draw a fullscreen quad that samples the requested buffer into the bound destination.
//
// The built-in Shaded/Wireframe/Normals/Depth modes still live in the engine (they need the geometry
// pass / G-buffer composite); these are the EXTRA modes that only need an already-rendered buffer.
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

    static int program;
    static bool installed;

    // Wire the engine delegate ONCE. Safe to call every frame from the editor; no-op after the first.
    public static void Install() {
        if (installed) return;
        installed = true;
        HDRenderer.EditorDebugComposite = Draw;
    }

    // Called by the renderer on the editor's render thread with the destination already bound. Returns
    // true (we always handle it when invoked, since the editor only invokes us for a known extra mode).
    static bool Draw(HDRenderer.DebugFrame f) {
        EnsureResources();

        GL.Viewport(0, 0, f.DestWidth, f.DestHeight);
        GL.UseProgram(program);

        // Source buffer + how to interpret it depends on the mode.
        int src = f.Mode switch {
            AmbientOcclusion => f.AoTexture,
            Ssgi => f.SsgiTexture,
            Lit => f.LitColor,
            _ => f.LitColor,
        };
        if (src == 0 || src == -1) {
            // The buffer wasn't produced this frame (e.g. AO when SSAO is off) — clear to mid-grey so
            // it's obvious rather than sampling a stale/garbage texture.
            GL.ClearColor(0.2f, 0.2f, 0.22f, 1f);
            GL.Clear(ClearBufferMask.ColorBufferBit);
            return true;
        }

        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, src);
        GL.Uniform1(GL.GetUniformLocation(program, "src"), 0);
        GL.Uniform1(GL.GetUniformLocation(program, "mode"), f.Mode);

        // Use the engine's shared fullscreen-quad VAO (real VBO, attribs 0=pos 1=uv) that EVERY post
        // pass draws with — an attribute-less gl_VertexID triangle drew NOTHING here (left the previous
        // frame frozen), so reuse the proven path instead.
        GLBufferUtilities.DrawFullscreenQuad();
        return true;
    }

    static void EnsureResources() {
        if (program == 0) {
            // Matches the engine's shared fullscreen quad: attrib 0 = clip-space position, 1 = uv.
            string vert = EmbeddedShaderSource.Read("EditorDebugView_Vert.glsl");
            string frag = EmbeddedShaderSource.Read("EditorDebugView_Frag.glsl");
            int v = GL.CreateShader(ShaderType.VertexShader);
            GL.ShaderSource(v, vert); GL.CompileShader(v);
            CheckCompile(v, "debug-view vertex");
            int fr = GL.CreateShader(ShaderType.FragmentShader);
            GL.ShaderSource(fr, frag); GL.CompileShader(fr);
            CheckCompile(fr, "debug-view fragment");
            program = GL.CreateProgram();
            GL.AttachShader(program, v);
            GL.AttachShader(program, fr);
            GL.LinkProgram(program);
            GL.GetProgram(program, GetProgramParameterName.LinkStatus, out int linked);
            if (linked == 0)
                Debugging.LogError($"Editor debug-view shader link failed: {GL.GetProgramInfoLog(program)}");
            GL.DeleteShader(v);
            GL.DeleteShader(fr);
        }
    }

    static void CheckCompile(int shader, string label) {
        GL.GetShader(shader, ShaderParameter.CompileStatus, out int ok);
        if (ok == 0)
            Debugging.LogError($"Editor debug-view {label} shader compile failed: {GL.GetShaderInfoLog(shader)}");
    }
}
