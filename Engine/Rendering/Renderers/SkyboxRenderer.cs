using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace BallisticEngine.Sky;

public class SkyboxRenderer : ISkyboxDrawable {
    public Transform Transform { get; }
    public bool RenderedThisFrame { get; set; }
    public bool AtmosphereScattering { get; private set; }

    readonly Vector3[] skyboxVertices = {
        // Back face (+Z)
        new Vector3(-1, -1, 1),
        new Vector3(1, -1, 1),
        new Vector3(1, 1, 1),
        new Vector3(1, 1, 1),
        new Vector3(-1, 1, 1),
        new Vector3(-1, -1, 1),

        // Front face (-Z)
        new Vector3(-1, -1, -1),
        new Vector3(-1, 1, -1),
        new Vector3(1, 1, -1),
        new Vector3(1, 1, -1),
        new Vector3(1, -1, -1),
        new Vector3(-1, -1, -1),

        // Left face (-X)
        new Vector3(-1, -1, -1),
        new Vector3(-1, -1, 1),
        new Vector3(-1, 1, 1),
        new Vector3(-1, 1, 1),
        new Vector3(-1, 1, -1),
        new Vector3(-1, -1, -1),

        // Right face (+X)
        new Vector3(1, -1, -1),
        new Vector3(1, 1, -1),
        new Vector3(1, 1, 1),
        new Vector3(1, 1, 1),
        new Vector3(1, -1, 1),
        new Vector3(1, -1, -1),

        // Top face (+Y)
        new Vector3(-1, 1, -1),
        new Vector3(-1, 1, 1),
        new Vector3(1, 1, 1),
        new Vector3(1, 1, 1),
        new Vector3(1, 1, -1),
        new Vector3(-1, 1, -1),

        // Bottom face (-Y)
        new Vector3(-1, -1, -1),
        new Vector3(1, -1, -1),
        new Vector3(1, -1, 1),
        new Vector3(1, -1, 1),
        new Vector3(-1, -1, 1),
        new Vector3(-1, -1, -1)
    };


    RenderContext renderContext;
    GPUBuffer<Vector3> cubemapVertexBuffer;
    Shader skyboxShader;
    public Texture3D cubemapTexture;

    public void init() {
        renderContext = GraphicAPI.CreateRenderContext();
        renderContext.Activate();

        // The cubemap comes from the scene's Skybox component (set per frame by the renderer).


        cubemapVertexBuffer = GraphicAPI.CreateVertexBuffer3(renderContext);
        cubemapVertexBuffer.Create();
        cubemapVertexBuffer.SetBufferData(in skyboxVertices, BufferUsage.StaticDraw);
        skyboxShader = GraphicAPI.CreateStandardShader(
            EmbeddedShaderSource.Read("Skybox_Vert.glsl"),
            EmbeddedShaderSource.Read("Skybox_Frag.glsl"));
    }

    public void RenderSkybox() {
        cubemapVertexBuffer.Activate();
        GL.DrawArrays(PrimitiveType.Triangles, 0, 36);
    }

    Matrix4 rotationMatrix = Matrix4.Identity;

    // Set per frame by the renderer so the sky uses the same (jittered) projection as the
    // geometry; without it TAA sees the sky and meshes jittering differently at silhouettes.
    public Matrix4? ProjectionOverride;

    // Pre-exposure (Frostbite-style): the camera's EV exposure multiplier, applied at the
    // SOURCE so the fp16 HDR buffer stays in a sane ~0-10 range instead of raw physical
    // luminance (which overflows fp16 and starves every bounded post effect). Set per frame
    // by the renderer; the composite then tonemaps with exposure 1.
    public float PreExposure = 1f;

    // Set by the renderer while a procedural sky drives the cubemap: its exposure is baked
    // into the texels and it is sun-oriented, so the Skybox component's exposure/rotation
    // must NOT apply on top.
    public bool NeutralSky;

    public void PreRenderCallback(RendererArgs args) {
        renderContext.Activate();
        cubemapTexture.Activate();
        skyboxShader.Activate();

        // Orientation and exposure come from the scene's Skybox component.
        Skybox sky = Skybox.Active;
        var exposure = NeutralSky ? 1f : sky?.Exposure ?? 1f;
        Vector3 euler = NeutralSky ? Vector3.Zero : sky?.RotationEuler ?? Vector3.Zero;
        rotationMatrix =
            Matrix4.CreateRotationX(MathHelper.DegreesToRadians(euler.X)) *
            Matrix4.CreateRotationY(MathHelper.DegreesToRadians(euler.Y)) *
            Matrix4.CreateRotationZ(MathHelper.DegreesToRadians(euler.Z));

        skyboxShader.SetMatrix4("rotation", ref rotationMatrix);
        skyboxShader.SetFloat("exposure", exposure * PreExposure);
        var matr = new Matrix4(new Matrix3(args.viewProjectionProvider.GetViewMatrix()));
        Matrix4 projection = ProjectionOverride ?? args.viewProjectionProvider.GetProjectionMatrix();
        skyboxShader.SetMatrix4("view", ref matr);
        skyboxShader.SetMatrix4("projection", ref projection);
        skyboxShader.SetInt("skybox", 11);
        GL.DepthFunc(DepthFunction.Lequal);
        GL.DepthMask(false);
        GL.Disable(EnableCap.CullFace);
    }

    public void PostRenderCallback(RendererArgs args) {
        GL.DepthMask(true);
        GL.DepthFunc(DepthFunction.Less);
        GL.Enable(EnableCap.CullFace);
    }

}