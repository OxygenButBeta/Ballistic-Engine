using System.Runtime.InteropServices;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace BallisticEngine;

// Draws every active ParticleSystem as GPU-instanced camera-facing billboards into the HDR color
// buffer, AFTER the transparent pass and BEFORE the screen-space/post chain — so particles get TAA +
// bloom for free (bright additive sparks glow). Self-contained (own VAO + unit-quad + per-particle
// instance VBO + shader), modeled on GLProbeDebugPass, and OFF the PassData UBO / z-prepass contracts
// entirely (particles write no depth). Saves/restores GL state on exit so the downstream HDR passes
// see the renderer's defaults — the zero-regression contract.
public sealed class GLParticlePass {
    StandardShader shader;
    int vao, quadVbo, quadEbo, instanceVbo;
    int instanceCapacity;   // in instances

    const int InstanceStrideBytes = 9 * sizeof(float); // vec3 pos + float size + vec4 color + float rot

    void EnsureResources() {
        if (shader is not null)
            return;

        shader = GraphicAPI.CreateStandardShader(
            EmbeddedShaderSource.Read("Particle_Vert.glsl"),
            EmbeddedShaderSource.Read("Particle_Frag.glsl"));

        // Unit quad: corners in [-0.5, 0.5] with uv. Two triangles.
        float[] quad = {
            // corner.xy      uv
            -0.5f, -0.5f,    0f, 0f,
             0.5f, -0.5f,    1f, 0f,
             0.5f,  0.5f,    1f, 1f,
            -0.5f,  0.5f,    0f, 1f,
        };
        uint[] indices = { 0, 1, 2, 0, 2, 3 };

        vao = GL.GenVertexArray();
        quadVbo = GL.GenBuffer();
        quadEbo = GL.GenBuffer();
        instanceVbo = GL.GenBuffer();

        GL.BindVertexArray(vao);

        GL.BindBuffer(BufferTarget.ArrayBuffer, quadVbo);
        GL.BufferData(BufferTarget.ArrayBuffer, quad.Length * sizeof(float), quad, BufferUsageHint.StaticDraw);
        GL.EnableVertexAttribArray(0); // corner.xy
        GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 4 * sizeof(float), 0);
        GL.EnableVertexAttribArray(1); // uv
        GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, 4 * sizeof(float), 2 * sizeof(float));

        GL.BindBuffer(BufferTarget.ElementArrayBuffer, quadEbo);
        GL.BufferData(BufferTarget.ElementArrayBuffer, indices.Length * sizeof(uint), indices, BufferUsageHint.StaticDraw);

        // Per-instance attributes (divisor 1) from the ParticleInstance layout. Allocate the buffer
        // BEFORE wiring attribute pointers — some drivers invalidate pointers set on a zero-size VBO.
        GL.BindBuffer(BufferTarget.ArrayBuffer, instanceVbo);
        GL.BufferData(BufferTarget.ArrayBuffer, InstanceStrideBytes * 256, IntPtr.Zero, BufferUsageHint.StreamDraw);
        instanceCapacity = 256;
        // Instance attribs at locations 4-7 (NOT 2/3 — those are reserved by the engine's standard
        // vertex layout for normal/tangent, and a particle VAO sharing the GL context inherited stale
        // generic-attrib state at 2/3 on this driver, reading zero. 4-7 read correctly).
        GL.EnableVertexAttribArray(4); // iPosition (vec3) @ 0
        GL.VertexAttribPointer(4, 3, VertexAttribPointerType.Float, false, InstanceStrideBytes, 0);
        GL.VertexAttribDivisor(4, 1);
        GL.EnableVertexAttribArray(5); // iSize (float) @ 12
        GL.VertexAttribPointer(5, 1, VertexAttribPointerType.Float, false, InstanceStrideBytes, 3 * sizeof(float));
        GL.VertexAttribDivisor(5, 1);
        GL.EnableVertexAttribArray(6); // iColor (vec4) @ 16
        GL.VertexAttribPointer(6, 4, VertexAttribPointerType.Float, false, InstanceStrideBytes, 4 * sizeof(float));
        GL.VertexAttribDivisor(6, 1);
        GL.EnableVertexAttribArray(7); // iRotation (float) @ 32
        GL.VertexAttribPointer(7, 1, VertexAttribPointerType.Float, false, InstanceStrideBytes, 8 * sizeof(float));
        GL.VertexAttribDivisor(7, 1);

        GL.BindVertexArray(0);
    }

    public void Render(ref Matrix4 view, ref Matrix4 projection) {
        var systems = RuntimeSet<ParticleSystem>.ReadOnlyCollection;
        if (systems.Count == 0)
            return;

        EnsureResources();

        // State: depth-test against the scene (already in the depth buffer) but DON'T write depth, so
        // particles are occluded by geometry yet don't occlude each other in depth. No backface cull
        // (billboards are single quads). Blend per system's mode.
        GL.Enable(EnableCap.DepthTest);
        GL.DepthFunc(DepthFunction.Lequal);
        GL.DepthMask(false);
        GL.Disable(EnableCap.CullFace);
        GL.Enable(EnableCap.Blend);

        shader.Activate();
        shader.SetMatrix4("view", ref view);
        shader.SetMatrix4("projection", ref projection);
        shader.SetInt("Particle", 0);

        GL.BindVertexArray(vao);

        foreach (ParticleSystem system in systems) {
            if (!system.IsActive)
                continue;
            int count = system.BuildInstances(out ParticleInstance[] instances);
            if (count == 0)
                continue;

            // Blend mode per system.
            if (system.BlendMode == ParticleBlendMode.Additive)
                GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One);
            else
                GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

            // Texture (unit 0) or the procedural soft dot.
            bool hasTex = system.Texture is { } tex && tex.UID != 0;
            shader.SetBool("HasTexture", hasTex);
            if (hasTex) {
                GL.ActiveTexture(TextureUnit.Texture0);
                GL.BindTexture(TextureTarget.Texture2D, system.Texture.UID);
            }

            UploadInstances(instances, count);
            GL.DrawElementsInstanced(PrimitiveType.Triangles, 6, DrawElementsType.UnsignedInt, IntPtr.Zero, count);
        }

        GL.BindVertexArray(0);
        shader.Deactivate();

        // Restore the renderer's default state for the downstream HDR passes (the zero-regression
        // contract): depth writes on, blend off, backface cull on, default depth func.
        GL.DepthMask(true);
        GL.Disable(EnableCap.Blend);
        GL.Enable(EnableCap.CullFace);
        GL.DepthFunc(DepthFunction.Less);
        GL.ActiveTexture(TextureUnit.Texture0);
    }

    float[] uploadScratch;
    void UploadInstances(ParticleInstance[] instances, int count) {
        // Flatten to a float[] (9 floats per instance) and upload — the most portable path through
        // OpenTK's typed BufferData overloads (a raw struct blit via BufferSubData(byte[]) silently
        // uploaded nothing on this driver).
        int floats = count * 9;
        if (uploadScratch is null || uploadScratch.Length < floats)
            uploadScratch = new float[floats];
        for (var i = 0; i < count; i++) {
            ref ParticleInstance p = ref instances[i];
            int o = i * 9;
            uploadScratch[o + 0] = p.Position.X;
            uploadScratch[o + 1] = p.Position.Y;
            uploadScratch[o + 2] = p.Position.Z;
            uploadScratch[o + 3] = p.Size;
            uploadScratch[o + 4] = p.Color.X;
            uploadScratch[o + 5] = p.Color.Y;
            uploadScratch[o + 6] = p.Color.Z;
            uploadScratch[o + 7] = p.Color.W;
            uploadScratch[o + 8] = p.Rotation;
        }

        GL.BindBuffer(BufferTarget.ArrayBuffer, instanceVbo);
        if (count > instanceCapacity) {
            GL.BufferData(BufferTarget.ArrayBuffer, count * InstanceStrideBytes, IntPtr.Zero, BufferUsageHint.StreamDraw);
            instanceCapacity = count;
        }
        GL.BufferSubData(BufferTarget.ArrayBuffer, IntPtr.Zero, floats * sizeof(float), uploadScratch);
    }
}
