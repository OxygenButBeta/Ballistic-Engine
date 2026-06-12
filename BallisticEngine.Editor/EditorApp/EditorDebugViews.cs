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
    public const int Luminance = 3;    // perceived brightness of the lit colour

    public static readonly (int mode, string label)[] Modes = [
        (AmbientOcclusion, "Ambient Occlusion"),
        (Lit, "Lit (no post)"),
        (Luminance, "Luminance"),
    ];

    static int program;
    static int quadVao;
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
            Lit or Luminance => f.LitColor,
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

        GL.BindVertexArray(quadVao);
        GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
        GL.BindVertexArray(0);
        return true;
    }

    static void EnsureResources() {
        if (program == 0) {
            const string vert = @"#version 330 core
out vec2 uv;
void main() {
    // Fullscreen triangle: 3 verts covering the screen, uv in [0,1].
    vec2 p = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2);
    uv = p;
    gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
}";
            const string frag = @"#version 330 core
in vec2 uv;
out vec4 col;
uniform sampler2D src;
uniform int mode;
void main() {
    vec3 c = texture(src, uv).rgb;
    if (mode == 1) {            // Ambient Occlusion — single channel, show as greyscale
        col = vec4(vec3(c.r), 1.0);
    } else if (mode == 3) {     // Luminance of the lit colour
        float l = dot(c, vec3(0.2126, 0.7152, 0.0722));
        col = vec4(vec3(l), 1.0);
    } else {                    // Lit (no post) — tonemap lightly so HDR doesn't blow out the view
        vec3 t = c / (c + vec3(1.0));
        col = vec4(pow(t, vec3(1.0/2.2)), 1.0);
    }
}";
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
            GL.DeleteShader(v);
            GL.DeleteShader(fr);
        }
        if (quadVao == 0)
            quadVao = GL.GenVertexArray();   // attribute-less; the vertex shader builds the triangle
    }

    static void CheckCompile(int shader, string label) {
        GL.GetShader(shader, ShaderParameter.CompileStatus, out int ok);
        if (ok == 0)
            Debugging.LogError($"Editor debug-view {label} shader compile failed: {GL.GetShaderInfoLog(shader)}");
    }
}
