using System.Buffers;
using BallisticEngine.Rendering;
using BallisticEngine.Sky;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace BallisticEngine;

public class GLHDRenderer : HDRenderer {
    const int ShadowMapSize = 4096;
    const int MaxPointLights = 8;
    const int MaxSpotLights = 4;

    IWindow window;
    bool anythingDrawnThisFrame;
    SkyboxRenderer skyboxRenderer;
    GLFrameBuffer frameBuffer;     // Scene view (editor camera) / player present target — HDR
    GLFrameBuffer gameBuffer;      // Game view (scene camera) — editor only, HDR
    GLFrameBuffer sceneDisplay;    // post-processed (tonemapped) output the editor panels sample
    GLFrameBuffer gameDisplay;
    GLMultisampleFrameBuffer sceneMsaa;
    GLMultisampleFrameBuffer gameMsaa;
    StandardShader shadowDepthShader;
    GLShadowMap shadowMap;
    GLCompositePass composite;
    GLBloomPass bloom;
    GLSSAOPass ssao;
    int maxMsaaSamples = 1;

    // IBL resources. The BRDF LUT is baked once; irradiance + prefiltered specular are
    // rebaked whenever the active skybox cubemap changes.
    int brdfLut;
    int irradianceMap;
    int prefilteredMap;
    Texture3D iblSource;

    Matrix4 lightSpaceMatrix = Matrix4.Identity;
    Matrix4 skyRotation = Matrix4.Identity;

    GLFrameBuffer CurrentTarget => ActiveTarget == RenderTarget.Game ? gameBuffer : frameBuffer;
    GLFrameBuffer CurrentDisplay => ActiveTarget == RenderTarget.Game ? gameDisplay : sceneDisplay;

    public override void Initialize() {
        skyboxRenderer = new SkyboxRenderer();
        skyboxRenderer.init();
        window = Window.Current;
        frameBuffer = new GLFrameBuffer(window.Width, window.Height, depthAsTexture: true);
        gameBuffer = new GLFrameBuffer(window.Width, window.Height, depthAsTexture: true);
        sceneDisplay = new GLFrameBuffer(window.Width, window.Height);
        gameDisplay = new GLFrameBuffer(window.Width, window.Height);
        shadowMap = new GLShadowMap(ShadowMapSize, ShadowMapSize);
        composite = new GLCompositePass();
        bloom = new GLBloomPass();
        ssao = new GLSSAOPass();
        maxMsaaSamples = GL.GetInteger(GetPName.MaxSamples);
        brdfLut = GLEnvironmentMaps.GenerateBrdfLut();

        // Track the window size only when presenting to it (player). In the editor the
        // panels own the target sizes via ResizeSceneTarget/ResizeGameTarget.
        window.OnResizeCallback += (x, y) => {
            if (PresentToScreen)
                frameBuffer.Resize(x, y);
        };
        const string shadowVert = @"
#version 330 core
layout(location = 0) in vec3 position;

uniform mat4 model;
uniform mat4 lightSpaceMatrix;

void main() {
    gl_Position = lightSpaceMatrix * model * vec4(position, 1.0);
}
";
        const string shadowFrag = @"
#version 330 core
void main() {
}
";
        shadowDepthShader = GraphicAPI.CreateStandardShader(shadowVert, shadowFrag);
    }

    public float Metallic = 1f;
    public float RoughnessValue = 1f;
    int renderMode = 0;

    // Per-frame working sets, split by material blend mode.
    static readonly List<IStaticMeshRenderer> opaqueRenderers = new();
    static readonly List<IStaticMeshRenderer> transparentRenderers = new();

    // Gathered punctual lights, uploaded per draw.
    int pointLightCount;
    readonly Vector3[] pointPositions = new Vector3[MaxPointLights];
    readonly Vector3[] pointColors = new Vector3[MaxPointLights];
    readonly float[] pointRanges = new float[MaxPointLights];
    int spotLightCount;
    readonly Vector3[] spotPositions = new Vector3[MaxSpotLights];
    readonly Vector3[] spotDirections = new Vector3[MaxSpotLights];
    readonly Vector3[] spotColors = new Vector3[MaxSpotLights];
    readonly float[] spotRanges = new float[MaxSpotLights];
    readonly float[] spotCosInner = new float[MaxSpotLights];
    readonly float[] spotCosOuter = new float[MaxSpotLights];

    static readonly string[] PointPositionNames = BuildIndexedNames("PointLightPosition", MaxPointLights);
    static readonly string[] PointColorNames = BuildIndexedNames("PointLightColor", MaxPointLights);
    static readonly string[] PointRangeNames = BuildIndexedNames("PointLightRange", MaxPointLights);
    static readonly string[] SpotPositionNames = BuildIndexedNames("SpotLightPosition", MaxSpotLights);
    static readonly string[] SpotDirectionNames = BuildIndexedNames("SpotLightDirection", MaxSpotLights);
    static readonly string[] SpotColorNames = BuildIndexedNames("SpotLightColor", MaxSpotLights);
    static readonly string[] SpotRangeNames = BuildIndexedNames("SpotLightRange", MaxSpotLights);
    static readonly string[] SpotCosInnerNames = BuildIndexedNames("SpotLightCosInner", MaxSpotLights);
    static readonly string[] SpotCosOuterNames = BuildIndexedNames("SpotLightCosOuter", MaxSpotLights);

    static string[] BuildIndexedNames(string baseName, int count) {
        var names = new string[count];
        for (var i = 0; i < count; i++)
            names[i] = $"{baseName}[{i}]";
        return names;
    }

    public override RenderMetrics BeginRender(RendererArgs args) {
        // The scene's Skybox component drives the sky (null = no sky, default ambient).
        skyboxRenderer.cubemapTexture = Skybox.Active is { IsActive: true } sky ? sky.Cubemap : null;

        // Scene-driven post settings (Unity-volume style); engine defaults when no volume exists.
        if (PostProcessVolume.Active is { IsActive: true } volume)
            volume.CopyTo(PostFX);

        UpdateEnvironmentMaps();
        UpdateSkyRotation();

        Matrix4 view = args.viewProjectionProvider.GetViewMatrix();
        Matrix4 projection = args.viewProjectionProvider.GetProjectionMatrix();
        Vector3 cameraPos = args.viewProjectionProvider.Transform.Position;

        SplitRenderables(cameraPos);
        GatherPunctualLights();

        LightUniforms light = LightUniforms.Resolve();
        lightSpaceMatrix = ShadowMath.ComputeLightSpaceMatrix(view, projection, -light.Direction,
            DirectionalLight.Instance?.ShadowDistance ?? 60f, shadowMap.Width);

        RenderShadowPass();

        if (RenderAsset.Current.InstancedDrawing) {
            // Disabled at the moment
            foreach (BatchGroup<IStaticMeshRenderer> batchGroup in
                     RendererHelpers.CreateBatchGroupsForOpaqueDrawables())
                RenderInstancing(batchGroup, args);
        }

        GLFrameBuffer target = CurrentTarget;
        GLMultisampleFrameBuffer msaa = EnsureMsaaTarget(target);
        if (msaa is not null)
            msaa.Activate();
        else
            target.Activate();
        GL.Viewport(0, 0, target.LenX, target.LenY);
        ClearColorBuffer();

        RenderMeshes(opaqueRenderers, transparentPass: false, ref view, ref projection, cameraPos);

        DebugCheck();
        if (skyboxRenderer.cubemapTexture is not null) {
            skyboxRenderer.PreRenderCallback(args);
            skyboxRenderer.RenderSkybox();
            skyboxRenderer.PostRenderCallback(args);
        }

        if (transparentRenderers.Count > 0) {
            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            GL.DepthMask(false);
            RenderMeshes(transparentRenderers, transparentPass: true, ref view, ref projection, cameraPos);
            GL.DepthMask(true);
            GL.Disable(EnableCap.Blend);
        }

        if (msaa is not null)
            msaa.BlitTo(target);

        var aoTexture = PostFX.SSAOEnabled
            ? ssao.Render(target.DepthTextureId, target.LenX, target.LenY, projection, PostFX)
            : 0;
        var bloomTexture = PostFX.BloomEnabled
            ? bloom.Render(target.colorBuffer, target.LenX, target.LenY, PostFX)
            : 0;

        if (PresentToScreen)
            composite.Render(target, null, target.LenX, target.LenY, PostFX, bloomTexture, aoTexture);
        else
            composite.Render(target, CurrentDisplay, CurrentDisplay.LenX, CurrentDisplay.LenY, PostFX,
                bloomTexture, aoTexture);

        return new RenderMetrics();
    }

    void SplitRenderables(Vector3 cameraPos) {
        opaqueRenderers.Clear();
        transparentRenderers.Clear();
        foreach (IStaticMeshRenderer target in RuntimeSet<IStaticMeshRenderer>.ReadOnlyCollection) {
            if (!target.IsRenderable || !target.IsActive)
                continue;

            // A multi-material mesh can hold both blend modes (walls + glass); it then goes in
            // both lists and each pass draws only its own submeshes.
            var hasOpaque = false;
            var hasTransparent = false;
            SubMeshData[] subMeshes = target.SharedMesh.SubMeshes;
            for (var i = 0; i < subMeshes.Length; i++) {
                Material material = target.MaterialFor(i);
                if (material is null)
                    continue;
                if (material.Transparent)
                    hasTransparent = true;
                else
                    hasOpaque = true;
            }

            if (hasOpaque)
                opaqueRenderers.Add(target);
            if (hasTransparent)
                transparentRenderers.Add(target);
        }

        // Back-to-front so alpha blending composites correctly.
        if (transparentRenderers.Count > 1)
            transparentRenderers.Sort((a, b) =>
                (b.Transform.Position - cameraPos).LengthSquared.CompareTo(
                    (a.Transform.Position - cameraPos).LengthSquared));
    }

    void GatherPunctualLights() {
        pointLightCount = 0;
        foreach (PointLight point in RuntimeSet<PointLight>.ReadOnlyCollection) {
            if (pointLightCount >= MaxPointLights)
                break;
            if (!point.IsActive)
                continue;
            pointPositions[pointLightCount] = point.transform.Position;
            pointColors[pointLightCount] = point.Color * point.Intensity;
            pointRanges[pointLightCount] = MathF.Max(point.Range, 1e-3f);
            pointLightCount++;
        }

        spotLightCount = 0;
        foreach (SpotLight spot in RuntimeSet<SpotLight>.ReadOnlyCollection) {
            if (spotLightCount >= MaxSpotLights)
                break;
            if (!spot.IsActive)
                continue;
            spotPositions[spotLightCount] = spot.transform.Position;
            spotDirections[spotLightCount] = spot.transform.Forward;
            spotColors[spotLightCount] = spot.Color * spot.Intensity;
            spotRanges[spotLightCount] = MathF.Max(spot.Range, 1e-3f);
            var inner = MathHelper.DegreesToRadians(Math.Clamp(spot.InnerAngle, 0f, 89f));
            var outer = MathHelper.DegreesToRadians(Math.Clamp(MathF.Max(spot.OuterAngle, spot.InnerAngle), 0f, 89.9f));
            spotCosInner[spotLightCount] = MathF.Cos(inner);
            spotCosOuter[spotLightCount] = MathF.Cos(outer);
            spotLightCount++;
        }
    }

    void RenderShadowPass() {
        shadowMap.Bind(); // binds FBO, sets viewport, clears depth
        GL.ColorMask(false, false, false, false);
        GL.DepthMask(true);
        GL.Enable(EnableCap.DepthTest);
        GL.Enable(EnableCap.CullFace);
        // Front-face culling pushes acne onto back faces where the bias hides it.
        GL.CullFace(TriangleFace.Front);

        shadowDepthShader.Activate();
        shadowDepthShader.SetMatrix4("lightSpaceMatrix", ref lightSpaceMatrix);
        foreach (IStaticMeshRenderer target in opaqueRenderers) {
            Mesh mesh = target.SharedMesh;
            Matrix4 worldMatrix = target.Transform.WorldMatrix;
            mesh.Activate(); // VAO only; the depth pass doesn't need the material
            shadowDepthShader.SetMatrix4("model", ref worldMatrix);

            // Only opaque submeshes cast shadows; transparent/unassigned ranges are skipped.
            SubMeshData[] subMeshes = mesh.SubMeshes;
            for (var i = 0; i < subMeshes.Length; i++) {
                if (target.MaterialFor(i) is not { Transparent: false })
                    continue;
                GL.DrawElements(PrimitiveType.Triangles, subMeshes[i].IndexCount, DrawElementsType.UnsignedInt,
                    (IntPtr)(subMeshes[i].IndexStart * sizeof(uint)));
            }

            mesh.Deactivate();
        }
        shadowDepthShader.Deactivate();

        GL.ColorMask(true, true, true, true);
        GL.CullFace(TriangleFace.Back);
        shadowMap.Unbind();
    }

    GLMultisampleFrameBuffer EnsureMsaaTarget(GLFrameBuffer target) {
        var samples = Math.Min(PostFX.MsaaSamples, maxMsaaSamples);
        if (samples <= 1)
            return null;

        if (ActiveTarget == RenderTarget.Game) {
            gameMsaa ??= new GLMultisampleFrameBuffer(target.LenX, target.LenY, samples);
            gameMsaa.Resize(target.LenX, target.LenY, samples);
            return gameMsaa;
        }

        sceneMsaa ??= new GLMultisampleFrameBuffer(target.LenX, target.LenY, samples);
        sceneMsaa.Resize(target.LenX, target.LenY, samples);
        return sceneMsaa;
    }

    void UpdateEnvironmentMaps() {
        Texture3D current = skyboxRenderer.cubemapTexture;
        if (ReferenceEquals(current, iblSource))
            return;

        if (irradianceMap != 0) {
            GL.DeleteTexture(irradianceMap);
            GL.DeleteTexture(prefilteredMap);
            irradianceMap = 0;
            prefilteredMap = 0;
        }

        iblSource = current;
        if (current is null)
            return;

        irradianceMap = GLEnvironmentMaps.GenerateIrradiance(current.UID);
        prefilteredMap = GLEnvironmentMaps.GeneratePrefiltered(current.UID);
        Console.WriteLine("IBL environment maps baked for active skybox.");
    }

    void UpdateSkyRotation() {
        Vector3 euler = Skybox.Active?.RotationEuler ?? Vector3.Zero;
        skyRotation =
            Matrix4.CreateRotationX(MathHelper.DegreesToRadians(euler.X)) *
            Matrix4.CreateRotationY(MathHelper.DegreesToRadians(euler.Y)) *
            Matrix4.CreateRotationZ(MathHelper.DegreesToRadians(euler.Z));
    }

    // Draws each target's submeshes that match the requested blend mode, binding that
    // submesh's material per range. Multi-material meshes thus split correctly across the
    // opaque and transparent passes.
    void RenderMeshes(List<IStaticMeshRenderer> targets, bool transparentPass, ref Matrix4 view,
        ref Matrix4 projection, Vector3 cameraPos) {
        GL.Enable(EnableCap.DepthTest);
        GL.CullFace(TriangleFace.Back);
        GL.Enable(EnableCap.CullFace);
        GL.FrontFace(FrontFaceDirection.Ccw);

        foreach (IStaticMeshRenderer target in targets) {
            Mesh mesh = target.SharedMesh;
            SubMeshData[] subMeshes = mesh.SubMeshes;
            mesh.Activate();

            Material lastActivated = null;
            for (var i = 0; i < subMeshes.Length; i++) {
                Material material = target.MaterialFor(i);
                if (material is null || material.Transparent != transparentPass)
                    continue;

                material.Activate();
                lastActivated = material;
                SetUniformsForLitRender(target, material, ref view, ref projection, cameraPos);
                GL.DrawElements(PrimitiveType.Triangles, subMeshes[i].IndexCount, DrawElementsType.UnsignedInt,
                    (IntPtr)(subMeshes[i].IndexStart * sizeof(uint)));
                anythingDrawnThisFrame = true;
            }

            lastActivated?.Deactivate();
            mesh.Deactivate();
        }
    }

    // Legacy entry point; the per-frame path is BeginRender, which splits opaque/transparent.
    public override void RenderOpaque(IReadOnlyCollection<IStaticMeshRenderer> renderTargets, RendererArgs args,
        bool isShadowPass) {
        if (isShadowPass) {
            RenderShadowPass();
            return;
        }

        Matrix4 view = args.viewProjectionProvider.GetViewMatrix();
        Matrix4 projection = args.viewProjectionProvider.GetProjectionMatrix();
        Vector3 cameraPos = args.viewProjectionProvider.Transform.Position;
        opaqueRenderers.Clear();
        foreach (IStaticMeshRenderer target in renderTargets)
            if (target.IsRenderable && target.IsActive)
                opaqueRenderers.Add(target);
        RenderMeshes(opaqueRenderers, transparentPass: false, ref view, ref projection, cameraPos);
    }

    void SetUniformsForLitRender(IStaticMeshRenderer target, Material material, ref Matrix4 view,
        ref Matrix4 projection, Vector3 cameraPos) {
        Shader shader = material.Shader;
        Matrix4 worldMatrix = target.Transform.WorldMatrix;
        LightUniforms light = LightUniforms.Resolve();

        // Sun + ambient.
        shader.SetFloat3("LightDirection", light.Direction);
        shader.SetFloat3("LightColor", light.Color);
        shader.SetFloat3("AmbientLight",
            light.AmbientIntensity * (Skybox.Active?.Exposure ?? 1f) *
            (skyboxRenderer.cubemapTexture?.skyAmbient ?? Vector3.One * 0.5f));

        // Punctual lights.
        shader.SetInt("PointLightCount", pointLightCount);
        for (var i = 0; i < pointLightCount; i++) {
            shader.SetFloat3(PointPositionNames[i], pointPositions[i]);
            shader.SetFloat3(PointColorNames[i], pointColors[i]);
            shader.SetFloat(PointRangeNames[i], pointRanges[i]);
        }

        shader.SetInt("SpotLightCount", spotLightCount);
        for (var i = 0; i < spotLightCount; i++) {
            shader.SetFloat3(SpotPositionNames[i], spotPositions[i]);
            shader.SetFloat3(SpotDirectionNames[i], spotDirections[i]);
            shader.SetFloat3(SpotColorNames[i], spotColors[i]);
            shader.SetFloat(SpotRangeNames[i], spotRanges[i]);
            shader.SetFloat(SpotCosInnerNames[i], spotCosInner[i]);
            shader.SetFloat(SpotCosOuterNames[i], spotCosOuter[i]);
        }

        // Material controls.
        shader.SetFloat("MetallicMultiplier", Metallic);
        shader.SetFloat("RoughnessMultiplier", RoughnessValue);
        shader.SetFloat("minRoughness", 0.04f);
        shader.SetBool("NormalFlipY", true);
        shader.SetFloat("NormalStrength", NormalStrength);
        shader.SetFloat3("EmissiveFactor", material.EmissiveColor * material.EmissiveIntensity);
        shader.SetBool("HasEmissive", material.Emissive is not null);
        shader.SetBool("AlphaBlend", material.Transparent);
        shader.SetFloat("Opacity", material.Opacity);

        // Shadows.
        shader.SetMatrix4("lightSpaceMatrix", ref lightSpaceMatrix);
        shader.SetFloat("ShadowBias", DirectionalLight.Instance?.ShadowBias ?? 0.0015f);
        GL.ActiveTexture(TextureUnit.Texture10);
        GL.BindTexture(TextureTarget.Texture2D, shadowMap.DepthTextureId);

        // Sky reflections fallback needs the cubemap bound during the lit pass too (the skybox
        // draw only binds it afterwards, which would leave unit 11 empty on the first frame).
        if (skyboxRenderer.cubemapTexture is not null) {
            GL.ActiveTexture(TextureUnit.Texture11);
            GL.BindTexture(TextureTarget.TextureCubeMap, skyboxRenderer.cubemapTexture.UID);
        }

        // IBL.
        GL.ActiveTexture(TextureUnit.Texture12);
        GL.BindTexture(TextureTarget.TextureCubeMap, irradianceMap);
        GL.ActiveTexture(TextureUnit.Texture13);
        GL.BindTexture(TextureTarget.TextureCubeMap, prefilteredMap);
        GL.ActiveTexture(TextureUnit.Texture14);
        GL.BindTexture(TextureTarget.Texture2D, brdfLut);
        shader.SetBool("UseIBL", irradianceMap != 0);
        shader.SetFloat("MaxPrefilterMips", GLEnvironmentMaps.PrefilterMipCount - 1);
        shader.SetFloat("SkyExposure", Skybox.Active?.Exposure ?? 1f);
        shader.SetMatrix4("SkyRotation", ref skyRotation);

        // Sampler slots.
        shader.SetInt("Diffuse", 0);
        shader.SetInt("Normal", 1);
        shader.SetInt("Metallic", 2);
        shader.SetInt("Roughness", 3);
        shader.SetInt("AO", 4);
        shader.SetInt("Emissive", 5);
        shader.SetInt("ShadowMap", 10);
        shader.SetInt("Skybox", 11);
        shader.SetInt("IrradianceMap", 12);
        shader.SetInt("PrefilteredEnvMap", 13);
        shader.SetInt("BRDF_LUT", 14);

        shader.SetInt("renderMode", renderMode);
        shader.SetBool("EnableAtmosphericScattering", fogEnabled);
        shader.SetMatrix4("view", ref view);
        shader.SetMatrix4("projection", ref projection);
        shader.SetMatrix4("model", ref worldMatrix);
        shader.SetFloat3("CameraPos", cameraPos);
    }

    public float NormalStrength { get; set; } = 1f;

    public override void RenderSkybox(IReadOnlyCollection<ISkyboxDrawable> renderTargets, RendererArgs args) {
        throw new NotImplementedException();
    }

    public override void RenderInstancing(Mesh mesh, Material material, Matrix4[] transforms, RendererArgs args) {
        throw new NotImplementedException(
            "Instancing is handled in RenderInstancing(BatchGroup<IOpaqueDrawable> batchGroup, RendererArgs args) method.");
    }

    public override void RenderInstancing(BatchGroup<IStaticMeshRenderer> batchGroup, RendererArgs args) {
        var instanceCount = batchGroup.Matrix4s.Count;
        if (instanceCount == 0)
            return;

        Matrix4 view = args.viewProjectionProvider.GetViewMatrix();
        Matrix4 projection = args.viewProjectionProvider.GetProjectionMatrix();

        IStaticMeshRenderer target = batchGroup.Drawable;
        Mesh mesh = target.SharedMesh;
        Material material = target.MaterialFor(0);
        if (material is null)
            return;
        Shader shader = material.Shader;
        material.Activate();
        mesh.Activate();


        shader.SetBool("isInstanced", true);
        shader.SetMatrix4("view", ref view, true);
        shader.SetMatrix4("projection", ref projection, true);

        Matrix4[] array = ArrayPool<Matrix4>.Shared.Rent(batchGroup.Matrix4s.Count);
        batchGroup.Matrix4s.CopyTo(array, 0);
        target.SharedMesh.InstanceBuffer.SetBufferData(array, BufferUsageHint.StreamDraw);
        GL.DrawElementsInstanced(
            PrimitiveType.Triangles,
            mesh.Indices.Length,
            DrawElementsType.UnsignedInt,
            IntPtr.Zero,
            instanceCount
        );
        ArrayPool<Matrix4>.Shared.Return(array);
        shader.SetBool("isInstanced", false);
        batchGroup.Dispose();
        anythingDrawnThisFrame = true;
    }

    public override int SceneColorTextureId => sceneDisplay.colorBuffer;
    public override int GameColorTextureId => gameDisplay.colorBuffer;

    public override void ResizeSceneTarget(int width, int height) {
        if (width <= 0 || height <= 0)
            return;
        frameBuffer.Resize(width, height);
        sceneDisplay.Resize(width, height);
    }

    public override void ResizeGameTarget(int width, int height) {
        if (width <= 0 || height <= 0)
            return;
        gameBuffer.Resize(width, height);
        gameDisplay.Resize(width, height);
    }

    void DebugCheck() {
        if (Input.IsKeyDown(Keys.KeyPad0)) {
            Metallic += 0.002f;
            Metallic = Math.Clamp(Metallic, 0f, 100f);
        }

        if (Input.IsKeyDown(Keys.KeyPad1)) {
            Metallic -= 0.002f;
            Metallic = Math.Clamp(Metallic, 0f, 100f);
        }

        if (Input.IsKeyDown(Keys.KeyPad2)) {
            RoughnessValue += 0.01f;
            RoughnessValue = Math.Clamp(RoughnessValue, 0f, 2f);
        }

        if (Input.IsKeyDown(Keys.KeyPad9)) {
            NormalStrength += 0.002f;
        }

        if (Input.IsKeyDown(Keys.KeyPad8)) {
            NormalStrength -= 0.002f;
        }

        if (Input.IsKeyDown(Keys.KeyPad3)) {
            RoughnessValue -= 0.01f;
            RoughnessValue = Math.Clamp(RoughnessValue, 0f, 2f);
        }

        if (Input.IsKeyPressed(Keys.KeyPad5)) {
            renderMode++;
            if (renderMode > 6)
                renderMode = 0;
        }

        if (Input.IsKeyPressed(Keys.P)) {
            fogEnabled = !fogEnabled;
            Console.WriteLine(fogEnabled);
        }
    }

    bool fogEnabled = false;

    public override void PostRenderCleanUp() {
        foreach (IStaticMeshRenderer opaqueDrawable in RuntimeSet<IStaticMeshRenderer>.ReadOnlyCollection)
            opaqueDrawable.RenderedThisFrame = false;
    }


    void ClearColorBuffer() {
        GL.ClearColor(0.4f, 0.55f, 0.65f, 1.0f);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit |
                 ClearBufferMask.StencilBufferBit);
    }
}
