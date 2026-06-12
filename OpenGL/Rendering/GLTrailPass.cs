using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace BallisticEngine;

// Draws every active TrailRenderer as a camera-facing ribbon (triangle strip) into the HDR color
// buffer, alongside the particle pass (after transparent, before the post chain) so trails get TAA +
// bloom. Self-contained (own VAO + a dynamic vertex VBO rebuilt each frame from the trail's ribbon
// vertices), OFF the PassData UBO / z-prepass, and restores GL state on exit (zero-regression).
//
// Unlike GLParticlePass this is NOT instanced — each trail is a unique strip, so the pass streams its
// CPU-built ribbon vertices (already camera-facing) and DrawArrays a TriangleStrip.
public sealed class GLTrailPass {
    StandardShader shader;
    int vao, vbo;
    int capacityVerts;

    const int Stride = 9 * sizeof(float); // pos(3) + uv(2) + color(4)

    void EnsureResources() {
        if (shader is not null)
            return;

        shader = GraphicAPI.CreateStandardShader(
            EmbeddedShaderSource.Read("Trail_Vert.glsl"),
            EmbeddedShaderSource.Read("Trail_Frag.glsl"));

        vao = GL.GenVertexArray();
        vbo = GL.GenBuffer();
        GL.BindVertexArray(vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, Stride * 256, IntPtr.Zero, BufferUsageHint.StreamDraw);
        capacityVerts = 256;
        GL.EnableVertexAttribArray(0); // position
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, Stride, 0);
        GL.EnableVertexAttribArray(1); // uv
        GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, Stride, 3 * sizeof(float));
        GL.EnableVertexAttribArray(2); // color
        GL.VertexAttribPointer(2, 4, VertexAttribPointerType.Float, false, Stride, 5 * sizeof(float));
        GL.BindVertexArray(0);
    }

    public void Render(ref Matrix4 view, ref Matrix4 projection, Vector3 cameraPos) {
        var sources = RuntimeSet<IRibbonSource>.ReadOnlyCollection;
        if (sources.Count == 0)
            return;

        EnsureResources();

        GL.Enable(EnableCap.DepthTest);
        GL.DepthFunc(DepthFunction.Lequal);
        GL.DepthMask(false);
        GL.Disable(EnableCap.CullFace);   // a ribbon is two-sided
        GL.Enable(EnableCap.Blend);

        shader.Activate();
        shader.SetMatrix4("view", ref view);
        shader.SetMatrix4("projection", ref projection);
        shader.SetInt("Trail", 0);

        GL.BindVertexArray(vao);

        foreach (IRibbonSource ribbon in sources) {
            if (!ribbon.IsActive || !ribbon.RibbonRenderable)
                continue;
            int count = ribbon.BuildRibbon(cameraPos, out RibbonVertex[] verts);
            if (count < 4)   // need at least 2 segments' worth for a visible strip
                continue;

            if (ribbon.BlendMode == RibbonBlendMode.Additive)
                GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One);
            else
                GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

            bool hasTex = ribbon.RibbonTexture is { } tex && tex.UID != 0;
            shader.SetBool("HasTexture", hasTex);
            if (hasTex) {
                GL.ActiveTexture(TextureUnit.Texture0);
                GL.BindTexture(TextureTarget.Texture2D, ribbon.RibbonTexture.UID);
            }

            Upload(verts, count);
            GL.DrawArrays(PrimitiveType.TriangleStrip, 0, count);
        }

        GL.BindVertexArray(0);
        shader.Deactivate();

        // Restore renderer defaults for the downstream HDR passes (zero-regression contract).
        GL.DepthMask(true);
        GL.Disable(EnableCap.Blend);
        GL.Enable(EnableCap.CullFace);
        GL.DepthFunc(DepthFunction.Less);
        GL.ActiveTexture(TextureUnit.Texture0);
    }

    float[] uploadScratch;
    void Upload(RibbonVertex[] verts, int count) {
        int floats = count * 9;
        if (uploadScratch is null || uploadScratch.Length < floats)
            uploadScratch = new float[floats];
        for (var i = 0; i < count; i++) {
            ref RibbonVertex v = ref verts[i];
            int o = i * 9;
            uploadScratch[o + 0] = v.Position.X;
            uploadScratch[o + 1] = v.Position.Y;
            uploadScratch[o + 2] = v.Position.Z;
            uploadScratch[o + 3] = v.Uv.X;
            uploadScratch[o + 4] = v.Uv.Y;
            uploadScratch[o + 5] = v.Color.X;
            uploadScratch[o + 6] = v.Color.Y;
            uploadScratch[o + 7] = v.Color.Z;
            uploadScratch[o + 8] = v.Color.W;
        }

        GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
        if (count > capacityVerts) {
            GL.BufferData(BufferTarget.ArrayBuffer, count * Stride, IntPtr.Zero, BufferUsageHint.StreamDraw);
            capacityVerts = count;
        }
        GL.BufferSubData(BufferTarget.ArrayBuffer, IntPtr.Zero, floats * sizeof(float), uploadScratch);
    }
}
