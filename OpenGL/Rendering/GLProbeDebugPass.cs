using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace BallisticEngine;

// Debug visualization for the baked irradiance volume: one small sphere per probe, each lit
// by evaluating its own stored L1 SH (ProbeDebug_Frag) - the exact data the PBR shader
// samples at that position. Toggled by IrradianceVolume.ShowProbes.
public sealed class GLProbeDebugPass {
    StandardShader shader;
    int vao, vbo, ebo;
    int indexCount;

    void EnsureResources() {
        if (shader is not null)
            return;

        shader = GraphicAPI.CreateStandardShader(
            EmbeddedShaderSource.Read("ProbeDebug_Vert.glsl"),
            EmbeddedShaderSource.Read("ProbeDebug_Frag.glsl"));

        // Unit lat-long sphere; positions double as normals in the shader.
        const int stacks = 12;
        const int slices = 18;
        var vertices = new List<Vector3>();
        for (var st = 0; st <= stacks; st++) {
            var phi = st / (float)stacks * MathF.PI;
            for (var sl = 0; sl <= slices; sl++) {
                var theta = sl / (float)slices * MathF.Tau;
                vertices.Add(new Vector3(
                    MathF.Sin(phi) * MathF.Cos(theta),
                    MathF.Cos(phi),
                    MathF.Sin(phi) * MathF.Sin(theta)));
            }
        }

        var indices = new List<uint>();
        var stride = slices + 1;
        for (var st = 0; st < stacks; st++)
        for (var sl = 0; sl < slices; sl++) {
            var a = (uint)(st * stride + sl);
            var b = (uint)(a + stride);
            indices.AddRange([a, b, a + 1, a + 1, b, b + 1]);
        }
        indexCount = indices.Count;

        vao = GL.GenVertexArray();
        vbo = GL.GenBuffer();
        ebo = GL.GenBuffer();
        GL.BindVertexArray(vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, vertices.Count * 3 * sizeof(float),
            vertices.ToArray(), BufferUsageHint.StaticDraw);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0);
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, ebo);
        GL.BufferData(BufferTarget.ElementArrayBuffer, indexCount * sizeof(uint),
            indices.ToArray(), BufferUsageHint.StaticDraw);
        GL.BindVertexArray(0);
    }

    public void Render(ref Matrix4 view, ref Matrix4 projection, int[] probeSH,
        Vector3 volumeMin, Vector3 volumeInvSize, int px, int py, int pz, float probeExposure) {
        EnsureResources();

        GL.Enable(EnableCap.DepthTest);
        GL.DepthFunc(DepthFunction.Less);
        GL.Enable(EnableCap.CullFace);
        GL.CullFace(TriangleFace.Back);
        GL.Disable(EnableCap.Blend);

        shader.Activate();
        for (var t = 0; t < 4; t++) {
            GL.ActiveTexture(TextureUnit.Texture0 + t);
            GL.BindTexture(TextureTarget.Texture3D, probeSH[t]);
        }
        shader.SetInt("ProbeSH0", 0);
        shader.SetInt("ProbeSH1", 1);
        shader.SetInt("ProbeSH2", 2);
        shader.SetInt("ProbeSH3", 3);
        shader.SetMatrix4("view", ref view);
        shader.SetMatrix4("projection", ref projection);
        shader.SetFloat("ProbeExposure", probeExposure);

        // Radius scales with the cell so dense grids stay readable; positions are cell
        // centres, matching both the bake and the 3D-texture texel centres.
        var size = new Vector3(1f / volumeInvSize.X, 1f / volumeInvSize.Y, 1f / volumeInvSize.Z);
        var cell = new Vector3(size.X / px, size.Y / py, size.Z / pz);
        var radius = Math.Clamp(MathF.Min(cell.X, MathF.Min(cell.Y, cell.Z)) * 0.22f, 0.05f, 0.5f);
        shader.SetFloat("Radius", radius);

        GL.BindVertexArray(vao);
        for (var z = 0; z < pz; z++)
        for (var y = 0; y < py; y++)
        for (var x = 0; x < px; x++) {
            var center = volumeMin + new Vector3(
                (x + 0.5f) / px * size.X, (y + 0.5f) / py * size.Y, (z + 0.5f) / pz * size.Z);
            shader.SetFloat3("Center", center);
            shader.SetFloat3("ProbeUVW", new Vector3(
                (x + 0.5f) / px, (y + 0.5f) / py, (z + 0.5f) / pz));
            GL.DrawElements(PrimitiveType.Triangles, indexCount, DrawElementsType.UnsignedInt, IntPtr.Zero);
        }
        GL.BindVertexArray(0);
        shader.Deactivate();
        GL.ActiveTexture(TextureUnit.Texture0);
    }
}
