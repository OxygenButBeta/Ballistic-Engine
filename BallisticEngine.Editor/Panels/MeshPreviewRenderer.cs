using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace BallisticEngine.Editor;

// Editor-local mesh thumbnail rendering: a tiny self-contained GL pass (own FBO + shader)
// that draws a mesh artifact's geometry with simple lambert shading from a 3/4 view.
// Deliberately independent of the engine renderer so it can never collide with it.
internal static class MeshPreviewRenderer {
    static int program;

    public static byte[] Render(in MeshData data, int size) {
        EnsureProgram();

        // GL state we touch and must restore (called mid-UI-frame on the shared context).
        int prevFbo = GL.GetInteger(GetPName.FramebufferBinding);
        int prevVao = GL.GetInteger(GetPName.VertexArrayBinding);
        int prevProgram = GL.GetInteger(GetPName.CurrentProgram);
        var prevViewport = new int[4];
        GL.GetInteger(GetPName.Viewport, prevViewport);
        bool prevDepth = GL.IsEnabled(EnableCap.DepthTest);

        // Target FBO.
        int fbo = GL.GenFramebuffer();
        int color = GL.GenTexture();
        int depth = GL.GenRenderbuffer();
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, color);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, size, size, 0,
            PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, color, 0);
        GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, depth);
        GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.DepthComponent24, size, size);
        GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
            RenderbufferTarget.Renderbuffer, depth);

        // Geometry.
        int vao = GL.GenVertexArray();
        int vbo = GL.GenBuffer();
        int nbo = GL.GenBuffer();
        int ebo = GL.GenBuffer();
        GL.BindVertexArray(vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, data.Vertices.Length * 12, data.Vertices, BufferUsageHint.StaticDraw);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 12, 0);
        GL.BindBuffer(BufferTarget.ArrayBuffer, nbo);
        GL.BufferData(BufferTarget.ArrayBuffer, data.Normals.Length * 12, data.Normals, BufferUsageHint.StaticDraw);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 12, 0);
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, ebo);
        GL.BufferData(BufferTarget.ElementArrayBuffer, data.Indices.Length * 4, data.Indices,
            BufferUsageHint.StaticDraw);

        // Frame the bounds from a 3/4 view.
        (Vector3 center, float radius) = Bounds(data.Vertices);
        Vector3 eye = center + new Vector3(1f, 0.65f, 1.3f).Normalized() * radius * 2.1f;
        Matrix4 view = Matrix4.LookAt(eye, center, Vector3.UnitY);
        Matrix4 proj = Matrix4.CreatePerspectiveFieldOfView(
            MathHelper.DegreesToRadians(40f), 1f, Math.Max(0.01f, radius * 0.1f), radius * 6f);
        Matrix4 mvp = view * proj;

        GL.Viewport(0, 0, size, size);
        GL.Enable(EnableCap.DepthTest);
        GL.ClearColor(0.16f, 0.16f, 0.17f, 1f);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        GL.UseProgram(program);
        GL.UniformMatrix4(GL.GetUniformLocation(program, "mvp"), false, ref mvp);
        GL.DrawElements(PrimitiveType.Triangles, data.Indices.Length, DrawElementsType.UnsignedInt, 0);

        // Read back (bottom-up) and flip for ImGui.
        var pixels = new byte[size * size * 4];
        GL.ReadPixels(0, 0, size, size, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
        FlipRows(pixels, size);

        // Cleanup + restore.
        GL.DeleteBuffer(vbo);
        GL.DeleteBuffer(nbo);
        GL.DeleteBuffer(ebo);
        GL.DeleteVertexArray(vao);
        GL.DeleteTexture(color);
        GL.DeleteRenderbuffer(depth);
        GL.DeleteFramebuffer(fbo);

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, prevFbo);
        GL.BindVertexArray(prevVao);
        GL.UseProgram(prevProgram);
        GL.Viewport(prevViewport[0], prevViewport[1], prevViewport[2], prevViewport[3]);
        if (!prevDepth)
            GL.Disable(EnableCap.DepthTest);

        return pixels;
    }

    static void EnsureProgram() {
        if (program != 0)
            return;

        const string vert = @"#version 330 core
layout(location = 0) in vec3 pos;
layout(location = 1) in vec3 normal;
uniform mat4 mvp;
out vec3 n;
void main() { gl_Position = mvp * vec4(pos, 1.0); n = normal; }";
        const string frag = @"#version 330 core
in vec3 n;
out vec4 color;
void main() {
    vec3 l = normalize(vec3(0.5, 0.8, 0.6));
    float d = max(dot(normalize(n), l), 0.0) * 0.75 + 0.3;
    color = vec4(vec3(0.78, 0.80, 0.84) * d, 1.0);
}";
        int v = GL.CreateShader(ShaderType.VertexShader);
        GL.ShaderSource(v, vert);
        GL.CompileShader(v);
        int f = GL.CreateShader(ShaderType.FragmentShader);
        GL.ShaderSource(f, frag);
        GL.CompileShader(f);
        program = GL.CreateProgram();
        GL.AttachShader(program, v);
        GL.AttachShader(program, f);
        GL.LinkProgram(program);
        GL.DeleteShader(v);
        GL.DeleteShader(f);
    }

    static (Vector3 center, float radius) Bounds(Vector3[] vertices) {
        Vector3 min = vertices[0], max = vertices[0];
        foreach (Vector3 v in vertices) {
            min = Vector3.ComponentMin(min, v);
            max = Vector3.ComponentMax(max, v);
        }
        Vector3 center = (min + max) * 0.5f;
        var radius = Math.Max(0.01f, (max - min).Length * 0.5f);
        return (center, radius);
    }

    static void FlipRows(byte[] pixels, int size)
    {
        return;
        // var stride = size * 4;
        // var row = new byte[stride];
        // for (var y = 0; y < size / 2; y++) {
        //     var top = y * stride;
        //     var bottom = (size - 1 - y) * stride;
        //     Buffer.BlockCopy(pixels, top, row, 0, stride);
        //     Buffer.BlockCopy(pixels, bottom, pixels, top, stride);
        //     Buffer.BlockCopy(row, 0, pixels, bottom, stride);
        // }
    }
}
