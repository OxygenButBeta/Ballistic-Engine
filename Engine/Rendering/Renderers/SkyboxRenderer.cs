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
        cubemapVertexBuffer.SetBufferData(in skyboxVertices, BufferUsageHint.StaticDraw);
        skyboxShader = GraphicAPI.CreateStandardShader(skyboxVertexShader, skyboxFragmentShader);
    }

    public void RenderSkybox() {
        cubemapVertexBuffer.Activate();
        GL.DrawArrays(PrimitiveType.Triangles, 0, 36);
    }

    Matrix4 rotationMatrix = Matrix4.Identity;

    public void PreRenderCallback(RendererArgs args) {
        renderContext.Activate();
        cubemapTexture.Activate();
        skyboxShader.Activate();

        // Orientation and exposure come from the scene's Skybox component.
        Skybox sky = Skybox.Active;
        var exposure = sky?.Exposure ?? 1f;
        Vector3 euler = sky?.RotationEuler ?? Vector3.Zero;
        rotationMatrix =
            Matrix4.CreateRotationX(MathHelper.DegreesToRadians(euler.X)) *
            Matrix4.CreateRotationY(MathHelper.DegreesToRadians(euler.Y)) *
            Matrix4.CreateRotationZ(MathHelper.DegreesToRadians(euler.Z));

        skyboxShader.SetMatrix4("rotation", ref rotationMatrix);
        skyboxShader.SetFloat("exposure", exposure);
        var matr = new Matrix4(new Matrix3(args.viewProjectionProvider.GetViewMatrix()));
        var projection = args.viewProjectionProvider.GetProjectionMatrix();
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

    public string skyboxVertexShader = @"#version 330 core
layout (location = 0) in vec3 aPos;

out vec3 TexCoords;

uniform mat4 projection;
uniform mat4 view;
uniform mat4 rotation;
void main()
{
      mat4 rotView = mat4(mat3(view)) * rotation; 
    vec4 pos = projection * rotView * vec4(aPos, 1.0);
    gl_Position = pos.xyww;
    TexCoords = aPos;
}";

    public string skyboxFragmentShader = @"#version 330 core
out vec4 FragColor;
in vec3 TexCoords;

uniform samplerCube skybox;
uniform float exposure;

void main()
{
    vec4 sky = texture(skybox, TexCoords);
    FragColor = vec4(sky.rgb * exposure, sky.a);
}
";
}