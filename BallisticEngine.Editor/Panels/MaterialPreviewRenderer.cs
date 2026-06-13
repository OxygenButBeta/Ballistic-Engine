using BallisticEngine.AssetPipeline.Loaders;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace BallisticEngine.Editor;

// Editor-local MATERIAL thumbnail rendering (Unity's material preview sphere): a tiny self-contained
// GL pass that draws a UV sphere shaded with the material's base color, with a specular highlight whose
// tightness follows roughness and a metallic tint. Deliberately independent of the engine renderer
// (own FBO + shader + sphere mesh) so it can never collide with it, mirroring MeshPreviewRenderer.
//
// This is a thumbnail, not the real PBR pass — it reads as "this material" at a glance. It samples the
// material's diffuse + normal maps (a loaded Texture's UID IS its GL handle), tinted by base colour and
// shaded with a metallic/roughness specular, so it actually shows the textures, not just the colour.
internal static class MaterialPreviewRenderer {
    static int program;
    static int sphereVao, sphereVbo, sphereEbo, sphereIndexCount;

    public static byte[] Render(MaterialDefinition material, int size) {
        EnsureProgram();
        EnsureSphere();

        int prevFbo = GL.GetInteger(GetPName.FramebufferBinding);
        int prevVao = GL.GetInteger(GetPName.VertexArrayBinding);
        int prevProgram = GL.GetInteger(GetPName.CurrentProgram);
        var prevViewport = new int[4];
        GL.GetInteger(GetPName.Viewport, prevViewport);
        bool prevDepth = GL.IsEnabled(EnableCap.DepthTest);

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

        Matrix4 view = Matrix4.LookAt(new Vector3(0f, 0f, 3.0f), Vector3.Zero, Vector3.UnitY);
        Matrix4 proj = Matrix4.CreatePerspectiveFieldOfView(MathHelper.DegreesToRadians(35f), 1f, 0.1f, 10f);
        Matrix4 mvp = view * proj;

        // Capture + force the render state this pass needs — the engine leaves cull/blend/depth-func in
        // whatever the last scene draw set them to, which was making the sphere vanish or composite oddly
        // (it looked flat, not a sphere). Restored at the end.
        bool prevCull = GL.IsEnabled(EnableCap.CullFace);
        bool prevBlend = GL.IsEnabled(EnableCap.Blend);
        int prevDepthFunc = GL.GetInteger(GetPName.DepthFunc);
        int prevPolyMode = GL.GetInteger(GetPName.PolygonMode);

        GL.Viewport(0, 0, size, size);
        GL.Enable(EnableCap.DepthTest);
        GL.DepthFunc(DepthFunction.Less);
        GL.Disable(EnableCap.CullFace);   // draw both faces so winding can't hide the sphere
        GL.Disable(EnableCap.Blend);
        GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Fill);
        GL.ClearColor(0.13f, 0.13f, 0.15f, 1f);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        GL.UseProgram(program);
        GL.UniformMatrix4(GL.GetUniformLocation(program, "mvp"), false, ref mvp);

        Vector4 baseColor = BaseColorOf(material);
        GL.Uniform4(GL.GetUniformLocation(program, "baseColor"), baseColor);
        float rough = Clamp01(material.Roughness ?? 0.5f);
        float metal = Clamp01(material.Metallic ?? 0f);
        GL.Uniform1(GL.GetUniformLocation(program, "roughness"), rough);
        GL.Uniform1(GL.GetUniformLocation(program, "metallic"), metal);

        // Bind the material's DIFFUSE map (its GL handle is Texture.UID) so the preview shows the actual
        // albedo texture, not just the base colour. 0 if the slot is empty / not loadable -> shader uses
        // base colour alone.
        int diffuse = ResolveTextureHandle(material, "Diffuse");
        int normalMap = ResolveTextureHandle(material, "Normal");
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, diffuse);
        GL.Uniform1(GL.GetUniformLocation(program, "albedoMap"), 0);
        GL.Uniform1(GL.GetUniformLocation(program, "hasAlbedo"), diffuse != 0 ? 1 : 0);
        GL.ActiveTexture(TextureUnit.Texture1);
        GL.BindTexture(TextureTarget.Texture2D, normalMap);
        GL.Uniform1(GL.GetUniformLocation(program, "normalMap"), 1);
        GL.Uniform1(GL.GetUniformLocation(program, "hasNormal"), normalMap != 0 ? 1 : 0);

        GL.BindVertexArray(sphereVao);
        GL.DrawElements(PrimitiveType.Triangles, sphereIndexCount, DrawElementsType.UnsignedInt, 0);

        var pixels = new byte[size * size * 4];
        GL.ReadPixels(0, 0, size, size, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);

        GL.DeleteTexture(color);
        GL.DeleteRenderbuffer(depth);
        GL.DeleteFramebuffer(fbo);

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, prevFbo);
        GL.BindVertexArray(prevVao);
        GL.UseProgram(prevProgram);
        GL.Viewport(prevViewport[0], prevViewport[1], prevViewport[2], prevViewport[3]);
        if (!prevDepth) GL.Disable(EnableCap.DepthTest);
        if (prevCull) GL.Enable(EnableCap.CullFace);
        if (prevBlend) GL.Enable(EnableCap.Blend);
        GL.DepthFunc((DepthFunction)prevDepthFunc);
        GL.PolygonMode(MaterialFace.FrontAndBack, (PolygonMode)prevPolyMode);

        return pixels;
    }

    // The GL texture handle for a material slot ("Diffuse"/"Normal"/...), or 0 if empty/unloadable.
    // A loaded Texture's UID is its GL name (GLTexture2D.UID = GL.GenTexture()), so we can bind it.
    static int ResolveTextureHandle(MaterialDefinition m, string slot) {
        if (m.Textures is null || !m.Textures.TryGetValue(slot, out string reference) || string.IsNullOrEmpty(reference))
            return 0;
        try {
            string path = reference.StartsWith("guid:", StringComparison.Ordinal) &&
                          Guid.TryParseExact(reference["guid:".Length..], "N", out Guid g)
                ? AssetDatabase.GuidToAssetPath(g)
                : reference;
            if (path is null) return 0;
            var tex = AssetDatabase.Load<Texture2D>(path);
            return tex?.UID ?? 0;
        }
        catch { return 0; }
    }

    static Vector4 BaseColorOf(MaterialDefinition m) => m.BaseColor switch {
        { Length: >= 4 } c => new Vector4(c[0], c[1], c[2], c[3]),
        { Length: 3 } c => new Vector4(c[0], c[1], c[2], 1f),
        _ => Vector4.One,
    };

    static float Clamp01(float v) => Math.Clamp(v, 0f, 1f);

    static void EnsureProgram() {
        if (program != 0) return;
        string vert = EmbeddedShaderSource.Read("EditorMaterialPreview_Vert.glsl");
        string frag = EmbeddedShaderSource.Read("EditorMaterialPreview_Frag.glsl");
        int v = GL.CreateShader(ShaderType.VertexShader);
        GL.ShaderSource(v, vert); GL.CompileShader(v);
        CheckCompile(v, "material-preview vertex");
        int f = GL.CreateShader(ShaderType.FragmentShader);
        GL.ShaderSource(f, frag); GL.CompileShader(f);
        CheckCompile(f, "material-preview fragment");
        program = GL.CreateProgram();
        GL.AttachShader(program, v);
        GL.AttachShader(program, f);
        GL.LinkProgram(program);
        GL.GetProgram(program, GetProgramParameterName.LinkStatus, out int linked);
        if (linked == 0)
            Debugging.LogError($"Material preview shader link failed: {GL.GetProgramInfoLog(program)}");
        GL.DeleteShader(v);
        GL.DeleteShader(f);
    }

    static void CheckCompile(int shader, string label) {
        GL.GetShader(shader, ShaderParameter.CompileStatus, out int ok);
        if (ok == 0)
            Debugging.LogError($"Material preview {label} shader compile failed: {GL.GetShaderInfoLog(shader)}");
    }

    // A unit UV sphere (interleaved pos/normal/uv), generated once.
    static void EnsureSphere() {
        if (sphereVao != 0) return;
        const int stacks = 32, slices = 48;
        var verts = new List<float>();
        var indices = new List<uint>();
        for (int i = 0; i <= stacks; i++) {
            float phi = MathF.PI * i / stacks;
            for (int j = 0; j <= slices; j++) {
                float theta = 2f * MathF.PI * j / slices;
                float x = MathF.Sin(phi) * MathF.Cos(theta);
                float y = MathF.Cos(phi);
                float z = MathF.Sin(phi) * MathF.Sin(theta);
                verts.Add(x); verts.Add(y); verts.Add(z);          // pos (unit = normal)
                verts.Add(x); verts.Add(y); verts.Add(z);          // normal
                verts.Add((float)j / slices); verts.Add((float)i / stacks); // uv
            }
        }
        int ring = slices + 1;
        for (int i = 0; i < stacks; i++)
            for (int j = 0; j < slices; j++) {
                uint a = (uint)(i * ring + j), b = (uint)((i + 1) * ring + j);
                indices.Add(a); indices.Add(b); indices.Add(a + 1);
                indices.Add(a + 1); indices.Add(b); indices.Add(b + 1);
            }
        sphereIndexCount = indices.Count;

        sphereVao = GL.GenVertexArray();
        sphereVbo = GL.GenBuffer();
        sphereEbo = GL.GenBuffer();
        GL.BindVertexArray(sphereVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, sphereVbo);
        GL.BufferData(BufferTarget.ArrayBuffer, verts.Count * 4, verts.ToArray(), BufferUsageHint.StaticDraw);
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, sphereEbo);
        GL.BufferData(BufferTarget.ElementArrayBuffer, indices.Count * 4, indices.ToArray(), BufferUsageHint.StaticDraw);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 32, 0);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 32, 12);
        GL.EnableVertexAttribArray(2);
        GL.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, 32, 24);
        GL.BindVertexArray(0);
    }
}
