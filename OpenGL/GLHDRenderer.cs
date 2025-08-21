using System.Buffers;
using BallisticEngine.Rendering;
using BallisticEngine.Sky;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace BallisticEngine;

public class GLHDRenderer : HDRenderer {
    IWindow window;
    bool anythingDrawnThisFrame;
    SkyboxRenderer skyboxRenderer;
    GLFrameBuffer frameBuffer;
    StandardShader standardShader;
    GLShadowMap shadowMap;

    public override void Initialize() {
        skyboxRenderer = new SkyboxRenderer();
        skyboxRenderer.init();
        window = Window.Current;
        frameBuffer = new GLFrameBuffer(window.Width, window.Height);
        shadowMap = new GLShadowMap(window.Width, window.Height);

        window.OnResizeCallback += (x, y) => frameBuffer.Resize(x, y);
        const string stdVert = @"
#version 330 core
layout(location = 0) in vec3 position;

uniform mat4 model;
uniform mat4 lightSpaceMatrix;

void main() {
    gl_Position = lightSpaceMatrix * model * vec4(position, 1.0);
}
";
        const string stdFrag = @"
#version 330 core
void main() {
}
";
        standardShader = GraphicAPI.CreateStandardShader(stdVert, stdFrag);
    }

    public float Metallic = 0.2f;
    public float RoughnessValue = .5f;
    int renderMode = 0;

    public override void RenderOpaque(IReadOnlyCollection<IStaticMeshRenderer> renderTargets, RendererArgs args,
        bool isShadowPass) {
        if (isShadowPass) {
            shadowMap.Bind();
            GL.Viewport(0, 0, shadowMap.Width, shadowMap.Height);
            GL.Clear(ClearBufferMask.DepthBufferBit);

            GL.ColorMask(false, false, false, false);
            GL.DepthMask(true);
            standardShader.Activate();
        }

        GL.Enable(EnableCap.DepthTest);
        GL.CullFace(TriangleFace.Back);
        GL.Enable(EnableCap.CullFace);
        GL.FrontFace(FrontFaceDirection.Ccw);

        Matrix4 view = args.viewProjectionProvider.GetViewMatrix();
        Matrix4 projection = args.viewProjectionProvider.GetProjectionMatrix();
        Matrix4 lightSpaceMatrix = DirectionalLight.Instance.GetLightSpaceMatrix();
        if (isShadowPass) {
            shadowMap.Bind();
            standardShader.Activate();
        }

        foreach (IStaticMeshRenderer target in renderTargets) {
            Mesh mesh = target.SharedMesh;
            Shader shader = target.SharedMaterial.Shader;
            Matrix4 WorldMatrix = target.Transform.WorldMatrix;
            target.Activate();

            if (isShadowPass) {
                standardShader.SetMatrix4("model", ref WorldMatrix);
                standardShader.SetMatrix4("lightSpaceMatrix", ref lightSpaceMatrix);
            }
            else {
                SetUniformsForLitRender(args, shader, args.viewProjectionProvider.GetViewMatrix(), WorldMatrix, ref projection);
            }

            GL.DrawElements(PrimitiveType.Triangles, mesh.Indices.Length, DrawElementsType.UnsignedInt, 0);
            target.Deactivate();
        }

        if (isShadowPass) {
            GL.ColorMask(true, true, true, true);
            shadowMap.Unbind();
        }
    }

    void SetUniformsForLitRender(RendererArgs args, Shader shader, Matrix4 view, Matrix4 WorldMatrix,
        ref Matrix4 projection) {
        shader.SetFloat3("LightPos", -DirectionalLight.Instance.transform.Forward);
        shader.SetFloat3("LightColor",
            DirectionalLight.Instance.LightIntensity * skyboxRenderer.cubemapTexture.skyAmbient);
        shader.SetFloat3("AmbientLight",
            DirectionalLight.Instance.ambientIntensity * skyboxRenderer.cubemapTexture.skyAmbient);
        shader.SetFloat("MetallicMultiplier", Metallic);
        shader.SetFloat("SmoothnessMultiplier", RoughnessValue);
        shader.SetFloat("rimPower", RimPower);

        Matrix4 lightSpaceMatrix = DirectionalLight.Instance.GetLightSpaceMatrix();
        shader.SetMatrix4("lightSpaceMatrix", ref lightSpaceMatrix);
        GL.ActiveTexture(TextureUnit.Texture10);
        GL.BindTexture(TextureTarget.Texture2D, shadowMap.DepthTextureId);
        shader.SetInt("Diffuse", 0);
        shader.SetInt("Normal", 1);
        shader.SetInt("Metallic", 2);
        shader.SetInt("Roughness", 3);
        shader.SetInt("ShadowMap", 10);
        shader.SetInt("Skybox", 11);
        shader.SetInt("AO", 4);
        shader.SetInt("renderMode", renderMode);
        shader.SetFloat("NormalStrength", NormalStrength);
        shader.SetBool("EnableAtmosphericScattering", fogEnabled);
        shader.SetMatrix4("view", ref view);
        shader.SetMatrix4("projection", ref projection);
        shader.SetMatrix4("model", ref WorldMatrix);
        shader.SetFloat3("CameraPos", args.viewProjectionProvider.Transform.Position);
    }

    public float NormalStrength { get; set; } = 1f;
    public float RimPower { get; set; } = 3f;

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
        Shader shader = target.SharedMaterial.Shader;
        target.Activate();


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

    public override RenderMetrics BeginRender(RendererArgs args) {
        ClearColorBuffer();
        RenderOpaque(RuntimeSet<IStaticMeshRenderer>.ReadOnlyCollection, args, true);
        if (RenderAsset.Current.InstancedDrawing) {
            // Disabled at the moment
            foreach (BatchGroup<IStaticMeshRenderer> batchGroup in
                     RendererHelpers.CreateBatchGroupsForOpaqueDrawables())
                RenderInstancing(batchGroup, args);
        }

        frameBuffer.Activate();
        ClearColorBuffer();
        GL.Viewport(0, 0, frameBuffer.LenX, frameBuffer.LenY);

        RenderOpaque(RuntimeSet<IStaticMeshRenderer>.ReadOnlyCollection, args, false);

        DebugCheck();
        skyboxRenderer.RotUpdate();
        skyboxRenderer.PreRenderCallback(args);
        skyboxRenderer.RenderSkybox();
        skyboxRenderer.PostRenderCallback(args);

        frameBuffer.DrawBufferToScreen();
        return new RenderMetrics();
    }

    void DebugCheck() {
        if (Input.IsKeyDown(Keys.KeyPad0)) {
            Metallic += 0.002f;
            Metallic = Math.Clamp(Metallic, 0f, 1f);
        }

        if (Input.IsKeyDown(Keys.KeyPad1)) {
            Metallic -= 0.002f;
            Metallic = Math.Clamp(Metallic, 0f, 1f);
        }

        if (Input.IsKeyDown(Keys.KeyPad2)) {
            RoughnessValue += 0.002f;
            RoughnessValue = Math.Clamp(RoughnessValue, 0f, 1f);
        }

        if (Input.IsKeyDown(Keys.KeyPad9)) {
            NormalStrength += 0.002f;
        }

        if (Input.IsKeyDown(Keys.KeyPad8)) {
            NormalStrength -= 0.002f;
        }

        if (Input.IsKeyDown(Keys.KeyPad3)) {
            RoughnessValue -= 0.02f;
            RoughnessValue = Math.Clamp(RoughnessValue, 0f, 1f);
        }

        if (Input.IsKeyDown(Keys.KeyPadDivide)) {
            RimPower -= 0.01f;
        }

        if (Input.IsKeyDown(Keys.KeyPadMultiply)) {
            RimPower += 0.01f;
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

    bool fogEnabled = true;

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