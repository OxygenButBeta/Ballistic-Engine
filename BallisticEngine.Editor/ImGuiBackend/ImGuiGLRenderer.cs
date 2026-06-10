using System.Runtime.InteropServices;
using ImGuiNET;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace BallisticEngine.Editor;

// Minimal Dear ImGui OpenGL3 renderer for OpenTK. Uploads ImDrawData each frame into a
// growable VBO/EBO and draws with a small ortho shader, honoring per-command clip rects and
// texture bindings. Saves/restores the GL state it touches because it shares the engine's context.
internal sealed class ImGuiGLRenderer : IDisposable {
    int vao, vbo, ebo;
    int shader, attribLocationTex, attribLocationProj, attribLocationPos, attribLocationUV, attribLocationColor;
    int fontTexture;
    int vboSize, eboSize;

    public void CreateDeviceResources() {
        vbo = GL.GenBuffer();
        ebo = GL.GenBuffer();
        vboSize = 10000;
        eboSize = 2000;

        const string vert = @"#version 330 core
uniform mat4 projection_matrix;
layout(location = 0) in vec2 in_position;
layout(location = 1) in vec2 in_texCoord;
layout(location = 2) in vec4 in_color;
out vec4 color;
out vec2 texCoord;
void main() {
    gl_Position = projection_matrix * vec4(in_position, 0, 1);
    color = in_color;
    texCoord = in_texCoord;
}";
        const string frag = @"#version 330 core
uniform sampler2D in_fontTexture;
in vec4 color;
in vec2 texCoord;
out vec4 outputColor;
void main() {
    outputColor = color * texture(in_fontTexture, texCoord);
}";
        shader = CompileShader(vert, frag);

        attribLocationTex = GL.GetUniformLocation(shader, "in_fontTexture");
        attribLocationProj = GL.GetUniformLocation(shader, "projection_matrix");
        attribLocationPos = GL.GetAttribLocation(shader, "in_position");
        attribLocationUV = GL.GetAttribLocation(shader, "in_texCoord");
        attribLocationColor = GL.GetAttribLocation(shader, "in_color");

        vao = GL.GenVertexArray();
        GL.BindVertexArray(vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, vboSize, IntPtr.Zero, BufferUsageHint.DynamicDraw);
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, ebo);
        GL.BufferData(BufferTarget.ElementArrayBuffer, eboSize, IntPtr.Zero, BufferUsageHint.DynamicDraw);

        int stride = Marshal.SizeOf<ImDrawVert>();
        GL.EnableVertexAttribArray(attribLocationPos);
        GL.VertexAttribPointer(attribLocationPos, 2, VertexAttribPointerType.Float, false, stride, 0);
        GL.EnableVertexAttribArray(attribLocationUV);
        GL.VertexAttribPointer(attribLocationUV, 2, VertexAttribPointerType.Float, false, stride, 8);
        GL.EnableVertexAttribArray(attribLocationColor);
        GL.VertexAttribPointer(attribLocationColor, 4, VertexAttribPointerType.UnsignedByte, true, stride, 16);

        GL.BindVertexArray(0);

        RecreateFontTexture();
    }

    public void RecreateFontTexture() {
        ImGuiIOPtr io = ImGui.GetIO();
        io.Fonts.GetTexDataAsRGBA32(out IntPtr pixels, out int width, out int height, out _);

        fontTexture = GL.GenTexture();
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, fontTexture);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, width, height, 0,
            PixelFormat.Bgra, PixelType.UnsignedByte, pixels);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);

        io.Fonts.SetTexID(fontTexture);
        io.Fonts.ClearTexData();
    }

    public void Render(ImDrawDataPtr drawData) {
        if (drawData.CmdListsCount == 0)
            return;

        // Save GL state we modify. The engine leaves ActiveTexture on a high unit (shadow map /
        // skybox); ImGui's sampler reads unit 0, so we MUST switch to unit 0 before binding —
        // otherwise the whole UI samples whatever scene texture is left on unit 0.
        int lastActiveTexture = GL.GetInteger(GetPName.ActiveTexture);
        GL.ActiveTexture(TextureUnit.Texture0);
        int lastProgram = GL.GetInteger(GetPName.CurrentProgram);
        int lastTexture = GL.GetInteger(GetPName.TextureBinding2D);
        int lastVao = GL.GetInteger(GetPName.VertexArrayBinding);
        int lastArrayBuffer = GL.GetInteger(GetPName.ArrayBufferBinding);
        bool lastBlend = GL.IsEnabled(EnableCap.Blend);
        bool lastCull = GL.IsEnabled(EnableCap.CullFace);
        bool lastDepth = GL.IsEnabled(EnableCap.DepthTest);
        bool lastScissor = GL.IsEnabled(EnableCap.ScissorTest);
        var lastViewport = new int[4];
        GL.GetInteger(GetPName.Viewport, lastViewport);

        // Set up state for ImGui.
        GL.Enable(EnableCap.Blend);
        GL.BlendEquation(BlendEquationMode.FuncAdd);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        GL.Disable(EnableCap.CullFace);
        GL.Disable(EnableCap.DepthTest);
        GL.Enable(EnableCap.ScissorTest);

        ImGuiIOPtr io = ImGui.GetIO();
        var fbWidth = (int)(io.DisplaySize.X * io.DisplayFramebufferScale.X);
        var fbHeight = (int)(io.DisplaySize.Y * io.DisplayFramebufferScale.Y);
        GL.Viewport(0, 0, fbWidth, fbHeight);

        Matrix4 ortho = Matrix4.CreateOrthographicOffCenter(
            0f, io.DisplaySize.X, io.DisplaySize.Y, 0f, -1f, 1f);

        GL.UseProgram(shader);
        GL.UniformMatrix4(attribLocationProj, false, ref ortho);
        GL.Uniform1(attribLocationTex, 0);
        GL.BindVertexArray(vao);

        drawData.ScaleClipRects(io.DisplayFramebufferScale);

        for (int n = 0; n < drawData.CmdListsCount; n++) {
            ImDrawListPtr cmdList = drawData.CmdLists[n];

            int vtxBytes = cmdList.VtxBuffer.Size * Marshal.SizeOf<ImDrawVert>();
            int idxBytes = cmdList.IdxBuffer.Size * sizeof(ushort);

            GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
            if (vtxBytes > vboSize) { vboSize = Math.Max(vboSize * 2, vtxBytes); GL.BufferData(BufferTarget.ArrayBuffer, vboSize, IntPtr.Zero, BufferUsageHint.DynamicDraw); }
            GL.BufferSubData(BufferTarget.ArrayBuffer, IntPtr.Zero, vtxBytes, cmdList.VtxBuffer.Data);

            GL.BindBuffer(BufferTarget.ElementArrayBuffer, ebo);
            if (idxBytes > eboSize) { eboSize = Math.Max(eboSize * 2, idxBytes); GL.BufferData(BufferTarget.ElementArrayBuffer, eboSize, IntPtr.Zero, BufferUsageHint.DynamicDraw); }
            GL.BufferSubData(BufferTarget.ElementArrayBuffer, IntPtr.Zero, idxBytes, cmdList.IdxBuffer.Data);

            int idxOffset = 0;
            for (int cmdI = 0; cmdI < cmdList.CmdBuffer.Size; cmdI++) {
                ImDrawCmdPtr pcmd = cmdList.CmdBuffer[cmdI];
                System.Numerics.Vector4 clip = pcmd.ClipRect;
                GL.BindTexture(TextureTarget.Texture2D, (int)pcmd.TextureId);
                GL.Scissor((int)clip.X, fbHeight - (int)clip.W, (int)(clip.Z - clip.X), (int)(clip.W - clip.Y));
                GL.DrawElements(PrimitiveType.Triangles, (int)pcmd.ElemCount, DrawElementsType.UnsignedShort,
                    idxOffset * sizeof(ushort));
                idxOffset += (int)pcmd.ElemCount;
            }
        }

        // Restore state.
        GL.BindVertexArray(lastVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, lastArrayBuffer);
        GL.UseProgram(lastProgram);
        GL.BindTexture(TextureTarget.Texture2D, lastTexture);
        GL.ActiveTexture((TextureUnit)lastActiveTexture);
        GL.Viewport(lastViewport[0], lastViewport[1], lastViewport[2], lastViewport[3]);
        if (!lastScissor) GL.Disable(EnableCap.ScissorTest);
        if (!lastBlend) GL.Disable(EnableCap.Blend);
        if (lastCull) GL.Enable(EnableCap.CullFace);
        if (lastDepth) GL.Enable(EnableCap.DepthTest);
    }

    static int CompileShader(string vert, string frag) {
        int v = GL.CreateShader(ShaderType.VertexShader);
        GL.ShaderSource(v, vert); GL.CompileShader(v); CheckShader(v, "vertex");
        int f = GL.CreateShader(ShaderType.FragmentShader);
        GL.ShaderSource(f, frag); GL.CompileShader(f); CheckShader(f, "fragment");
        int p = GL.CreateProgram();
        GL.AttachShader(p, v); GL.AttachShader(p, f); GL.LinkProgram(p);
        GL.DetachShader(p, v); GL.DetachShader(p, f);
        GL.DeleteShader(v); GL.DeleteShader(f);
        return p;
    }

    static void CheckShader(int shader, string stage) {
        GL.GetShader(shader, ShaderParameter.CompileStatus, out int ok);
        if (ok == 0)
            throw new Exception($"ImGui {stage} shader failed: {GL.GetShaderInfoLog(shader)}");
    }

    public void Dispose() {
        if (vbo != 0) GL.DeleteBuffer(vbo);
        if (ebo != 0) GL.DeleteBuffer(ebo);
        if (vao != 0) GL.DeleteVertexArray(vao);
        if (shader != 0) GL.DeleteProgram(shader);
        if (fontTexture != 0) GL.DeleteTexture(fontTexture);
    }
}
