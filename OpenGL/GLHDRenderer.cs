using System.Buffers;
using BallisticEngine.Rendering;
using BallisticEngine.Sky;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace BallisticEngine;

public class GLHDRenderer : HDRenderer {
    // 4 x 2048 cascades: same memory as one 4096 map, but cascade 0 covers metres instead of
    // the whole frustum, so near shadows get real texel density.
    const int ShadowMapSize = 2048;
    const int CascadeCount = 4;
    const int MaxPointLights = 8;
    const int MaxSpotLights = 4;

    // Punctual shadows: one 512x16 depth array; spots take layers 0..3, each shadowed point
    // light takes 6 cube-face layers after that.
    const int MaxShadowedSpots = 4;
    const int MaxShadowedPoints = 2;
    const int PunctualShadowSize = 512;

    IWindow window;
    bool anythingDrawnThisFrame;
    SkyboxRenderer skyboxRenderer;
    GLFrameBuffer frameBuffer;     // Scene view (editor camera) / player present target — HDR
    GLFrameBuffer gameBuffer;      // Game view (scene camera) — editor only, HDR
    GLFrameBuffer sceneDisplay;    // post-processed (tonemapped) output the editor panels sample
    GLFrameBuffer gameDisplay;
    StandardShader shadowDepthShader;
    GLShadowMap shadowMap;
    GLShadowMap punctualShadows;
    GLCompositePass composite;
    GLBloomPass bloom;
    GLSSAOPass ssao;
    GLSSGIPass ssgi;
    GLSSRPass ssr;
    GLVolumetricLightPass volumetric;
    GLTAAPass taa;
    GLDepthOfFieldPass depthOfField;
    GLAutoExposurePass autoExposure;
    readonly GLProbeDebugPass probeDebug = new();
    readonly GLProceduralSkyPass proceduralSkyPass = new();
    // Sky luminance scale of the ACTIVE sky source: the Skybox component's Exposure for HDRI
    // skies, 1 for the procedural sky (its Exposure is baked into the cubemap texels).
    float skyExposureBase = 1f;

    // TAA bookkeeping: jitter phase and last frame's unjittered view-projection per target.
    int taaFrameIndex;
    readonly Matrix4[] prevViewProjection = new Matrix4[2];
    readonly bool[] prevViewProjectionValid = new bool[2];

    // IBL resources. The BRDF LUT is baked once; irradiance + prefiltered specular are
    // rebaked whenever the active skybox cubemap changes.
    int brdfLut;
    int irradianceMap;
    int prefilteredMap;
    Texture3D iblSource;

    // Baked irradiance probe volume: four 3D textures holding the L1-SH coefficients of the
    // irradiance at each grid probe (c0 + linear y/z/x). Filled by BakeProbeVolume; sampled by
    // the PBR shader with trilinear filtering as position-aware ambient. SH values are stored
    // UN-exposed (physical) and re-exposed at sample time, so changing EV doesn't stale them.
    readonly int[] probeSH = new int[4];
    bool probeVolumeReady;
    Vector3 probeVolumeMin, probeVolumeInvSize;

    // Baked reflection probe volume (ReflectionVolume): a cube-map array of GGX-prefiltered local
    // reflections, one layer per OCCUPIED grid cell, plus an R32I 3D texture mapping each cell to
    // its layer (or -1 for empty-air cells, which fall back to the global skybox). Like the diffuse
    // SH above, cubes store PHYSICAL radiance and are re-exposed (x SkyExposure) at sample time.
    int reflectionArray;          // GL_TEXTURE_CUBE_MAP_ARRAY
    int reflectionCellToLayer;    // R32I sampler3D: cell -> layer index, or -1
    bool reflectionVolumeReady;
    Vector3 reflectionVolumeMin, reflectionVolumeInvSize;
    int reflectionGridX, reflectionGridY, reflectionGridZ;

    // Cascaded sun shadows: world->light-clip matrix, depth range (for bias conversion) per
    // cascade, recomputed every frame from the camera + sun. Layout (count, distance, split
    // shape, blend, resolution) comes from the Shadows volume component via PostFX.
    readonly Matrix4[] cascadeMatrices = new Matrix4[CascadeCount];
    readonly float[] cascadeDepthRanges = new float[CascadeCount];
    Vector4 cascadeBias;     // compare-space slope bias per cascade
    int activeCascadeCount = CascadeCount;
    float cascadeBlend = 0.15f;
    readonly float[] cascadeRadii = new float[CascadeCount];
    Vector4 cascadeTexelWorld;  // world units per shadow texel, per cascade (PCSS penumbra->texels)
    Vector4 cascadeDepthWorld;  // world units per compare-space depth unit, per cascade
    // PCSS blocker search reads the cascade array WITHOUT depth compare: a GL sampler object
    // on a second texture unit overrides the texture's compare state just for that unit.
    int shadowRawSampler;
    static readonly string[] CascadeMatrixNames = BuildIndexedNames("CascadeMatrices", CascadeCount);

    Matrix4 skyRotation = Matrix4.Identity;

    // Scenes without a SceneLighting component fall back to these. Physical: 1.0. The old 0.6
    // haircut compensated for a double-applied specular-occlusion term; with that fixed (single
    // Lagarde term + multiscatter energy conservation in the shader) reflections run at full
    // energy. Matches SceneLighting's default.
    const float DefaultReflectionIntensity = 1f;

    // Per-frame scene lighting environment (from SceneLighting.Active, or neutral defaults).
    Vector3 ambientTint = Vector3.One;
    float reflectionIntensity = DefaultReflectionIntensity;
    Vector3 shadowColor = Vector3.Zero;
    float shadowStrength = 1f;
    bool sceneFogEnabled;
    Vector3 sceneFogColor = new(0.6f, 0.7f, 0.9f);
    float sceneFogDensity = 0.0015f;

    // Per-frame resolved sun (from DirectionalLight.Instance, fallback, or zero when the
    // scene has no light but is IBL-lit).
    Vector3 sunDirection = Vector3.UnitY;
    Vector3 sunColor = Vector3.Zero;
    Vector3 ambientFallback = Vector3.Zero;

    GLFrameBuffer CurrentTarget => ActiveTarget == RenderTarget.Game ? gameBuffer : frameBuffer;
    GLFrameBuffer CurrentDisplay => ActiveTarget == RenderTarget.Game ? gameDisplay : sceneDisplay;

    public override void Initialize() {
        skyboxRenderer = new SkyboxRenderer();
        skyboxRenderer.init();
        window = Window.Current;
        frameBuffer = new GLFrameBuffer(window.Width, window.Height, depthAsTexture: true,
            withNormalAttachment: true);
        gameBuffer = new GLFrameBuffer(window.Width, window.Height, depthAsTexture: true,
            withNormalAttachment: true);
        sceneDisplay = new GLFrameBuffer(window.Width, window.Height);
        gameDisplay = new GLFrameBuffer(window.Width, window.Height);
        shadowMap = new GLShadowMap(ShadowMapSize, ShadowMapSize, CascadeCount);
        punctualShadows = new GLShadowMap(PunctualShadowSize, PunctualShadowSize,
            MaxShadowedSpots + MaxShadowedPoints * 6);
        composite = new GLCompositePass();
        bloom = new GLBloomPass();
        ssao = new GLSSAOPass();
        ssgi = new GLSSGIPass();
        ssr = new GLSSRPass();
        volumetric = new GLVolumetricLightPass();
        taa = new GLTAAPass();
        depthOfField = new GLDepthOfFieldPass();
        autoExposure = new GLAutoExposurePass();
        brdfLut = GLEnvironmentMaps.GenerateBrdfLut();

        // Non-compare sampler for PCSS blocker-depth reads (unit 19). Border = 1 (far plane)
        // so searches past the cascade edge find no blockers.
        shadowRawSampler = GL.GenSampler();
        GL.SamplerParameter(shadowRawSampler, SamplerParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.SamplerParameter(shadowRawSampler, SamplerParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        GL.SamplerParameter(shadowRawSampler, SamplerParameterName.TextureWrapS, (int)TextureWrapMode.ClampToBorder);
        GL.SamplerParameter(shadowRawSampler, SamplerParameterName.TextureWrapT, (int)TextureWrapMode.ClampToBorder);
        GL.SamplerParameter(shadowRawSampler, SamplerParameterName.TextureBorderColor, new[] { 1f, 1f, 1f, 1f });

        // Track the window size only when presenting to it (player). In the editor the
        // panels own the target sizes via ResizeSceneTarget/ResizeGameTarget.
        window.OnResizeCallback += (x, y) => {
            if (PresentToScreen)
                frameBuffer.Resize(x, y);
        };
        const string shadowVert = @"
#version 330 core
layout(location = 0) in vec3 position;
layout(location = 1) in vec2 aTexCoord;

out vec2 uv;

uniform mat4 model;
uniform mat4 lightSpaceMatrix;

void main() {
    uv = aTexCoord;
    gl_Position = lightSpaceMatrix * model * vec4(position, 1.0);
}
";
        const string shadowFrag = @"
#version 330 core
in vec2 uv;

uniform bool AlphaCutout;
uniform sampler2D Diffuse;

void main() {
    if (AlphaCutout && texture(Diffuse, uv).a < 0.5)
        discard;
}
";
        shadowDepthShader = GraphicAPI.CreateStandardShader(shadowVert, shadowFrag);
    }

    public float Metallic = 1f;
    public float RoughnessValue = 1f;
    int renderMode = 0;

    // Per-frame working sets, split by material blend mode, with the world AABB computed once
    // per renderer per frame. The FULL lists feed shadow casting, the light stamp and the bakes
    // (an off-screen mesh still casts a visible shadow); the `visible*` lists are the camera-
    // frustum survivors the main passes actually draw.
    struct DrawItem {
        public IStaticMeshRenderer R;
        public Vector3 AabbMin, AabbMax;
    }

    static readonly List<DrawItem> opaqueItems = new();
    static readonly List<DrawItem> transparentItems = new();
    static readonly List<IStaticMeshRenderer> visibleOpaque = new();
    static readonly List<IStaticMeshRenderer> visibleTransparent = new();
    static readonly List<(ulong Key, IStaticMeshRenderer R)> opaqueSortScratch = new();
    // Scratch for per-cascade / per-face shadow-caster culling (single-threaded use only).
    static readonly List<IStaticMeshRenderer> cullScratch = new();
    static readonly Vector4[] cullPlanes = new Vector4[6];

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

    // Punctual shadow bookkeeping: per-light slot (-1 = unshadowed), per-slot matrices/bias.
    readonly int[] spotShadowSlots = new int[MaxSpotLights];
    readonly Matrix4[] spotShadowMatrices = new Matrix4[MaxShadowedSpots];
    readonly float[] spotShadowBiases = new float[MaxShadowedSpots];
    int shadowedSpotCount;
    readonly int[] pointShadowSlots = new int[MaxPointLights];
    readonly Matrix4[] pointShadowMatrices = new Matrix4[MaxShadowedPoints * 6];
    readonly float[] pointShadowBiases = new float[MaxShadowedPoints];
    int shadowedPointCount;
    // Tiles re-render only when lights or geometry actually changed (static scene = free).
    int punctualShadowStamp;
    bool punctualShadowsDirty;

    // Scene-geometry fingerprint (world AABBs of all opaque casters), rebuilt per frame by
    // SplitRenderables. AABBs catch moves, rotations and scales; the blind spot is an object
    // whose AABB is rotation-symmetric — whose shadow silhouette barely changes anyway.
    int geometryStamp;

    // Sun cascades re-render only when their texel-snapped fit OR the geometry changed. The
    // snap in ShadowMath quantizes the matrix, so a static/slow camera skips most renders —
    // and far cascades (bigger texels) skip far more often than near ones.
    readonly Matrix4[] cascadeCachedMatrix = new Matrix4[CascadeCount];
    readonly int[] cascadeCachedGeometry = new int[CascadeCount];
    readonly bool[] cascadeValid = new bool[CascadeCount];

    // Screen-space AO for the CURRENT forward pass (0 = none): computed from the depth
    // prepass before shading so occlusion lands in the ambient terms, not on final color.
    int screenAoTexture;
    Vector2 screenAoTargetSize;

    // A standalone copy of the prepass depth, blitted after the prepass so contact shadows can
    // SAMPLE depth during the opaque pass without a feedback loop (the live depth attachment is
    // bound and read-only-tested then, so sampling it directly is GL-undefined).
    int prepassDepthCopy;
    int prepassDepthCopyFbo;
    int prepassDepthCopyW, prepassDepthCopyH;

    void EnsurePrepassDepthCopy(int w, int h) {
        if (prepassDepthCopy != 0 && w == prepassDepthCopyW && h == prepassDepthCopyH)
            return;
        if (prepassDepthCopy != 0)
            GL.DeleteTexture(prepassDepthCopy);
        if (prepassDepthCopyFbo == 0)
            prepassDepthCopyFbo = GL.GenFramebuffer();
        prepassDepthCopyW = w;
        prepassDepthCopyH = h;
        prepassDepthCopy = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, prepassDepthCopy);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.DepthComponent24, w, h, 0,
            PixelFormat.DepthComponent, PixelType.Float, IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, prepassDepthCopyFbo);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
            TextureTarget.Texture2D, prepassDepthCopy, 0);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    // Blit the just-rendered prepass depth from `target` into the standalone copy.
    void CopyPrepassDepth(GLFrameBuffer target) {
        EnsurePrepassDepthCopy(target.LenX, target.LenY);
        GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, target.FrameBufferId);
        GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, prepassDepthCopyFbo);
        GL.BlitFramebuffer(0, 0, target.LenX, target.LenY, 0, 0, target.LenX, target.LenY,
            ClearBufferMask.DepthBufferBit, BlitFramebufferFilter.Nearest);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    static readonly string[] PointPositionNames = BuildIndexedNames("PointLightPosition", MaxPointLights);
    static readonly string[] PointColorNames = BuildIndexedNames("PointLightColor", MaxPointLights);
    static readonly string[] PointRangeNames = BuildIndexedNames("PointLightRange", MaxPointLights);
    static readonly string[] SpotPositionNames = BuildIndexedNames("SpotLightPosition", MaxSpotLights);
    static readonly string[] SpotDirectionNames = BuildIndexedNames("SpotLightDirection", MaxSpotLights);
    static readonly string[] SpotColorNames = BuildIndexedNames("SpotLightColor", MaxSpotLights);
    static readonly string[] SpotRangeNames = BuildIndexedNames("SpotLightRange", MaxSpotLights);
    static readonly string[] SpotCosInnerNames = BuildIndexedNames("SpotLightCosInner", MaxSpotLights);
    static readonly string[] SpotCosOuterNames = BuildIndexedNames("SpotLightCosOuter", MaxSpotLights);
    static readonly string[] SpotShadowSlotNames = BuildIndexedNames("SpotShadowSlot", MaxSpotLights);
    static readonly string[] SpotShadowMatrixNames = BuildIndexedNames("SpotShadowMatrix", MaxShadowedSpots);
    static readonly string[] SpotShadowBiasNames = BuildIndexedNames("SpotShadowBias", MaxShadowedSpots);
    static readonly string[] PointShadowSlotNames = BuildIndexedNames("PointShadowSlot", MaxPointLights);
    static readonly string[] PointShadowMatrixNames =
        BuildIndexedNames("PointShadowMatrix", MaxShadowedPoints * 6);
    static readonly string[] PointShadowBiasNames = BuildIndexedNames("PointShadowBias", MaxShadowedPoints);

    // Cube-face bases for point-light shadows; order MUST match the shader's CubeFace():
    // +X, -X, +Y, -Y, +Z, -Z.
    static readonly Vector3[] CubeFaceForward =
        { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ };
    static readonly Vector3[] CubeFaceUp =
        { Vector3.UnitY, Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ, Vector3.UnitY, Vector3.UnitY };

    static string[] BuildIndexedNames(string baseName, int count) {
        var names = new string[count];
        for (var i = 0; i < count; i++)
            names[i] = $"{baseName}[{i}]";
        return names;
    }

    // Per-target GPU pass timers + live stats (Scene = 0, Game = 1); `stats` aliases the
    // active target's instance for the duration of one BeginRender.
    readonly GLGpuTimers[] gpuTimers = { new(), new() };
    RenderStats stats = RenderStats.Scene;

    // All pass-constant shader data lives in ONE std140 block (binding 0), filled once per
    // pass and shared by every lit program — the old path re-sent ~150 glUniforms per shader
    // per pass. Programs without the block (user shaders predating it) fall back to the
    // legacy per-uniform upload. Sampler unit assignments are set once per program ever.
    readonly GLUniformBlock passData = new("PassData", 0);
    readonly HashSet<int> samplerReadyPrograms = new();
    bool passDataFilled; // reset per RenderMeshes call; first registered program fills

    public override RenderMetrics BeginRender(RendererArgs args) {
        using var profileZone = Profiler.Zone("HD.BeginRender");

        var targetIndex = ActiveTarget == RenderTarget.Game ? 1 : 0;
        GLGpuTimers timers = gpuTimers[targetIndex];
        stats = targetIndex == 1 ? RenderStats.Game : RenderStats.Scene;
        stats.ResetSubmission();
        timers.BeginFrame();

        // The scene's Skybox component drives the sky (null = no sky, default ambient).
        skyboxRenderer.cubemapTexture = Skybox.Active is { IsActive: true } sky ? sky.Cubemap : null;

        // ProceduralSky takes precedence while active: bake (sun- or parameter-changes only)
        // and substitute its cubemap, so the sky, IBL and ambient all follow the sun.
        var proceduralActive = false;
        if (ProceduralSky.Active is { IsActive: true } proceduralSky) {
            LightUniforms skySun = LightUniforms.Resolve();
            proceduralSkyPass.EnsureBaked(proceduralSky, skySun.Direction, skySun.Color,
                MathHelper.DegreesToRadians(DirectionalLight.Instance?.AngularDiameter ?? 0.53f) * 0.5f);
            skyboxRenderer.cubemapTexture = proceduralSkyPass.Cubemap;
            proceduralActive = true;
        }
        skyboxRenderer.NeutralSky = proceduralActive;
        skyExposureBase = proceduralActive ? 1f : Skybox.Active?.Exposure ?? 1f;

        // Volume framework: blend every active Volume (global + camera-local) into the stack,
        // then drive the live PostFX settings from the result. No volumes = engine defaults.
        // WORLD position, not local: a PlayerController-spawned camera is PARENTED (eye-height
        // child), so .Position is just the local offset near the origin.
        VolumeManager.Update(args.viewProjectionProvider.Transform.WorldPosition);
        VolumePostProcessing.Apply(VolumeManager.Stack, PostFX);
        ApplyEnvOverrides();

        // Auto exposure adapts NOW, before the first ExposureMultiplier read below: lighting
        // is pre-exposed, so this frame's EV feeds every light uniform. The target EV comes
        // from last frame's metering of this same render target (scene/game views adapt
        // independently - they look at the scene through different cameras).
        autoExposure.Adapt(ActiveTarget == RenderTarget.Game ? 1 : 0, PostFX, (float)Time.DeltaTime);

        // Scene lighting environment (ambient, reflections, shadow appearance, fog).
        if (SceneLighting.Active is { IsActive: true } lighting) {
            ambientTint = lighting.AmbientColor * lighting.AmbientIntensity;
            reflectionIntensity = MathF.Max(lighting.ReflectionIntensity, 0f);
            shadowColor = lighting.ShadowColor;
            shadowStrength = Math.Clamp(lighting.ShadowStrength, 0f, 1f);
            sceneFogEnabled = lighting.FogEnabled;
            sceneFogColor = lighting.FogColor;
            sceneFogDensity = lighting.FogDensity;
        }
        else {
            ambientTint = Vector3.One;
            reflectionIntensity = DefaultReflectionIntensity;
            shadowColor = Vector3.Zero;
            shadowStrength = 1f;
            sceneFogEnabled = fogEnabled; // legacy debug toggle (P key)
            sceneFogColor = new Vector3(0.6f, 0.7f, 0.9f);
            sceneFogDensity = 0.0015f;
        }

        UpdateEnvironmentMaps();
        UpdateSkyRotation();

        Matrix4 view = args.viewProjectionProvider.GetViewMatrix();
        Matrix4 projection = args.viewProjectionProvider.GetProjectionMatrix();
        // WORLD position (parented spawned cameras!): this feeds the shader's CameraPos —
        // every fresnel/specular/IBL view vector — plus the volumetric ray origin and the
        // transparent sort. The view matrix is already world-space; using the LOCAL position
        // here silently flattened/darkened all view-dependent lighting for parented cameras.
        Vector3 cameraPos = args.viewProjectionProvider.Transform.WorldPosition;

        // Cull with the unjittered camera frustum (TAA jitter is sub-pixel; the AABB test
        // doesn't care). Shadow casters keep the full list — they're culled per light below.
        Matrix4 cullViewProjection = view * projection;
        SplitRenderables(cameraPos, ref cullViewProjection);
        GatherPunctualLights();

        // PRE-EXPOSED LIGHTING (Frostbite-style). Lights are authored in physical units (sun
        // ~80000 lux), but raw physical radiance overflows the RGBA16F buffer (fp16 max ~65504
        // -> Inf/NaN) and dwarfs every bounded post effect (SSGI's clamp-8 bounce, volumetric's
        // ~2.5 scatter, the composite's luma-based AO gate - all calibrated for a ~0-10 range).
        // So the camera's EV exposure is applied at the SOURCE: every light uniform (sun,
        // punctual, sky, ambient fallback) is multiplied by ExposureMultiplier before shading,
        // the buffer stays in a sane range, and the composite tonemaps with exposure 1.
        float preExposure = PostFX.ExposureMultiplier;
        skyboxRenderer.PreExposure = preExposure;

        LightUniforms light = LightUniforms.Resolve();
        sunDirection = light.Direction;
        sunColor = light.Color * preExposure;
        ambientFallback = light.AmbientIntensity * skyExposureBase * preExposure *
                          (skyboxRenderer.cubemapTexture?.skyAmbient ?? Vector3.One * 0.5f);


        // No scene light + IBL available = the sky alone lights the scene. The built-in
        // fallback sun only applies when there is no skybox either, so empty scenes stay visible.
        if (DirectionalLight.Instance is null && irradianceMap != 0)
            sunColor = Vector3.Zero;

        screenAoTexture = 0; // probe captures shade without screen AO (wrong camera)

        // Irradiance probe bake: time-sliced (a few ms per frame, so the editor keeps painting
        // and the busy overlay shows progress) with a VOLUME-fitted shadow map, so captures
        // don't depend on where the editor camera happens to be mid-bake. Cached results load
        // instead of re-baking. Runs BEFORE the view cascade fit below, which then rebuilds
        // every cascade field the bake borrowed.
        using (probeBake is not null ? timers.Time("ProbeBake") : default)
            StepProbeBake(preExposure);
        // Local reflection probe bake: same time-sliced, volume-fitted-shadow machinery as the
        // diffuse bake, but it keeps each captured cubemap on the GPU and GGX-prefilters it into a
        // cube-array slice (no CPU readback / SH integration). Runs after the diffuse bake so the
        // two share one frame's shadow override; the cascade fit below rebuilds the borrowed fields.
        using (reflectionBake is not null ? timers.Time("ReflectionBake") : default)
            StepReflectionBake(preExposure);

        // Cascade layout from the Shadows volume (stack defaults when no volume overrides).
        var shadowDistance = MathF.Max(PostFX.ShadowMaxDistance, 1f);
        activeCascadeCount = Math.Clamp(PostFX.ShadowCascadeCount, 1, CascadeCount);
        cascadeBlend = Math.Clamp(PostFX.ShadowCascadeBlend, 0.001f, 0.5f);
        EnsureShadowResolution(PostFX.ShadowResolution);

        ShadowMath.ComputeCascades(view, projection, -sunDirection, shadowDistance, shadowMap.Width,
            cascadeMatrices.AsSpan(0, activeCascadeCount), cascadeDepthRanges.AsSpan(0, activeCascadeCount),
            PostFX.ShadowSplitDistribution, cascadeRadii.AsSpan(0, activeCascadeCount));

        // Per-cascade scale factors for PCSS: world size of one shadow texel, and world units
        // per compare-space depth unit (converts receiver-blocker gaps into metres).
        Vector4 texelWorld = Vector4.Zero;
        Vector4 depthWorld = Vector4.Zero;
        for (var i = 0; i < CascadeCount; i++) {
            var src = Math.Min(i, activeCascadeCount - 1);
            texelWorld[i] = cascadeRadii[src] * 2f / shadowMap.Width;
            depthWorld[i] = cascadeDepthRanges[src];
        }
        cascadeTexelWorld = texelWorld;
        cascadeDepthWorld = depthWorld;

        // PCSS blocker search samples the cascade depths without compare via this sampler.
        GL.BindSampler(19, shadowRawSampler);

        // ShadowBias keeps its historical meaning (compare-space at the old single 60m fit,
        // whose depth range was ~140 world units); convert to a world-space bias once, then to
        // each cascade's own compare space so near cascades don't get a 10x overscaled bias.
        // Unused slots repeat the last active value (finite, never sampled).
        var worldBias = (DirectionalLight.Instance?.ShadowBias ?? 0.0015f) * 140f;
        Vector4 bias = Vector4.Zero;
        for (var i = 0; i < CascadeCount; i++) {
            var range = cascadeDepthRanges[Math.Min(i, activeCascadeCount - 1)];
            bias[i] = worldBias / MathF.Max(range, 1e-3f);
        }
        cascadeBias = bias;

        using (timers.Time("Shadows"))
            RenderShadowPass();

        GLFrameBuffer target = CurrentTarget;

        // TAA jitters the projection used for RENDERING. CRITICAL: the depth/normal G-buffer is
        // rasterised with THIS jittered projection, so any pass that reconstructs view/world
        // position from that depth must invert the SAME jittered matrix - otherwise the
        // reconstructed position carries a sub-pixel error that ROTATES every frame (the jitter
        // sequence advances), and SSGI/SSR/TAA can never converge: the gather wobbles, history
        // never matches, reflections shimmer. (The view matrix is jitter-free; jitter only
        // shifts projection M31/M32, so passing renderProjection is sufficient.) Shadow fitting
        // still uses the unjittered light matrices - it doesn't read this depth buffer.
        Matrix4 renderProjection = projection;
        var taaActive = PostFX.TaaEnabled;
        if (taaActive) {
            Vector2 jitter = GLTAAPass.JitterOffset(taaFrameIndex++);
            renderProjection.M31 += jitter.X * 2f / target.LenX;
            renderProjection.M32 += jitter.Y * 2f / target.LenY;
        }

        target.Activate();
        GL.Viewport(0, 0, target.LenX, target.LenY);
        ClearColorBuffer();
        if (target.NormalTextureId != -1)
            GL.ClearBuffer(ClearBuffer.Color, 1, new[] { 0f, 0f, 0f, 1f }); // no normal, roughness 1

        // Z-prepass (always on): renders depth with each material's own vertex math (invariant
        // gl_Position -> bit-identical), feeds SSAO before shading, and the main pass below
        // re-tests LEqual WITHOUT depth writes — every opaque pixel runs the (expensive) lit
        // fragment shader exactly once, whatever the overdraw.
        screenAoTexture = 0;
        screenAoTargetSize = new Vector2(target.LenX, target.LenY);
        var aoTexture = 0;
        using (timers.Time("DepthPrepass"))
            RenderDepthPrepass(ref view, ref renderProjection, cameraPos);

        // Contact shadows sample depth during the opaque pass; copy it out of the live target
        // first (sampling the bound depth attachment is undefined). Only when the feature is on.
        if (PostFX.ContactShadowsEnabled)
            CopyPrepassDepth(target);

        if (PostFX.SSAOEnabled) {
            using (timers.Time("SSAO"))
                aoTexture = ssao.Render(target.DepthTextureId, target.LenX, target.LenY, renderProjection,
                    PostFX);
            screenAoTexture = aoTexture;
            target.Activate();
            GL.Viewport(0, 0, target.LenX, target.LenY);
        }

        using (timers.Time("Opaque"))
            RenderMeshes(visibleOpaque, transparentPass: false, ref view, ref renderProjection, cameraPos,
                prepassDepth: true);

        DebugCheck();
        if (skyboxRenderer.cubemapTexture is not null) {
            using var skyZone = timers.Time("Sky");
            // The sky only writes scene color; keep the cleared "no normal" in attachment 1.
            var maskNormal = target.NormalTextureId != -1;
            if (maskNormal)
                GL.ColorMask(1, false, false, false, false);
            skyboxRenderer.ProjectionOverride = renderProjection;
            skyboxRenderer.PreRenderCallback(args);
            skyboxRenderer.RenderSkybox();
            skyboxRenderer.PostRenderCallback(args);
            if (maskNormal)
                GL.ColorMask(1, true, true, true, true);
            stats.DrawCalls++;
        }

        // Probe debug spheres: opaque, depth-tested, before transparency so glass blends over
        // them and TAA antialiases them like any geometry.
        if (IrradianceVolume.Active is { IsActive: true, ShowProbes: true } && probeVolumeReady)
            probeDebug.Render(ref view, ref renderProjection, probeSH,
                probeVolumeMin, probeVolumeInvSize, probeDimX, probeDimY, probeDimZ,
                PostFX.ExposureMultiplier);

        if (visibleTransparent.Count > 0) {
            using var transparentZone = timers.Time("Transparent");
            GL.Enable(EnableCap.Blend);
            // Premultiplied alpha: the shader fades transmission with alpha but keeps
            // specular at full strength, so glass stays reflective while see-through.
            GL.BlendFunc(BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);
            GL.DepthMask(false);
            RenderMeshes(visibleTransparent, transparentPass: true, ref view, ref renderProjection, cameraPos);
            GL.DepthMask(true);
            GL.Disable(EnableCap.Blend);
        }

        // Screen-space passes reconstruct from the JITTERED depth, so they get the jittered
        // projection (see the renderProjection note above). `viewProjection` below stays
        // UNJITTERED on purpose: it's only used as TAA's previous-frame reprojection matrix,
        // and TAA resolves jitter itself, so its history reference must be jitter-free.
        Matrix4 viewProjection = view * projection;
        var litColor = target.colorBuffer;

        // SSGI first: it adds AO-occluded indirect bounce to the lit color, so the
        // reflections SSR gathers afterwards see the GI-lifted scene, not the crushed one.
        // Reconstructs from the jittered depth -> gets renderProjection (see note above).
        if (PostFX.SsgiEnabled)
            using (timers.Time("SSGI"))
                litColor = ssgi.Render(targetIndex, litColor, target.DepthTextureId, target.NormalTextureId,
                    aoTexture, target.LenX, target.LenY, ref view, ref renderProjection, ref projection,
                    prefilteredMap, ref skyRotation, skyExposureBase * preExposure, PostFX);

        if (PostFX.SsrEnabled)
            using (timers.Time("SSR"))
                litColor = ssr.Render(targetIndex, litColor, target.DepthTextureId, target.NormalTextureId,
                    target.LenX, target.LenY, ref view, ref renderProjection, PostFX);

        // Volumetric sun shafts before TAA so the temporal pass stabilizes the dithered march
        // and bloom catches the bright shafts.
        if (PostFX.VolumetricEnabled)
            using (timers.Time("Volumetric"))
                litColor = volumetric.Render(targetIndex, litColor, target.DepthTextureId,
                    shadowMap.DepthTextureId, target.LenX, target.LenY, ref view, ref projection,
                    cascadeMatrices, cascadeBias, activeCascadeCount, cameraPos, sunDirection, sunColor,
                    shadowDistance, PostFX);

        if (taaActive) {
            using var taaZone = timers.Time("TAA");
            Matrix4 invViewProjection = Matrix4.Invert(viewProjection);
            Matrix4 previousVp = prevViewProjectionValid[targetIndex]
                ? prevViewProjection[targetIndex]
                : viewProjection;
            litColor = taa.Render(targetIndex, litColor, target.DepthTextureId, target.LenX, target.LenY,
                ref invViewProjection, ref previousVp, PostFX);
        }

        prevViewProjection[targetIndex] = viewProjection;
        prevViewProjectionValid[targetIndex] = true;

        // Depth of field AFTER TAA (blurs the resolved, antialiased image — running before TAA
        // would fight the history clamp at focus edges) and BEFORE bloom (so out-of-focus
        // highlights bloom as bokeh). Uses the UNJITTERED projection to linearize depth, like
        // the volumetric pass. Deterministic: no temporal state, so it diffs clean when paused.
        if (PostFX.DofEnabled)
            using (timers.Time("DepthOfField"))
                litColor = depthOfField.Render(litColor, target.DepthTextureId, target.LenX, target.LenY,
                    ref projection, PostFX);

        // Auto exposure meters the lit, pre-bloom frame (bloom is an additive overlay; the
        // meter wants the image's own luminance) and stores a target EV for the next frame's
        // Adapt. PBO-buffered readback: no CPU/GPU sync stall, one frame of latency.
        if (PostFX.ExposureMode != ExposureMode.Fixed)
            using (timers.Time("Exposure"))
                autoExposure.Measure(targetIndex, litColor, preExposure, PostFX);

        var bloomTexture = 0;
        if (PostFX.BloomEnabled)
            using (timers.Time("Bloom"))
                bloomTexture = bloom.Render(litColor, target.LenX, target.LenY, PostFX);

        // AO was applied during shading (the prepass feeds it into the ambient terms), so the
        // composite never multiplies it in.
        using (timers.Time("Composite")) {
            if (PresentToScreen)
                composite.Render(litColor, null, target.LenX, target.LenY, PostFX, bloomTexture, 0);
            else
                composite.Render(litColor, CurrentDisplay, CurrentDisplay.LenX, CurrentDisplay.LenY, PostFX,
                    bloomTexture, 0);
        }

        // Every pool-acquired transient target has been consumed by now.
        GLRenderTexturePool.Shared.EndFrame();

        timers.EndFrame(stats);
        return new RenderMetrics(stats.DrawCalls, 0, (int)stats.Triangles,
            stats.DrawsSavedByInstancing, (float)stats.GpuFrameMs);
    }

    // Headless/agent verification: BALLISTIC_FX_<NAME>=0|1 forces a post-FX toggle after the
    // volume stack applies (e.g. BALLISTIC_FX_SSGI=0 to A/B an effect in screenshot runs).
    static readonly bool? EnvSsgi = EnvToggle("BALLISTIC_FX_SSGI");
    static readonly bool? EnvSsr = EnvToggle("BALLISTIC_FX_SSR");
    static readonly bool? EnvSsgiDebug = EnvToggle("BALLISTIC_FX_SSGI_DEBUG");
    static readonly bool? EnvVolumetric = EnvToggle("BALLISTIC_FX_VOLUMETRIC");
    static readonly bool? EnvSsao = EnvToggle("BALLISTIC_FX_SSAO");
    // Value overrides for A/B-ing the cinematic lens FX headlessly (e.g. BALLISTIC_FX_CA=2).
    static readonly float? EnvChromaticAberration = EnvFloat("BALLISTIC_FX_CA");
    static readonly float? EnvLensDistortion = EnvFloat("BALLISTIC_FX_DISTORTION");
    static readonly bool? EnvDof = EnvToggle("BALLISTIC_FX_DOF");
    static readonly float? EnvDofFocus = EnvFloat("BALLISTIC_FX_DOF_FOCUS");
    static readonly bool? EnvContactShadows = EnvToggle("BALLISTIC_FX_CONTACTSHADOWS");
    static readonly float? EnvNormalStrength = EnvFloat("BALLISTIC_FX_NORMAL");

    static bool? EnvToggle(string name) => Environment.GetEnvironmentVariable(name) switch {
        "0" => false,
        "1" => true,
        _ => null,
    };

    static float? EnvFloat(string name) =>
        float.TryParse(Environment.GetEnvironmentVariable(name),
            System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : null;

    void ApplyEnvOverrides() {
        if (EnvSsgi is { } ssgiOn)
            PostFX.SsgiEnabled = ssgiOn;
        if (EnvSsr is { } ssrOn)
            PostFX.SsrEnabled = ssrOn;
        if (EnvSsgiDebug is { } ssgiDebug)
            PostFX.SsgiDebugView = ssgiDebug;
        if (EnvVolumetric is { } volumetricOn)
            PostFX.VolumetricEnabled = volumetricOn;
        if (EnvSsao is { } ssaoOn)
            PostFX.SSAOEnabled = ssaoOn;
        if (EnvChromaticAberration is { } ca)
            PostFX.ChromaticAberration = ca;
        if (EnvLensDistortion is { } dist)
            PostFX.LensDistortion = dist;
        if (EnvDof is { } dofOn)
            PostFX.DofEnabled = dofOn;
        if (EnvDofFocus is { } dofFocus)
            PostFX.DofFocusDistance = dofFocus;
        if (EnvContactShadows is { } csOn)
            PostFX.ContactShadowsEnabled = csOn;
        if (EnvNormalStrength is { } ns)
            NormalStrength = ns;
    }

    void SplitRenderables(Vector3 cameraPos, ref Matrix4 viewProjection) {
        opaqueItems.Clear();
        transparentItems.Clear();
        visibleOpaque.Clear();
        visibleTransparent.Clear();
        ExtractFrustumPlanes(ref viewProjection, cullPlanes);
        var geo = new HashCode();

        foreach (IStaticMeshRenderer target in RuntimeSet<IStaticMeshRenderer>.ReadOnlyCollection) {
            if (!target.IsRenderable || !target.IsActive)
                continue;

            // A multi-material mesh can hold both blend modes (walls + glass); it then goes in
            // both lists and each pass draws only its own submeshes. A single-submesh renderer
            // (SubMeshIndex >= 0) is classified by that submesh alone.
            var hasOpaque = false;
            var hasTransparent = false;
            SubMeshData[] subMeshes = target.SharedMesh.SubMeshes;
            (int first, int end) = SubMeshRange(target, subMeshes.Length);
            for (var i = first; i < end; i++) {
                Material material = target.MaterialFor(i);
                if (material is null)
                    continue;
                if (material.Transparent)
                    hasTransparent = true;
                else
                    hasOpaque = true;
            }

            if (!hasOpaque && !hasTransparent)
                continue;

            ComputeWorldAabb(target, out Vector3 aabbMin, out Vector3 aabbMax);
            var item = new DrawItem { R = target, AabbMin = aabbMin, AabbMax = aabbMax };
            var inView = AabbInFrustum(cullPlanes, aabbMin, aabbMax);

            if (hasOpaque) {
                opaqueItems.Add(item);
                geo.Add(aabbMin);
                geo.Add(aabbMax);
                if (inView)
                    visibleOpaque.Add(target);
            }

            if (hasTransparent) {
                transparentItems.Add(item);
                if (inView)
                    visibleTransparent.Add(target);
            }

            if (inView)
                stats.RenderersVisible++;
            else
                stats.RenderersCulled++;
        }

        geometryStamp = geo.ToHashCode();

        // State-change sort for opaques: group by material, then mesh, then submesh. The
        // z-prepass already eliminates overdraw shading, so binding cost is what's left —
        // and identical (mesh, submesh, material) runs become adjacent for instancing.
        // Identity hashes only need to be CONSISTENT within a frame, not meaningful.
        if (visibleOpaque.Count > 1) {
            opaqueSortScratch.Clear();
            foreach (IStaticMeshRenderer r in visibleOpaque) {
                Material material = r.MaterialFor(Math.Max(r.SubMeshIndex, 0));
                var key = ((ulong)(uint)(material?.GetHashCode() ?? 0) << 32) |
                          ((uint)(r.SharedMesh.GetHashCode() & 0xFFFF) << 16) |
                          (ushort)(r.SubMeshIndex + 1);
                opaqueSortScratch.Add((key, r));
            }

            opaqueSortScratch.Sort(static (a, b) => a.Key.CompareTo(b.Key));
            visibleOpaque.Clear();
            foreach ((_, IStaticMeshRenderer r) in opaqueSortScratch)
                visibleOpaque.Add(r);
        }

        // Back-to-front so alpha blending composites correctly. World positions: parented
        // glass would otherwise sort by its local offset.
        if (visibleTransparent.Count > 1)
            visibleTransparent.Sort((a, b) =>
                (b.Transform.WorldPosition - cameraPos).LengthSquared.CompareTo(
                    (a.Transform.WorldPosition - cameraPos).LengthSquared));
    }

    // World AABB of what this renderer actually draws: its submesh's baked-space bounds (or the
    // whole mesh's for SubMeshIndex -1) pushed through the same matrix the draw uses.
    static void ComputeWorldAabb(IStaticMeshRenderer target, out Vector3 min, out Vector3 max) {
        Mesh mesh = target.SharedMesh;
        Vector3 lMin, lMax;
        if (target.SubMeshIndex >= 0)
            mesh.GetSubMeshBounds(target.SubMeshIndex, out lMin, out lMax);
        else
            mesh.GetLocalBounds(out lMin, out lMax);

        Matrix4 model = ModelMatrix(target, mesh);
        min = new Vector3(float.MaxValue);
        max = new Vector3(float.MinValue);
        for (var c = 0; c < 8; c++) {
            var corner = new Vector3(
                (c & 1) == 0 ? lMin.X : lMax.X,
                (c & 2) == 0 ? lMin.Y : lMax.Y,
                (c & 4) == 0 ? lMin.Z : lMax.Z);
            Vector3 w = (new Vector4(corner, 1f) * model).Xyz;
            min = Vector3.ComponentMin(min, w);
            max = Vector3.ComponentMax(max, w);
        }
    }

    // Fills `result` with the items whose AABB intersects the frustum of `viewProjection`.
    static void CullInto(ref Matrix4 viewProjection, List<DrawItem> items,
        List<IStaticMeshRenderer> result) {
        ExtractFrustumPlanes(ref viewProjection, cullPlanes);
        result.Clear();
        foreach (DrawItem item in items)
            if (AabbInFrustum(cullPlanes, item.AabbMin, item.AabbMax))
                result.Add(item.R);
    }

    void GatherPunctualLights() {
        var stamp = new HashCode();

        pointLightCount = 0;
        shadowedPointCount = 0;
        foreach (PointLight point in RuntimeSet<PointLight>.ReadOnlyCollection) {
            if (pointLightCount >= MaxPointLights)
                break;
            if (!point.IsActive)
                continue;
            Vector3 position = point.transform.WorldPosition; // parented lights shine from where they ARE
            var range = MathF.Max(point.Range, 1e-3f);
            pointPositions[pointLightCount] = position;
            // lumens -> candela -> radiance, pre-exposed (see the pre-exposure note in Render).
            pointColors[pointLightCount] = point.PhysicalColor * PostFX.ExposureMultiplier;
            pointRanges[pointLightCount] = range;

            // Shadow slot: 6 cube-face matrices into the punctual array.
            pointShadowSlots[pointLightCount] = -1;
            if (point.CastShadows && shadowedPointCount < MaxShadowedPoints) {
                var slot = shadowedPointCount++;
                pointShadowSlots[pointLightCount] = slot;
                pointShadowBiases[slot] = MathF.Max(point.ShadowBias, 0f);
                Matrix4 faceProj = Matrix4.CreatePerspectiveFieldOfView(
                    MathHelper.PiOver2, 1f, 0.05f, MathF.Max(range, 0.1f));
                for (var f = 0; f < 6; f++) {
                    Matrix4 faceView = Matrix4.LookAt(position, position + CubeFaceForward[f], CubeFaceUp[f]);
                    pointShadowMatrices[slot * 6 + f] = faceView * faceProj;
                }
                stamp.Add(position);
                stamp.Add(range);
            }

            pointLightCount++;
        }

        spotLightCount = 0;
        shadowedSpotCount = 0;
        foreach (SpotLight spot in RuntimeSet<SpotLight>.ReadOnlyCollection) {
            if (spotLightCount >= MaxSpotLights)
                break;
            if (!spot.IsActive)
                continue;
            Vector3 position = spot.transform.WorldPosition; // world, not the local parent offset
            Vector3 direction = spot.transform.WorldRotation * Vector3.UnitZ;
            var range = MathF.Max(spot.Range, 1e-3f);
            spotPositions[spotLightCount] = position;
            spotDirections[spotLightCount] = direction;
            spotColors[spotLightCount] = spot.PhysicalColor * PostFX.ExposureMultiplier; // pre-exposed
            spotRanges[spotLightCount] = range;
            var inner = MathHelper.DegreesToRadians(Math.Clamp(spot.InnerAngle, 0f, 89f));
            var outer = MathHelper.DegreesToRadians(Math.Clamp(MathF.Max(spot.OuterAngle, spot.InnerAngle), 0f, 89.9f));
            spotCosInner[spotLightCount] = MathF.Cos(inner);
            spotCosOuter[spotLightCount] = MathF.Cos(outer);

            spotShadowSlots[spotLightCount] = -1;
            if (spot.CastShadows && shadowedSpotCount < MaxShadowedSpots) {
                var slot = shadowedSpotCount++;
                spotShadowSlots[spotLightCount] = slot;
                spotShadowBiases[slot] = MathF.Max(spot.ShadowBias, 0f);
                Vector3 up = MathF.Abs(Vector3.Dot(direction, Vector3.UnitY)) > 0.99f
                    ? Vector3.UnitZ
                    : Vector3.UnitY;
                Matrix4 view = Matrix4.LookAt(position, position + direction, up);
                Matrix4 proj = Matrix4.CreatePerspectiveFieldOfView(
                    MathHelper.DegreesToRadians(Math.Clamp(MathF.Max(spot.OuterAngle, spot.InnerAngle),
                        1f, 89.9f)) * 2f, 1f, 0.05f, range);
                spotShadowMatrices[slot] = view * proj;
                stamp.Add(position);
                stamp.Add(direction);
                stamp.Add(range);
            }

            spotLightCount++;
        }

        // Geometry fingerprint: punctual tiles re-render when meshes move/appear/rotate
        // (AABB-based stamp from SplitRenderables).
        stamp.Add(geometryStamp);

        var newStamp = stamp.ToHashCode();
        punctualShadowsDirty = newStamp != punctualShadowStamp;
        punctualShadowStamp = newStamp;
    }

    void RenderShadowPass() {
        using var profileZone = Profiler.Zone("HD.ShadowPass");

        GL.ColorMask(false, false, false, false);
        GL.DepthMask(true);
        GL.Enable(EnableCap.DepthTest);
        GL.Enable(EnableCap.CullFace);
        // Front-face culling pushes acne onto back faces where the bias hides it.
        GL.CullFace(TriangleFace.Front);

        shadowDepthShader.Activate();
        for (var cascade = 0; cascade < activeCascadeCount; cascade++) {
            // Skip cascades whose texel-snapped fit AND casters are unchanged — the layer in
            // the depth array is still exactly right (static camera = all four free).
            if (cascadeValid[cascade] && cascadeCachedGeometry[cascade] == geometryStamp &&
                cascadeCachedMatrix[cascade] == cascadeMatrices[cascade])
                continue;
            cascadeValid[cascade] = true;
            cascadeCachedGeometry[cascade] = geometryStamp;
            cascadeCachedMatrix[cascade] = cascadeMatrices[cascade];

            shadowMap.Bind(cascade); // binds FBO layer, sets viewport, clears depth
            shadowDepthShader.SetMatrix4("lightSpaceMatrix", ref cascadeMatrices[cascade]);
            // Each cascade draws only the casters inside its own light-space frustum — the
            // rasterizer clipped the rest anyway, so this can't change the image.
            CullInto(ref cascadeMatrices[cascade], opaqueItems, cullScratch);
            RenderShadowCasters(cullScratch);
        }

        // Punctual tiles only when lights/geometry changed (static scenes render these once).
        if (punctualShadowsDirty) {
            for (var s = 0; s < shadowedSpotCount; s++) {
                punctualShadows.Bind(s);
                shadowDepthShader.SetMatrix4("lightSpaceMatrix", ref spotShadowMatrices[s]);
                CullInto(ref spotShadowMatrices[s], opaqueItems, cullScratch);
                RenderShadowCasters(cullScratch);
            }

            for (var p = 0; p < shadowedPointCount; p++)
            for (var f = 0; f < 6; f++) {
                punctualShadows.Bind(MaxShadowedSpots + p * 6 + f);
                shadowDepthShader.SetMatrix4("lightSpaceMatrix", ref pointShadowMatrices[p * 6 + f]);
                CullInto(ref pointShadowMatrices[p * 6 + f], opaqueItems, cullScratch);
                RenderShadowCasters(cullScratch);
            }
        }

        shadowDepthShader.Deactivate();

        GL.ColorMask(true, true, true, true);
        GL.CullFace(TriangleFace.Back);
        shadowMap.Unbind();
    }

    void RenderShadowCasters(List<IStaticMeshRenderer> casters) {
        foreach (IStaticMeshRenderer target in casters) {
            Mesh mesh = target.SharedMesh;
            Matrix4 worldMatrix = ModelMatrix(target, mesh);
            mesh.Activate(); // VAO only; the depth pass doesn't need the material
            shadowDepthShader.SetMatrix4("model", ref worldMatrix);

            // Only opaque submeshes cast shadows; transparent/unassigned ranges are skipped.
            SubMeshData[] subMeshes = mesh.SubMeshes;
            (int first, int end) = SubMeshRange(target, subMeshes.Length);
            for (var i = first; i < end; i++) {
                if (target.MaterialFor(i) is not { Transparent: false } material)
                    continue;

                // Cutout casters: alpha-test the depth pass so leaves shadow as leaves,
                // not as full quads — and skip culling, since cards are single-sided.
                var cutout = material.Cutout && material.Diffuse is not null;
                shadowDepthShader.SetBool("AlphaCutout", cutout);
                if (cutout) {
                    GL.ActiveTexture(TextureUnit.Texture0);
                    GL.BindTexture(TextureTarget.Texture2D, material.Diffuse.UID);
                    shadowDepthShader.SetInt("Diffuse", 0);
                    GL.Disable(EnableCap.CullFace);
                }

                GL.DrawElements(PrimitiveType.Triangles, subMeshes[i].IndexCount, DrawElementsType.UnsignedInt,
                    (IntPtr)(subMeshes[i].IndexStart * sizeof(uint)));
                stats.DepthOnlyDrawCalls++;

                if (cutout)
                    GL.Enable(EnableCap.CullFace);
            }

            mesh.Deactivate();
        }
    }

    // Recreates the cascade array when the Shadows volume changes resolution (pow2-snapped,
    // so volume blending between two resolutions flips once instead of thrashing).
    void EnsureShadowResolution(int requested) {
        var snapped = 1 << (int)MathF.Round(MathF.Log2(Math.Clamp(requested, 512, 4096)));
        if (shadowMap is not null && shadowMap.Width == snapped)
            return;
        shadowMap?.Dispose();
        shadowMap = new GLShadowMap(snapped, snapped, CascadeCount);
        Array.Clear(cascadeValid); // fresh texture array: every cached layer is gone
    }

    // Depth-only companions to material shaders: the SAME vertex source compiled with a tiny
    // cutout-aware fragment. With `invariant gl_Position` injected at compile, prepass depth is
    // bit-identical to the main pass, so the main pass can re-test LEqual and skip depth writes.
    readonly Dictionary<Shader, Shader> prepassShaders = new();

    const string PrepassFrag = @"
#version 330 core
in vec2 texCoord;
uniform bool AlphaCutout;
uniform sampler2D Diffuse;

void main() {
    if (AlphaCutout && texture(Diffuse, texCoord).a < 0.5)
        discard;
}
";

    Shader PrepassShaderFor(Shader materialShader) {
        if (prepassShaders.TryGetValue(materialShader, out Shader cached))
            return cached;

        // CreateStandardShader dedupes by source hash, so shaders sharing a vertex stage
        // share one prepass program.
        Shader created = materialShader is StandardShader std
            ? GraphicAPI.CreateStandardShader(std.VertexCode, PrepassFrag)
            : null;
        prepassShaders[materialShader] = created;
        return created;
    }

    // True z-prepass with the SAME jittered projection and the SAME per-material vertex math
    // as the main pass. Feeds SSAO before shading AND gives the main pass exact early-z: it
    // re-tests LEqual against this depth without writing, so every opaque pixel shades once.
    void RenderDepthPrepass(ref Matrix4 view, ref Matrix4 renderProjection, Vector3 cameraPos) {
        GL.ColorMask(false, false, false, false);
        GL.DepthMask(true);
        GL.Enable(EnableCap.DepthTest);
        GL.DepthFunc(DepthFunction.Less);
        GL.Enable(EnableCap.CullFace);
        GL.CullFace(TriangleFace.Back);
        passDataFilled = false;

        Shader activePrepass = null;
        for (var t = 0; t < visibleOpaque.Count; t++) {
            IStaticMeshRenderer target = visibleOpaque[t];
            Mesh mesh = target.SharedMesh;
            SubMeshData[] subMeshes = mesh.SubMeshes;

            // Instanced runs MUST go through the same instanced vertex path as the main pass,
            // or the depth-equality contract (invariant gl_Position) breaks for them.
            var run = InstancedRunLength(visibleOpaque, t);
            if (run >= 2 && target.MaterialFor(target.SubMeshIndex) is { } runMaterial &&
                PrepassShaderFor(runMaterial.Shader) is { } runPrepass) {
                ActivatePrepassShader(runPrepass, ref activePrepass, ref view, ref renderProjection,
                    cameraPos);

                var runCutout = runMaterial.Cutout && runMaterial.Diffuse is not null;
                runPrepass.SetBool("AlphaCutout", runCutout);
                if (runCutout) {
                    GL.ActiveTexture(TextureUnit.Texture0);
                    GL.BindTexture(TextureTarget.Texture2D, runMaterial.Diffuse.UID);
                    GL.Disable(EnableCap.CullFace);
                }

                mesh.Activate();
                Matrix4[] matrices = ArrayPool<Matrix4>.Shared.Rent(run);
                for (var k = 0; k < run; k++)
                    matrices[k] = ModelMatrix(visibleOpaque[t + k], mesh);
                mesh.InstanceBuffer.SetBufferData(matrices, BufferUsageHint.StreamDraw);
                ArrayPool<Matrix4>.Shared.Return(matrices);

                SubMeshData subMesh = subMeshes[target.SubMeshIndex];
                runPrepass.SetBool("isInstanced", true);
                GL.DrawElementsInstanced(PrimitiveType.Triangles, subMesh.IndexCount,
                    DrawElementsType.UnsignedInt, (IntPtr)(subMesh.IndexStart * sizeof(uint)), run);
                runPrepass.SetBool("isInstanced", false);
                stats.DepthOnlyDrawCalls++;

                if (runCutout)
                    GL.Enable(EnableCap.CullFace);
                mesh.Deactivate();
                t += run - 1;
                continue;
            }

            Matrix4 worldMatrix = ModelMatrix(target, mesh);
            mesh.Activate();

            (int first, int end) = SubMeshRange(target, subMeshes.Length);
            for (var i = first; i < end; i++) {
                if (target.MaterialFor(i) is not { Transparent: false } material)
                    continue;

                Shader prepass = PrepassShaderFor(material.Shader);
                if (prepass is null)
                    continue;
                ActivatePrepassShader(prepass, ref activePrepass, ref view, ref renderProjection,
                    cameraPos);

                prepass.SetMatrix4("model", ref worldMatrix);

                var cutout = material.Cutout && material.Diffuse is not null;
                prepass.SetBool("AlphaCutout", cutout);
                if (cutout) {
                    GL.ActiveTexture(TextureUnit.Texture0);
                    GL.BindTexture(TextureTarget.Texture2D, material.Diffuse.UID);
                    GL.Disable(EnableCap.CullFace); // cards are single-sided, same as the main pass
                }

                GL.DrawElements(PrimitiveType.Triangles, subMeshes[i].IndexCount, DrawElementsType.UnsignedInt,
                    (IntPtr)(subMeshes[i].IndexStart * sizeof(uint)));
                stats.DepthOnlyDrawCalls++;

                if (cutout)
                    GL.Enable(EnableCap.CullFace);
            }

            mesh.Deactivate();
        }

        activePrepass?.Deactivate();
        GL.ColorMask(true, true, true, true);
    }

    void ActivatePrepassShader(Shader prepass, ref Shader activePrepass, ref Matrix4 view,
        ref Matrix4 renderProjection, Vector3 cameraPos) {
        if (ReferenceEquals(prepass, activePrepass))
            return;
        prepass.Activate();
        if (passData.RegisterProgram(prepass.UID)) {
            if (!passDataFilled) {
                FillPassData(ref view, ref renderProjection, cameraPos);
                passDataFilled = true;
            }
            passData.UploadAndBind();
        }
        else {
            // Legacy vertex source: plain view/projection uniforms.
            prepass.SetMatrix4("view", ref view);
            prepass.SetMatrix4("projection", ref renderProjection);
        }
        prepass.SetBool("isInstanced", false);
        prepass.SetInt("Diffuse", 0);
        activePrepass = prepass;
    }

    // ---- Irradiance probe bake (time-sliced) -------------------------------------------
    // Each probe renders the scene into a 6-face atlas strip (ONE readback per probe instead
    // of six - driver sync stalls dominated the old bake), projected to L1 SH on the CPU.
    // The job advances a few milliseconds per frame so the editor keeps painting, and the
    // finished grid is cached to Library/ProbeVolumes so later loads skip the bake entirely.

    const int BakeFaceRes = 32;
    const double BakeBudgetMs = 14.0;

    sealed class ProbeBakeJob {
        public IrradianceVolume Volume;
        public int Px, Py, Pz, Total, Cursor;
        public Vector3 Min, Size;
        public float[][] Sh;     // 4 channels x (Total * 4 floats)
        public bool[] Occupied;  // probes near geometry; the rest skip straight to sky SH
        public int CapturedCount;
        // Renderer snapshot + world AABBs (computed once): per-face frustum culling means a
        // probe in one street doesn't re-draw the whole city for every cube face.
        public IStaticMeshRenderer[] Renderers;
        public Vector3[] AabbMin, AabbMax;
        public int Fbo, AtlasTex, DepthRbo;
        public float[] Pixels;   // one probe's 6-face atlas readback
        public Matrix4 CaptureProj;
        public GLShadowMap Shadow;       // volume-fitted sun shadow, rendered ONCE per bake
        public Matrix4 ShadowMatrix;
        public float ShadowCompareBias;
        public System.Diagnostics.Stopwatch Watch;
    }

    ProbeBakeJob probeBake;
    // While non-zero, the lit pass samples THIS depth array as the sun shadow instead of the
    // per-view cascades (probe captures use the bake's volume-fitted map).
    int sunShadowOverride;

    // Scratch state for per-face culling during the bake.
    readonly List<IStaticMeshRenderer> bakeVisible = new();
    readonly Vector4[] bakeFrustum = new Vector4[6];

    // Gribb-Hartmann plane extraction for the row-vector convention (clip_i = v . Column_i).
    static void ExtractFrustumPlanes(ref Matrix4 vp, Vector4[] planes) {
        Vector4 c0 = vp.Column0, c1 = vp.Column1, c2 = vp.Column2, c3 = vp.Column3;
        planes[0] = c3 + c0; // left
        planes[1] = c3 - c0; // right
        planes[2] = c3 + c1; // bottom
        planes[3] = c3 - c1; // top
        planes[4] = c3 + c2; // near
        planes[5] = c3 - c2; // far
    }

    // Positive-vertex AABB test: outside if the farthest corner along the plane normal is behind it.
    static bool AabbInFrustum(Vector4[] planes, Vector3 min, Vector3 max) {
        for (var i = 0; i < 6; i++) {
            Vector4 plane = planes[i];
            var p = new Vector3(
                plane.X >= 0f ? max.X : min.X,
                plane.Y >= 0f ? max.Y : min.Y,
                plane.Z >= 0f ? max.Z : min.Z);
            if (plane.X * p.X + plane.Y * p.Y + plane.Z * p.Z + plane.W < 0f)
                return false;
        }
        return true;
    }

    void StepProbeBake(float preExposure) {
        // The editor renders ONE view per frame (Scene tab or Game tab), so stepping from
        // whichever BeginRender runs is correct - gating on the Scene target stalled the bake
        // completely while the Game tab was active.
        IrradianceVolume vol = IrradianceVolume.Active is { IsActive: true } active ? active : null;

        // Volume removed/disabled mid-bake: abort cleanly.
        if (probeBake is not null && !ReferenceEquals(probeBake.Volume, vol)) {
            AbortProbeBake();
            if (vol is null)
                return;
        }

        if (vol is null)
            return;

        if (IrradianceVolume.CancelRequested) {
            IrradianceVolume.CancelRequested = false;
            if (probeBake is not null) {
                Console.WriteLine($"[ProbeVolume] bake cancelled at {probeBake.Cursor}/{probeBake.Total}.");
                AbortProbeBake();
            }
            return;
        }

        // Clear Baked Data: drop the live textures, the gizmo viz, and the cache file - the
        // scene falls back to plain sky irradiance until the next bake.
        if (vol.ClearRequested) {
            vol.ClearRequested = false;
            AbortProbeBake();
            probeVolumeReady = false;
            IrradianceVolume.Viz = null;
            IrradianceVolume.DeleteCache(ProbeCacheKey(vol));
            vol.Bake = false;
            vol.CacheChecked = true; // don't auto-rebake what was just deliberately cleared
            Console.WriteLine("[ProbeVolume] baked data cleared.");
            return;
        }

        // One-shot auto-restore the first time this volume instance is seen (scene open).
        // The cache key derives from scene + grid settings, so baked data returns WITHOUT the
        // scene ever having been re-saved after a bake. Cache miss = bake now regardless of
        // the serialized flag - a present-but-dataless volume is never what anyone wants.
        if (!vol.CacheChecked && probeBake is null) {
            vol.CacheChecked = true;
            if (!vol.ForceRebake && TryLoadProbeCache(vol)) {
                vol.Bake = false;
                return;
            }
            vol.Bake = true;
        }

        if (vol.Bake && probeBake is null) {
            vol.Bake = false;
            var force = vol.ForceRebake;
            vol.ForceRebake = false;
            if (!force && TryLoadProbeCache(vol))
                return;
            BeginProbeBake(vol);
        }

        if (probeBake is null)
            return;

        ProbeBakeJob job = probeBake;

        // Captures read the job's volume-fitted shadow through cascade slot 0; the view
        // cascade fit right after this method rebuilds every field the bake borrows.
        cascadeMatrices[0] = job.ShadowMatrix;
        activeCascadeCount = 1;
        cascadeBias = new Vector4(job.ShadowCompareBias);
        cascadeBlend = 0.001f;
        sunShadowOverride = job.Shadow.DepthTextureId;

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, job.Fbo);
        Vector3 skyAvg = (skyboxRenderer.cubemapTexture?.skyAmbient ?? Vector3.One * 0.5f) *
                         skyExposureBase * preExposure;
        GL.ClearColor(skyAvg.X, skyAvg.Y, skyAvg.Z, 1f);

        // Constant-radiance sky SH for skipped empty-air probes: c0 = L * 0.282095 * 4pi,
        // directional bands zero. Stored un-exposed like everything else.
        Vector3 skyUnexposed = (skyboxRenderer.cubemapTexture?.skyAmbient ?? Vector3.One * 0.5f) *
                               skyExposureBase;
        Vector3 skySh0 = skyUnexposed * 3.5449f;

        var slice = System.Diagnostics.Stopwatch.StartNew();
        GL.Enable(EnableCap.ScissorTest);
        while (job.Cursor < job.Total && slice.Elapsed.TotalMilliseconds < BakeBudgetMs) {
            var idx = job.Cursor;

            // Empty air: no geometry anywhere near this cell, so a capture would just return
            // the sky average from every direction. Write that directly and skip 6 renders.
            if (!job.Occupied[idx]) {
                StoreSH(job.Sh[0], idx * 4, skySh0);
                StoreSH(job.Sh[1], idx * 4, Vector3.Zero);
                StoreSH(job.Sh[2], idx * 4, Vector3.Zero);
                StoreSH(job.Sh[3], idx * 4, Vector3.Zero);
                job.Cursor++;
                continue;
            }

            var ix = idx % job.Px;
            var iy = idx / job.Px % job.Py;
            var iz = idx / (job.Px * job.Py);
            var probePos = job.Min + new Vector3(
                (ix + 0.5f) / job.Px * job.Size.X,
                (iy + 0.5f) / job.Py * job.Size.Y,
                (iz + 0.5f) / job.Pz * job.Size.Z);

            for (var f = 0; f < 6; f++) {
                GL.Viewport(f * BakeFaceRes, 0, BakeFaceRes, BakeFaceRes);
                GL.Scissor(f * BakeFaceRes, 0, BakeFaceRes, BakeFaceRes);
                GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
                Matrix4 view = Matrix4.LookAt(probePos, probePos + CubeFaceForward[f], CubeFaceUp[f]);

                // Frustum-cull against this face's 90-degree view: each face only draws what
                // it can actually see instead of the whole scene six times per probe.
                Matrix4 faceVp = view * job.CaptureProj;
                ExtractFrustumPlanes(ref faceVp, bakeFrustum);
                bakeVisible.Clear();
                for (var r = 0; r < job.Renderers.Length; r++)
                    if (AabbInFrustum(bakeFrustum, job.AabbMin[r], job.AabbMax[r]))
                        bakeVisible.Add(job.Renderers[r]);

                RenderMeshes(bakeVisible, transparentPass: false, ref view, ref job.CaptureProj, probePos);
            }

            GL.ReadPixels(0, 0, BakeFaceRes * 6, BakeFaceRes, PixelFormat.Rgba, PixelType.Float, job.Pixels);
            IntegrateProbe(job, idx, preExposure);
            job.Cursor++;
        }
        GL.Disable(EnableCap.ScissorTest);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        sunShadowOverride = 0;

        IrradianceVolume.BakeProgress = job.Cursor / (float)job.Total;
        IrradianceVolume.BakeStatus = $"Baking light probes  {job.Cursor}/{job.Total}";

        if (job.Cursor >= job.Total)
            FinishProbeBake();
    }

    void BeginProbeBake(IrradianceVolume vol) {
        var job = new ProbeBakeJob {
            Volume = vol,
            Px = Math.Clamp(vol.ProbesX, 2, 64),
            Py = Math.Clamp(vol.ProbesY, 2, 64),
            Pz = Math.Clamp(vol.ProbesZ, 2, 64),
            Watch = System.Diagnostics.Stopwatch.StartNew(),
        };
        job.Total = job.Px * job.Py * job.Pz;
        job.Size = Vector3.ComponentMax(vol.Size, Vector3.One * 0.5f);
        job.Min = vol.Center - job.Size * 0.5f;
        job.Sh = new float[4][];
        for (var t = 0; t < 4; t++)
            job.Sh[t] = new float[job.Total * 4];
        job.Pixels = new float[BakeFaceRes * 6 * BakeFaceRes * 4];
        var far = MathF.Max(job.Size.Length, 10f) + 20f;
        job.CaptureProj = Matrix4.CreatePerspectiveFieldOfView(MathHelper.PiOver2, 1f, 0.05f, far);

        // Snapshot the renderable set with the world AABBs SplitRenderables already computed
        // this frame; both the occupancy grid below and the per-face frustum culling use these.
        job.Renderers = new IStaticMeshRenderer[opaqueItems.Count];
        job.AabbMin = new Vector3[opaqueItems.Count];
        job.AabbMax = new Vector3[opaqueItems.Count];
        for (var r = 0; r < opaqueItems.Count; r++) {
            job.Renderers[r] = opaqueItems[r].R;
            job.AabbMin[r] = opaqueItems[r].AabbMin;
            job.AabbMax[r] = opaqueItems[r].AabbMax;
        }

        // Occupancy grid: only probes with geometry in (or one cell around) their cell are
        // worth capturing - a probe floating in open air sees the sky average from every
        // direction, which we can write analytically. In a city scene this skips MOST of the
        // grid, and it's where the bulk of the bake time went.
        job.Occupied = new bool[job.Total];
        var cell = new Vector3(job.Size.X / job.Px, job.Size.Y / job.Py, job.Size.Z / job.Pz);
        for (var r = 0; r < job.Renderers.Length; r++) {
            // Dilate by one cell so probes just above a roof / beside a wall still capture.
            Vector3 wMin = job.AabbMin[r] - cell;
            Vector3 wMax = job.AabbMax[r] + cell;
            if (wMax.X < job.Min.X || wMin.X > job.Min.X + job.Size.X ||
                wMax.Y < job.Min.Y || wMin.Y > job.Min.Y + job.Size.Y ||
                wMax.Z < job.Min.Z || wMin.Z > job.Min.Z + job.Size.Z)
                continue;

            var x0 = Math.Clamp((int)((wMin.X - job.Min.X) / cell.X), 0, job.Px - 1);
            var x1 = Math.Clamp((int)((wMax.X - job.Min.X) / cell.X), 0, job.Px - 1);
            var y0 = Math.Clamp((int)((wMin.Y - job.Min.Y) / cell.Y), 0, job.Py - 1);
            var y1 = Math.Clamp((int)((wMax.Y - job.Min.Y) / cell.Y), 0, job.Py - 1);
            var z0 = Math.Clamp((int)((wMin.Z - job.Min.Z) / cell.Z), 0, job.Pz - 1);
            var z1 = Math.Clamp((int)((wMax.Z - job.Min.Z) / cell.Z), 0, job.Pz - 1);

            for (var z = z0; z <= z1; z++)
            for (var y = y0; y <= y1; y++)
            for (var x = x0; x <= x1; x++)
                job.Occupied[(z * job.Py + y) * job.Px + x] = true;
        }
        foreach (var occupied in job.Occupied)
            if (occupied)
                job.CapturedCount++;

        // Volume-fitted sun shadow, rendered ONCE (the volume and sun are static for the
        // duration of the bake; re-rendering it per slice was a full extra scene pass per frame).
        Vector3 lightDir = (-sunDirection).Normalized();
        Vector3 volumeCenter = job.Min + job.Size * 0.5f;
        var radius = MathF.Max(job.Size.Length * 0.5f, 1f);
        var backup = radius * 2f + 60f;
        Vector3 shadowUp = MathF.Abs(Vector3.Dot(lightDir, Vector3.UnitY)) > 0.99f
            ? Vector3.UnitZ
            : Vector3.UnitY;
        Matrix4 lightView = Matrix4.LookAt(volumeCenter - lightDir * backup, volumeCenter, shadowUp);
        Matrix4 lightProj = Matrix4.CreateOrthographic(radius * 2f, radius * 2f, 0.1f, backup + radius * 2f);
        job.ShadowMatrix = lightView * lightProj;
        job.ShadowCompareBias = (DirectionalLight.Instance?.ShadowBias ?? 0.0015f) * 140f /
                                (backup + radius * 2f);
        job.Shadow = new GLShadowMap(2048, 2048, 1);

        GL.ColorMask(false, false, false, false);
        GL.DepthMask(true);
        GL.Enable(EnableCap.DepthTest);
        GL.Enable(EnableCap.CullFace);
        GL.CullFace(TriangleFace.Front);
        shadowDepthShader.Activate();
        job.Shadow.Bind(0);
        shadowDepthShader.SetMatrix4("lightSpaceMatrix", ref job.ShadowMatrix);
        CullInto(ref job.ShadowMatrix, opaqueItems, cullScratch);
        RenderShadowCasters(cullScratch);
        shadowDepthShader.Deactivate();
        GL.ColorMask(true, true, true, true);
        GL.CullFace(TriangleFace.Back);
        job.Shadow.Unbind();

        // Capture atlas: the 6 faces side by side, so one ReadPixels drains a whole probe.
        job.Fbo = GL.GenFramebuffer();
        job.AtlasTex = GL.GenTexture();
        job.DepthRbo = GL.GenRenderbuffer();
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, job.Fbo);
        GL.BindTexture(TextureTarget.Texture2D, job.AtlasTex);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba16f,
            BakeFaceRes * 6, BakeFaceRes, 0, PixelFormat.Rgba, PixelType.Float, IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, job.AtlasTex, 0);
        GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, job.DepthRbo);
        GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.DepthComponent24,
            BakeFaceRes * 6, BakeFaceRes);
        GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
            RenderbufferTarget.Renderbuffer, job.DepthRbo);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

        probeBake = job;
        IrradianceVolume.IsBaking = true;
        IrradianceVolume.BakeProgress = 0f;
        IrradianceVolume.BakeStatus = $"Baking light probes  0/{job.Total}";
        Console.WriteLine($"[ProbeVolume] baking {job.Px}x{job.Py}x{job.Pz} = {job.Total} probes " +
                          $"({job.CapturedCount} near geometry, {job.Total - job.CapturedCount} skipped as air)...");
    }

    // L1 SH projection of one probe's atlas readback. Stored UN-exposed (divide the frame's
    // pre-exposure back out) so the volume survives EV changes / auto-exposure.
    void IntegrateProbe(ProbeBakeJob job, int probeIndex, float preExposure) {
        Vector3 c0 = Vector3.Zero, c1 = Vector3.Zero, c2 = Vector3.Zero, c3 = Vector3.Zero;
        var atlasWidth = BakeFaceRes * 6;

        for (var f = 0; f < 6; f++) {
            // Screen axes for this face (mirrors Matrix4.LookAt's basis).
            Vector3 zAxis = -CubeFaceForward[f];
            var xAxis = Vector3.Normalize(Vector3.Cross(CubeFaceUp[f], zAxis));
            Vector3 yAxis = Vector3.Cross(zAxis, xAxis);

            for (var yPix = 0; yPix < BakeFaceRes; yPix++)
            for (var xPix = 0; xPix < BakeFaceRes; xPix++) {
                float u = (xPix + 0.5f) / BakeFaceRes * 2f - 1f;
                float v = (yPix + 0.5f) / BakeFaceRes * 2f - 1f;
                var dir = Vector3.Normalize(CubeFaceForward[f] + xAxis * u + yAxis * v);
                // Solid angle of this cube-face texel.
                float w = 4f / (BakeFaceRes * BakeFaceRes * MathF.Pow(1f + u * u + v * v, 1.5f));

                var i = (yPix * atlasWidth + f * BakeFaceRes + xPix) * 4;
                var radiance = new Vector3(job.Pixels[i], job.Pixels[i + 1], job.Pixels[i + 2]);
                c0 += radiance * (0.282095f * w);
                c1 += radiance * (0.488603f * dir.Y * w);
                c2 += radiance * (0.488603f * dir.Z * w);
                c3 += radiance * (0.488603f * dir.X * w);
            }
        }

        var idx = probeIndex * 4;
        StoreSH(job.Sh[0], idx, c0 / preExposure);
        StoreSH(job.Sh[1], idx, c1 / preExposure);
        StoreSH(job.Sh[2], idx, c2 / preExposure);
        StoreSH(job.Sh[3], idx, c3 / preExposure);
    }

    void FinishProbeBake() {
        ProbeBakeJob job = probeBake;
        UploadProbeTextures(job.Px, job.Py, job.Pz, job.Sh, job.Min, job.Size);
        PublishProbeViz(job.Px, job.Py, job.Pz, job.Min, job.Size, job.Sh, job.Occupied);

        // Persist under the DERIVED key (scene + grid): reopening the scene recomputes the
        // same key and restores this file with no scene save involved.
        IrradianceVolume vol = job.Volume;
        IrradianceVolume.SaveCache(ProbeCacheKey(vol), job.Px, job.Py, job.Pz, vol.Center, job.Size, job.Sh);

        Console.WriteLine(
            $"[ProbeVolume] bake complete: {job.CapturedCount} captured + " +
            $"{job.Total - job.CapturedCount} sky probes in {job.Watch.Elapsed.TotalSeconds:F1}s (cached).");
        AbortProbeBake();
        IrradianceVolume.BakeProgress = 1f;
    }

    void AbortProbeBake() {
        if (probeBake is null)
            return;
        GL.DeleteFramebuffer(probeBake.Fbo);
        GL.DeleteTexture(probeBake.AtlasTex);
        GL.DeleteRenderbuffer(probeBake.DepthRbo);
        probeBake.Shadow?.Dispose();
        probeBake = null;
        sunShadowOverride = 0;
        IrradianceVolume.IsBaking = false;
    }

    // ---- Reflection probe bake (time-sliced) -------------------------------------------------
    // Mirrors the irradiance bake, but each occupied cell renders into a real cubemap (kept on the
    // GPU) and is GGX-prefiltered into a slice of a cube-map array - no readback, no SH. Empty-air
    // cells get no layer (cellToLayer = -1) and fall back to the global skybox reflection. The
    // finished array + cell->layer map cache to Library/ReflectionProbes (derived key, like the
    // diffuse volume) so reopening the scene restores them without a re-bake or a scene save.

    const int ReflectionCaptureRes = GLEnvironmentMaps.ReflectionFaceRes;
    const int ReflectionMipCount = GLEnvironmentMaps.ReflectionMipCount;
    const int MaxReflectionProbes = 96;     // ~1 MB each at 128px RGBA16F x 6 mips -> ~96 MB cap
    const double ReflectionBakeBudgetMs = 12.0;

    sealed class ReflectionBakeJob {
        public ReflectionVolume Volume;
        public int Px, Py, Pz, Total, Cursor;
        public Vector3 Min, Size;
        public bool[] Occupied;
        public int[] CellToLayer;   // per cell: layer index, or -1 (empty / capped -> skybox)
        public int OccupiedCount, LayerCount;
        public IStaticMeshRenderer[] Renderers;
        public Vector3[] AabbMin, AabbMax;
        public Matrix4 CaptureProj;
        public GLShadowMap Shadow;
        public Matrix4 ShadowMatrix;
        public float ShadowCompareBias;
        public float RadianceScale;   // 1/preExposure captured at bake start -> store physical
        public int CaptureCubemap, Fbo, DepthRbo, TargetArray;
        public System.Diagnostics.Stopwatch Watch;
    }

    ReflectionBakeJob reflectionBake;

    static string ReflectionCacheKey(ReflectionVolume vol) =>
        vol.DeriveCacheKey(SceneManager.GetCurrentScene()?.Name);

    void StepReflectionBake(float preExposure) {
        ReflectionVolume vol = ReflectionVolume.Active is { IsActive: true } active ? active : null;

        // Volume removed/disabled mid-bake: abort cleanly.
        if (reflectionBake is not null && !ReferenceEquals(reflectionBake.Volume, vol)) {
            AbortReflectionBake();
            if (vol is null)
                return;
        }

        if (vol is null)
            return;

        // Cancel is shared with the diffuse bake's channel; honour it only while WE are baking so
        // we don't swallow a cancel meant for the irradiance bake.
        if (IrradianceVolume.CancelRequested && reflectionBake is not null) {
            IrradianceVolume.CancelRequested = false;
            Console.WriteLine($"[ReflectionVolume] bake cancelled at {reflectionBake.Cursor}/{reflectionBake.Total}.");
            AbortReflectionBake();
            return;
        }

        // Clear Baked Data: drop the live array + cache; reflections fall back to the skybox.
        if (vol.ClearRequested) {
            vol.ClearRequested = false;
            AbortReflectionBake();
            if (reflectionArray != 0) {
                GL.DeleteTexture(reflectionArray);
                reflectionArray = 0;
            }
            if (reflectionCellToLayer != 0) {
                GL.DeleteTexture(reflectionCellToLayer);
                reflectionCellToLayer = 0;
            }
            reflectionVolumeReady = false;
            ReflectionVolume.Viz = null;
            ReflectionVolume.DeleteCache(ReflectionCacheKey(vol));
            vol.Bake = false;
            vol.CacheChecked = true; // don't auto-rebake what was just deliberately cleared
            Console.WriteLine("[ReflectionVolume] baked data cleared.");
            return;
        }

        // One-shot auto-restore the first time this volume instance is seen (scene open). The
        // cache key derives from scene + grid, so baked data returns WITHOUT a scene re-save.
        // Cache miss = bake now regardless of the serialized flag.
        if (!vol.CacheChecked && reflectionBake is null) {
            vol.CacheChecked = true;
            if (!vol.ForceRebake && TryLoadReflectionCache(vol)) {
                vol.Bake = false;
                return;
            }
            vol.Bake = true;
        }

        if (vol.Bake && reflectionBake is null) {
            vol.Bake = false;
            var force = vol.ForceRebake;
            vol.ForceRebake = false;
            if (!force && TryLoadReflectionCache(vol))
                return;
            BeginReflectionBake(vol, preExposure);
        }

        if (reflectionBake is null)
            return;

        ReflectionBakeJob job = reflectionBake;

        // Borrow cascade slot 0 for the bake's volume-fitted sun shadow (same as the diffuse bake).
        cascadeMatrices[0] = job.ShadowMatrix;
        activeCascadeCount = 1;
        cascadeBias = new Vector4(job.ShadowCompareBias);
        cascadeBlend = 0.001f;
        sunShadowOverride = job.Shadow.DepthTextureId;

        var slice = System.Diagnostics.Stopwatch.StartNew();
        while (job.Cursor < job.Total && slice.Elapsed.TotalMilliseconds < ReflectionBakeBudgetMs) {
            var idx = job.Cursor;
            var layer = job.CellToLayer[idx];

            // Empty-air or capped cell: nothing to capture (the shader falls back to the skybox).
            if (layer < 0) {
                job.Cursor++;
                continue;
            }

            var ix = idx % job.Px;
            var iy = idx / job.Px % job.Py;
            var iz = idx / (job.Px * job.Py);
            var probePos = job.Min + new Vector3(
                (ix + 0.5f) / job.Px * job.Size.X,
                (iy + 0.5f) / job.Py * job.Size.Y,
                (iz + 0.5f) / job.Pz * job.Size.Z);

            // Render the scene into the 6 faces of the reusable capture cubemap.
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, job.Fbo);
            GL.Viewport(0, 0, ReflectionCaptureRes, ReflectionCaptureRes);
            for (var f = 0; f < 6; f++) {
                GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                    TextureTarget.TextureCubeMapPositiveX + f, job.CaptureCubemap, 0);
                GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
                Matrix4 view = Matrix4.LookAt(probePos, probePos + CubeFaceForward[f], CubeFaceUp[f]);

                // Per-face frustum cull: each 90-degree face only draws what it can see.
                Matrix4 faceVp = view * job.CaptureProj;
                ExtractFrustumPlanes(ref faceVp, bakeFrustum);
                bakeVisible.Clear();
                for (var r = 0; r < job.Renderers.Length; r++)
                    if (AabbInFrustum(bakeFrustum, job.AabbMin[r], job.AabbMax[r]))
                        bakeVisible.Add(job.Renderers[r]);

                RenderMeshes(bakeVisible, transparentPass: false, ref view, ref job.CaptureProj, probePos);
            }
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

            // Mip the captured cube (the prefilter samples matched source mips to avoid fireflies),
            // then GGX-prefilter it into this cell's array layer, rescaled back to physical radiance.
            GL.BindTexture(TextureTarget.TextureCubeMap, job.CaptureCubemap);
            GL.GenerateMipmap(GenerateMipmapTarget.TextureCubeMap);
            GLEnvironmentMaps.GeneratePrefilteredInto(job.CaptureCubemap, job.TargetArray, layer,
                job.RadianceScale, ReflectionCaptureRes, ReflectionMipCount);

            job.Cursor++;
        }
        sunShadowOverride = 0;

        IrradianceVolume.BakeProgress = job.Cursor / (float)job.Total;
        IrradianceVolume.BakeStatus = $"Baking reflection probes  {job.Cursor}/{job.Total}";

        if (job.Cursor >= job.Total)
            FinishReflectionBake();
    }

    void BeginReflectionBake(ReflectionVolume vol, float preExposure) {
        var job = new ReflectionBakeJob {
            Volume = vol,
            Px = Math.Clamp(vol.ProbesX, 2, 64),
            Py = Math.Clamp(vol.ProbesY, 2, 64),
            Pz = Math.Clamp(vol.ProbesZ, 2, 64),
            RadianceScale = preExposure > 1e-6f ? 1f / preExposure : 1f,
            Watch = System.Diagnostics.Stopwatch.StartNew(),
        };
        job.Total = job.Px * job.Py * job.Pz;
        job.Size = Vector3.ComponentMax(vol.Size, Vector3.One * 0.5f);
        job.Min = vol.Center - job.Size * 0.5f;
        var far = MathF.Max(job.Size.Length, 10f) + 20f;
        job.CaptureProj = Matrix4.CreatePerspectiveFieldOfView(MathHelper.PiOver2, 1f, 0.05f, far);

        // Snapshot the renderable set with the world AABBs SplitRenderables already computed
        // this frame; the occupancy grid and per-face frustum culling both reuse these.
        job.Renderers = new IStaticMeshRenderer[opaqueItems.Count];
        job.AabbMin = new Vector3[opaqueItems.Count];
        job.AabbMax = new Vector3[opaqueItems.Count];
        for (var r = 0; r < opaqueItems.Count; r++) {
            job.Renderers[r] = opaqueItems[r].R;
            job.AabbMin[r] = opaqueItems[r].AabbMin;
            job.AabbMax[r] = opaqueItems[r].AabbMax;
        }

        // Occupancy grid (dilated by one cell), same as the diffuse bake: only cells with geometry
        // nearby get a captured cubemap; the rest fall back to the skybox.
        job.Occupied = new bool[job.Total];
        Vector3 cell = new(job.Size.X / job.Px, job.Size.Y / job.Py, job.Size.Z / job.Pz);
        for (var r = 0; r < job.Renderers.Length; r++) {
            Vector3 wMin = job.AabbMin[r] - cell;
            Vector3 wMax = job.AabbMax[r] + cell;
            if (wMax.X < job.Min.X || wMin.X > job.Min.X + job.Size.X ||
                wMax.Y < job.Min.Y || wMin.Y > job.Min.Y + job.Size.Y ||
                wMax.Z < job.Min.Z || wMin.Z > job.Min.Z + job.Size.Z)
                continue;

            var x0 = Math.Clamp((int)((wMin.X - job.Min.X) / cell.X), 0, job.Px - 1);
            var x1 = Math.Clamp((int)((wMax.X - job.Min.X) / cell.X), 0, job.Px - 1);
            var y0 = Math.Clamp((int)((wMin.Y - job.Min.Y) / cell.Y), 0, job.Py - 1);
            var y1 = Math.Clamp((int)((wMax.Y - job.Min.Y) / cell.Y), 0, job.Py - 1);
            var z0 = Math.Clamp((int)((wMin.Z - job.Min.Z) / cell.Z), 0, job.Pz - 1);
            var z1 = Math.Clamp((int)((wMax.Z - job.Min.Z) / cell.Z), 0, job.Pz - 1);

            for (var z = z0; z <= z1; z++)
            for (var y = y0; y <= y1; y++)
            for (var x = x0; x <= x1; x++)
                job.Occupied[(z * job.Py + y) * job.Px + x] = true;
        }

        // Assign a compact layer to each occupied cell until the cap; the rest get -1 (skybox).
        job.CellToLayer = new int[job.Total];
        var nextLayer = 0;
        var cappedCells = 0;
        for (var i = 0; i < job.Total; i++) {
            if (!job.Occupied[i]) {
                job.CellToLayer[i] = -1;
            }
            else if (nextLayer < MaxReflectionProbes) {
                job.CellToLayer[i] = nextLayer++;
            }
            else {
                job.CellToLayer[i] = -1;
                cappedCells++;
            }
        }
        job.OccupiedCount = nextLayer + cappedCells;
        job.LayerCount = Math.Max(nextLayer, 1); // a 0-layer array is invalid; allocate at least 1
        if (cappedCells > 0)
            Console.WriteLine($"[ReflectionVolume] occupied cells {job.OccupiedCount} exceed cap " +
                              $"{MaxReflectionProbes}; {cappedCells} cells fall back to skybox.");

        // Volume-fitted sun shadow, rendered ONCE (sun + volume static for the bake). Same as diffuse.
        Vector3 lightDir = (-sunDirection).Normalized();
        Vector3 volumeCenter = job.Min + job.Size * 0.5f;
        var radius = MathF.Max(job.Size.Length * 0.5f, 1f);
        var backup = radius * 2f + 60f;
        Vector3 shadowUp = MathF.Abs(Vector3.Dot(lightDir, Vector3.UnitY)) > 0.99f
            ? Vector3.UnitZ
            : Vector3.UnitY;
        Matrix4 lightView = Matrix4.LookAt(volumeCenter - lightDir * backup, volumeCenter, shadowUp);
        Matrix4 lightProj = Matrix4.CreateOrthographic(radius * 2f, radius * 2f, 0.1f, backup + radius * 2f);
        job.ShadowMatrix = lightView * lightProj;
        job.ShadowCompareBias = (DirectionalLight.Instance?.ShadowBias ?? 0.0015f) * 140f /
                                (backup + radius * 2f);
        job.Shadow = new GLShadowMap(2048, 2048, 1);

        GL.ColorMask(false, false, false, false);
        GL.DepthMask(true);
        GL.Enable(EnableCap.DepthTest);
        GL.Enable(EnableCap.CullFace);
        GL.CullFace(TriangleFace.Front);
        shadowDepthShader.Activate();
        job.Shadow.Bind(0);
        shadowDepthShader.SetMatrix4("lightSpaceMatrix", ref job.ShadowMatrix);
        CullInto(ref job.ShadowMatrix, opaqueItems, cullScratch);
        RenderShadowCasters(cullScratch);
        shadowDepthShader.Deactivate();
        GL.ColorMask(true, true, true, true);
        GL.CullFace(TriangleFace.Back);
        job.Shadow.Unbind();

        // Reusable capture cubemap (with mips) + its depth, and the destination cube-map array.
        job.CaptureCubemap = GL.GenTexture();
        GL.BindTexture(TextureTarget.TextureCubeMap, job.CaptureCubemap);
        for (var f = 0; f < 6; f++)
            GL.TexImage2D(TextureTarget.TextureCubeMapPositiveX + f, 0, PixelInternalFormat.Rgba16f,
                ReflectionCaptureRes, ReflectionCaptureRes, 0, PixelFormat.Rgba, PixelType.Float, IntPtr.Zero);
        var captureMips = (int)MathF.Log2(ReflectionCaptureRes) + 1;
        GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMinFilter,
            (int)TextureMinFilter.LinearMipmapLinear);
        GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMagFilter,
            (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapR, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMaxLevel, captureMips - 1);

        job.DepthRbo = GL.GenRenderbuffer();
        GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, job.DepthRbo);
        GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.DepthComponent24,
            ReflectionCaptureRes, ReflectionCaptureRes);

        job.Fbo = GL.GenFramebuffer();
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, job.Fbo);
        GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
            RenderbufferTarget.Renderbuffer, job.DepthRbo);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

        job.TargetArray = GLEnvironmentMaps.CreateCubemapArray(ReflectionCaptureRes, ReflectionMipCount, job.LayerCount);

        reflectionBake = job;
        IrradianceVolume.IsBaking = true;
        IrradianceVolume.BakeProgress = 0f;
        IrradianceVolume.BakeStatus = $"Baking reflection probes  0/{job.Total}";
        Console.WriteLine($"[ReflectionVolume] baking {job.Px}x{job.Py}x{job.Pz}: {nextLayer} local " +
                          $"probes ({job.Total - nextLayer} cells fall back to skybox)...");
    }

    void FinishReflectionBake() {
        ReflectionBakeJob job = reflectionBake;

        // Hand the live array to the renderer (so AbortReflectionBake below won't free it).
        if (reflectionArray != 0)
            GL.DeleteTexture(reflectionArray);
        reflectionArray = job.TargetArray;
        job.TargetArray = 0;

        UploadReflectionCellMap(job.Px, job.Py, job.Pz, job.CellToLayer, job.Min, job.Size);
        PublishReflectionViz(job.Px, job.Py, job.Pz, job.Min, job.Size, job.Occupied);

        // Read every layer's mip chain back ONCE for the disk cache (later loads skip the bake).
        var floatsPerLayer = ReflectionVolume.FloatsPerLayer(ReflectionCaptureRes, ReflectionMipCount);
        var cubeTexels = new float[job.LayerCount][];
        for (var l = 0; l < job.LayerCount; l++)
            cubeTexels[l] = new float[floatsPerLayer];
        ReadReflectionLayers(reflectionArray, job.LayerCount, cubeTexels);

        ReflectionVolume.SaveCache(ReflectionCacheKey(job.Volume), job.Px, job.Py, job.Pz,
            job.Volume.Center, job.Size, ReflectionCaptureRes, ReflectionMipCount, job.CellToLayer, cubeTexels);

        var mb = job.LayerCount * (double)floatsPerLayer * sizeof(float) / (1024.0 * 1024.0);
        Console.WriteLine(
            $"[ReflectionVolume] bake complete: {job.LayerCount} local probes " +
            $"({mb:F1} MB) in {job.Watch.Elapsed.TotalSeconds:F1}s (cached).");
        AbortReflectionBake();
        IrradianceVolume.BakeProgress = 1f;
    }

    // Reads each cube-array layer's full mip chain into cubeTexels[layer], in (face, mip) order to
    // match the .brp cache layout. One GetTexImage per mip drains all layers*6 face slices at that
    // mip; we then scatter the relevant slices into per-layer buffers.
    void ReadReflectionLayers(int array, int layerCount, float[][] cubeTexels) {
        GL.BindTexture(TextureTarget.TextureCubeMapArray, array);
        var offsets = new int[layerCount]; // running write cursor per layer
        for (var mip = 0; mip < ReflectionMipCount; mip++) {
            var mipSize = Math.Max(1, ReflectionCaptureRes >> mip);
            var faceFloats = mipSize * mipSize * 4;
            var whole = new float[faceFloats * 6 * layerCount];
            GL.GetTexImage(TextureTarget.TextureCubeMapArray, mip, PixelFormat.Rgba, PixelType.Float, whole);
            // whole is laid out as [depth = layer*6 + face][h][w][rgba]; copy each (layer,face) run.
            for (var l = 0; l < layerCount; l++)
            for (var f = 0; f < 6; f++) {
                var src = (l * 6 + f) * faceFloats;
                Array.Copy(whole, src, cubeTexels[l], offsets[l], faceFloats);
                offsets[l] += faceFloats;
            }
        }
    }

    void UploadReflectionCellMap(int px, int py, int pz, int[] cellToLayer, Vector3 min, Vector3 size) {
        if (reflectionCellToLayer == 0)
            reflectionCellToLayer = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture3D, reflectionCellToLayer);
        GL.TexImage3D(TextureTarget.Texture3D, 0, PixelInternalFormat.R32i, px, py, pz, 0,
            PixelFormat.RedInteger, PixelType.Int, cellToLayer);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapR, (int)TextureWrapMode.ClampToEdge);
        GL.BindTexture(TextureTarget.Texture3D, 0);

        reflectionGridX = px;
        reflectionGridY = py;
        reflectionGridZ = pz;
        reflectionVolumeMin = min;
        reflectionVolumeInvSize = new Vector3(1f / size.X, 1f / size.Y, 1f / size.Z);
        reflectionVolumeReady = true;
    }

    void PublishReflectionViz(int px, int py, int pz, Vector3 min, Vector3 size, bool[] captured) {
        var total = px * py * pz;
        var flags = new bool[total];
        for (var i = 0; i < total; i++)
            flags[i] = captured is null || captured[i];
        ReflectionVolume.Viz = new ReflectionVolume.ReflectionVizData {
            Px = px, Py = py, Pz = pz, Min = min, Size = size, Captured = flags,
        };
    }

    bool TryLoadReflectionCache(ReflectionVolume vol) {
        int px = Math.Clamp(vol.ProbesX, 2, 64);
        int py = Math.Clamp(vol.ProbesY, 2, 64);
        int pz = Math.Clamp(vol.ProbesZ, 2, 64);
        Vector3 size = Vector3.ComponentMax(vol.Size, Vector3.One * 0.5f);

        if (!ReflectionVolume.TryLoadCache(ReflectionCacheKey(vol), px, py, pz, vol.Center, size,
                ReflectionCaptureRes, ReflectionMipCount, out int[] cellToLayer, out float[][] cubeTexels))
            return false;

        var layerCount = Math.Max(cubeTexels.Length, 1);
        var array = GLEnvironmentMaps.CreateCubemapArray(ReflectionCaptureRes, ReflectionMipCount, layerCount);
        GL.BindTexture(TextureTarget.TextureCubeMapArray, array);
        for (var l = 0; l < cubeTexels.Length; l++) {
            var offset = 0;
            for (var mip = 0; mip < ReflectionMipCount; mip++) {
                var mipSize = Math.Max(1, ReflectionCaptureRes >> mip);
                var faceFloats = mipSize * mipSize * 4;
                for (var f = 0; f < 6; f++) {
                    var face = new float[faceFloats];
                    Array.Copy(cubeTexels[l], offset, face, 0, faceFloats);
                    GL.TexSubImage3D(TextureTarget.TextureCubeMapArray, mip, 0, 0, l * 6 + f,
                        mipSize, mipSize, 1, PixelFormat.Rgba, PixelType.Float, face);
                    offset += faceFloats;
                }
            }
        }
        GL.BindTexture(TextureTarget.TextureCubeMapArray, 0);

        if (reflectionArray != 0)
            GL.DeleteTexture(reflectionArray);
        reflectionArray = array;
        UploadReflectionCellMap(px, py, pz, cellToLayer, vol.Center - size * 0.5f, size);
        PublishReflectionViz(px, py, pz, vol.Center - size * 0.5f, size, null);
        Console.WriteLine("[ReflectionVolume] loaded baked reflections from cache (no re-bake).");
        return true;
    }

    void AbortReflectionBake() {
        if (reflectionBake is null)
            return;
        GL.DeleteFramebuffer(reflectionBake.Fbo);
        GL.DeleteTexture(reflectionBake.CaptureCubemap);
        GL.DeleteRenderbuffer(reflectionBake.DepthRbo);
        if (reflectionBake.TargetArray != 0)   // only set if the bake didn't finish (else handed off)
            GL.DeleteTexture(reflectionBake.TargetArray);
        reflectionBake.Shadow?.Dispose();
        reflectionBake = null;
        sunShadowOverride = 0;
        IrradianceVolume.IsBaking = false;
    }

    // Gizmo visualization: pre-exposed average irradiance per probe (c0 carries L_avg * 3.5449)
    // so the selected volume shows what each probe actually captured.
    void PublishProbeViz(int px, int py, int pz, Vector3 min, Vector3 size, float[][] sh, bool[] captured) {
        var total = px * py * pz;
        var colors = new Vector3[total];
        var flags = new bool[total];
        var exposure = PostFX.ExposureMultiplier;
        for (var i = 0; i < total; i++) {
            var b = i * 4;
            colors[i] = new Vector3(sh[0][b], sh[0][b + 1], sh[0][b + 2]) * (0.2821f * exposure);
            flags[i] = captured is null || captured[i];
        }
        IrradianceVolume.Viz = new IrradianceVolume.ProbeVizData {
            Px = px, Py = py, Pz = pz, Min = min, Size = size, Colors = colors, Captured = flags,
        };
    }

    static string ProbeCacheKey(IrradianceVolume vol) =>
        vol.DeriveCacheKey(SceneManager.GetCurrentScene()?.Name);

    bool TryLoadProbeCache(IrradianceVolume vol) {
        int px = Math.Clamp(vol.ProbesX, 2, 64);
        int py = Math.Clamp(vol.ProbesY, 2, 64);
        int pz = Math.Clamp(vol.ProbesZ, 2, 64);
        Vector3 size = Vector3.ComponentMax(vol.Size, Vector3.One * 0.5f);

        var sh = new float[4][];
        for (var t = 0; t < 4; t++)
            sh[t] = new float[px * py * pz * 4];
        if (!IrradianceVolume.TryLoadCache(ProbeCacheKey(vol), px, py, pz, vol.Center, size, sh))
            return false;

        UploadProbeTextures(px, py, pz, sh, vol.Center - size * 0.5f, size);
        PublishProbeViz(px, py, pz, vol.Center - size * 0.5f, size, sh, null);
        Console.WriteLine("[ProbeVolume] loaded baked probes from cache (no re-bake).");
        return true;
    }

    int probeDimX, probeDimY, probeDimZ; // grid dims of the uploaded volume (debug spheres)

    void UploadProbeTextures(int px, int py, int pz, float[][] sh, Vector3 min, Vector3 size) {
        probeDimX = px;
        probeDimY = py;
        probeDimZ = pz;
        for (var t = 0; t < 4; t++) {
            if (probeSH[t] == 0)
                probeSH[t] = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture3D, probeSH[t]);
            GL.TexImage3D(TextureTarget.Texture3D, 0, PixelInternalFormat.Rgba16f, px, py, pz,
                0, PixelFormat.Rgba, PixelType.Float, sh[t]);
            GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapR, (int)TextureWrapMode.ClampToEdge);
        }
        GL.BindTexture(TextureTarget.Texture3D, 0);

        probeVolumeMin = min;
        probeVolumeInvSize = new Vector3(1f / size.X, 1f / size.Y, 1f / size.Z);
        probeVolumeReady = true;
    }

    static void StoreSH(float[] target, int index, Vector3 value) {
        target[index] = Math.Clamp(value.X, -60000f, 60000f);
        target[index + 1] = Math.Clamp(value.Y, -60000f, 60000f);
        target[index + 2] = Math.Clamp(value.Z, -60000f, 60000f);
        target[index + 3] = 1f;
    }

    int iblSourceVersion;

    void UpdateEnvironmentMaps() {
        Texture3D current = skyboxRenderer.cubemapTexture;
        // Runtime cubemaps (procedural sky) keep the same wrapper instance across re-bakes;
        // their ContentVersion says when the texels changed and the IBL must re-convolve.
        var version = (current as GLRuntimeCubemap)?.ContentVersion ?? 0;
        if (ReferenceEquals(current, iblSource) && version == iblSourceVersion)
            return;
        // ContentVersion bumps (animated procedural-sky clouds) re-convolve silently;
        // only an actual source switch is worth a log line.
        var sourceChanged = !ReferenceEquals(current, iblSource);
        iblSourceVersion = version;

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
        if (sourceChanged)
            Console.WriteLine("IBL environment maps baked for active skybox.");
    }

    void UpdateSkyRotation() {
        // The procedural sky is sun-oriented already; rotation only applies to HDRI skies.
        Vector3 euler = ProceduralSky.Active is { IsActive: true }
            ? Vector3.Zero
            : Skybox.Active?.RotationEuler ?? Vector3.Zero;
        skyRotation =
            Matrix4.CreateRotationX(MathHelper.DegreesToRadians(euler.X)) *
            Matrix4.CreateRotationY(MathHelper.DegreesToRadians(euler.Y)) *
            Matrix4.CreateRotationZ(MathHelper.DegreesToRadians(euler.Z));
    }

    // Draws each target's submeshes that match the requested blend mode, binding that
    // submesh's material per range. Multi-material meshes thus split correctly across the
    // opaque and transparent passes.
    void RenderMeshes(List<IStaticMeshRenderer> targets, bool transparentPass, ref Matrix4 view,
        ref Matrix4 projection, Vector3 cameraPos, bool prepassDepth = false) {
        using var profileZone = Profiler.Zone(transparentPass ? "HD.Meshes.Transparent" : "HD.Meshes.Opaque");

        GL.Enable(EnableCap.DepthTest);
        // With a z-prepass the depth buffer is already final: re-test EQUAL (LEqual; the
        // invariant vertex math guarantees equality) and skip the redundant depth writes.
        GL.DepthFunc(prepassDepth ? DepthFunction.Lequal : DepthFunction.Less);
        if (prepassDepth)
            GL.DepthMask(false);
        GL.CullFace(TriangleFace.Back);
        GL.Enable(EnableCap.CullFace);
        GL.FrontFace(FrontFaceDirection.Ccw);

        // Pass-constant data goes up ONCE per pass through the PassData UBO (filled by the
        // first lit program seen, bound to every program that declares it); each draw then
        // only uploads its material block + model matrix. Legacy shaders without the block
        // get the old ~150-glUniform upload per program instead.
        passDataFilled = false;
        SetPassTextures();
        Shader passShader = null;
        Material lastMaterialUniforms = null;

        for (var t = 0; t < targets.Count; t++) {
            IStaticMeshRenderer target = targets[t];
            Mesh mesh = target.SharedMesh;
            SubMeshData[] subMeshes = mesh.SubMeshes;

            // Adjacent identical (mesh, submesh, material) renderers — the opaque sort makes
            // them consecutive — collapse into ONE instanced draw. Transparency keeps its
            // per-object back-to-front order, so runs apply to the opaque pass only.
            if (!transparentPass) {
                var run = InstancedRunLength(targets, t);
                if (run >= 2) {
                    Material runMaterial = target.MaterialFor(target.SubMeshIndex);
                    runMaterial.Activate();
                    Shader runShader = runMaterial.Shader;
                    if (!ReferenceEquals(runShader, passShader)) {
                        SetupProgramForPass(runShader, ref view, ref projection, cameraPos);
                        passShader = runShader;
                        lastMaterialUniforms = null;
                    }
                    if (!ReferenceEquals(runMaterial, lastMaterialUniforms)) {
                        SetMaterialUniforms(runShader, runMaterial);
                        lastMaterialUniforms = runMaterial;
                    }

                    mesh.Activate();
                    DrawInstancedRun(targets, t, run, mesh, subMeshes[target.SubMeshIndex],
                        runShader, runMaterial.Cutout);
                    mesh.Deactivate();
                    t += run - 1;
                    continue;
                }
            }

            Matrix4 modelMatrix = ModelMatrix(target, mesh);
            mesh.Activate();

            Material lastActivated = null;
            (int first, int end) = SubMeshRange(target, subMeshes.Length);
            for (var i = first; i < end; i++) {
                Material material = target.MaterialFor(i);
                if (material is null || material.Transparent != transparentPass)
                    continue;

                material.Activate();
                lastActivated = material;

                Shader shader = material.Shader;
                if (!ReferenceEquals(shader, passShader)) {
                    SetupProgramForPass(shader, ref view, ref projection, cameraPos);
                    passShader = shader;
                    lastMaterialUniforms = null;
                }
                if (!ReferenceEquals(material, lastMaterialUniforms)) {
                    SetMaterialUniforms(shader, material);
                    lastMaterialUniforms = material;
                }
                shader.SetMatrix4("model", ref modelMatrix);

                if (material.Cutout)
                    GL.Disable(EnableCap.CullFace); // leaf cards have no back faces
                GL.DrawElements(PrimitiveType.Triangles, subMeshes[i].IndexCount, DrawElementsType.UnsignedInt,
                    (IntPtr)(subMeshes[i].IndexStart * sizeof(uint)));
                if (material.Cutout)
                    GL.Enable(EnableCap.CullFace);
                anythingDrawnThisFrame = true;
                stats.DrawCalls++;
                stats.Triangles += subMeshes[i].IndexCount / 3;
            }

            lastActivated?.Deactivate();
            mesh.Deactivate();
        }

        if (prepassDepth) {
            GL.DepthMask(true);
            GL.DepthFunc(DepthFunction.Less);
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
        Vector3 cameraPos = args.viewProjectionProvider.Transform.WorldPosition;
        cullScratch.Clear();
        foreach (IStaticMeshRenderer target in renderTargets)
            if (target.IsRenderable && target.IsActive)
                cullScratch.Add(target);
        RenderMeshes(cullScratch, transparentPass: false, ref view, ref projection, cameraPos);
    }

    // Length of the run of consecutive renderers drawing the SAME (mesh, submesh, material) —
    // instancing candidates. Only single-submesh renderers participate; the opaque sort made
    // identical ones adjacent.
    static int InstancedRunLength(List<IStaticMeshRenderer> list, int start) {
        IStaticMeshRenderer first = list[start];
        var sub = first.SubMeshIndex;
        if (sub < 0)
            return 1;
        Mesh mesh = first.SharedMesh;
        if (sub >= mesh.SubMeshes.Length)
            return 1;
        Material material = first.MaterialFor(sub);
        if (material is null)
            return 1;

        var n = 1;
        while (start + n < list.Count) {
            IStaticMeshRenderer next = list[start + n];
            if (next.SubMeshIndex != sub || !ReferenceEquals(next.SharedMesh, mesh) ||
                !ReferenceEquals(next.MaterialFor(sub), material))
                break;
            n++;
        }

        return n;
    }

    // One instanced draw for a run: model matrices stream into the mesh's per-instance
    // buffer (attribs 4-7), the vertex shader's isInstanced path reads them back with the
    // exact uniform-path convention so prepass/main depth equality holds.
    void DrawInstancedRun(List<IStaticMeshRenderer> list, int start, int count, Mesh mesh,
        SubMeshData subMesh, Shader shader, bool cutout) {
        Matrix4[] matrices = ArrayPool<Matrix4>.Shared.Rent(count);
        for (var i = 0; i < count; i++)
            matrices[i] = ModelMatrix(list[start + i], mesh);
        mesh.InstanceBuffer.SetBufferData(matrices, BufferUsageHint.StreamDraw);
        ArrayPool<Matrix4>.Shared.Return(matrices);

        shader.SetBool("isInstanced", true);
        if (cutout)
            GL.Disable(EnableCap.CullFace);
        GL.DrawElementsInstanced(PrimitiveType.Triangles, subMesh.IndexCount, DrawElementsType.UnsignedInt,
            (IntPtr)(subMesh.IndexStart * sizeof(uint)), count);
        if (cutout)
            GL.Enable(EnableCap.CullFace);
        shader.SetBool("isInstanced", false);

        anythingDrawnThisFrame = true;
        stats.DrawCalls++;
        stats.InstancedDrawCalls++;
        stats.DrawsSavedByInstancing += count - 1;
        stats.Triangles += (long)subMesh.IndexCount / 3 * count;
    }

    // The matrix a renderer's submeshes draw with. Whole-mesh renderers use the entity world
    // matrix as-is (node placements are baked into the vertices). Single-submesh renderers
    // carry the node's transform ON the entity, so the baked placement is undone first:
    // model = inverseNode * world (row-vector convention, submesh-local first).
    static Matrix4 ModelMatrix(IStaticMeshRenderer target, Mesh mesh) {
        Matrix4 world = target.Transform.WorldMatrix;
        var index = target.SubMeshIndex;
        return index >= 0 && index < mesh.InverseNodeTransforms.Length
            ? mesh.InverseNodeTransforms[index] * world
            : world;
    }

    // The [first, end) submesh range a renderer draws: everything for SubMeshIndex -1, that
    // one submesh otherwise. A stale out-of-range index draws nothing (drawing the whole mesh
    // would double-draw geometry its sibling entities already cover).
    static (int first, int end) SubMeshRange(IStaticMeshRenderer target, int subMeshCount) {
        var index = target.SubMeshIndex;
        if (index < 0)
            return (0, subMeshCount);
        return index < subMeshCount ? (index, index + 1) : (0, 0);
    }

    // Per-MATERIAL uniforms only — everything a draw actually changes besides the model
    // matrix. Kept tiny on purpose; the heavy state lives in SetPassUniforms.
    void SetMaterialUniforms(Shader shader, Material material) {
        shader.SetFloat4("BaseColorFactor", material.BaseColorFactor);
        shader.SetFloat("MetallicMultiplier", material.MetallicFactor * Metallic);
        shader.SetFloat("RoughnessMultiplier", material.RoughnessFactor * RoughnessValue);
        shader.SetBool("PackedOrm", material.PackedOrm);
        shader.SetBool("HasMetallicMap", material.Metallic is not null);
        shader.SetBool("HasRoughnessMap", material.Roughness is not null);
        shader.SetBool("NormalFlipY", material.NormalFlipY);
        shader.SetFloat("NormalStrength", material.NormalStrength * NormalStrength);
        shader.SetFloat3("EmissiveFactor", material.EmissiveColor * material.EmissiveIntensity);
        shader.SetBool("HasEmissive", material.Emissive is not null);
        shader.SetBool("AlphaBlend", material.Transparent);
        shader.SetFloat("Opacity", material.Opacity);
        shader.SetBool("AlphaCutout", material.Cutout);
        shader.SetBool("ContactShadowsOn", PostFX.ContactShadowsEnabled);
        shader.SetFloat("ContactShadowLength", PostFX.ContactShadowLength);
        shader.SetInt("ContactShadowSteps", PostFX.ContactShadowSteps);
        shader.SetFloat("ContactShadowThickness", PostFX.ContactShadowThickness);
    }

    // Binds the pass UBO to this program (filling it on the first program of the pass) and
    // assigns sampler units once per program ever. Programs without a PassData block fall
    // back to the legacy per-uniform storm.
    void SetupProgramForPass(Shader shader, ref Matrix4 view, ref Matrix4 projection, Vector3 cameraPos) {
        if (passData.RegisterProgram(shader.UID)) {
            if (!passDataFilled) {
                FillPassData(ref view, ref projection, cameraPos);
                passDataFilled = true;
            }
            passData.UploadAndBind();
            if (samplerReadyPrograms.Add(shader.UID))
                SetSamplerUniforms(shader);
        }
        else {
            SetPassUniformsLegacy(shader, ref view, ref projection, cameraPos);
        }
    }

    // Texture-unit assignments are program STATE: once per program, not per pass.
    void SetSamplerUniforms(Shader shader) {
        shader.SetInt("Diffuse", 0);
        shader.SetInt("Normal", 1);
        shader.SetInt("Metallic", 2);
        shader.SetInt("Roughness", 3);
        shader.SetInt("AO", 4);
        shader.SetInt("Emissive", 5);
        shader.SetInt("SceneDepth", 7);
        shader.SetInt("ScreenAO", 8);
        shader.SetInt("PunctualShadows", 9);
        shader.SetInt("ShadowCascades", 10);
        shader.SetInt("Skybox", 11);
        shader.SetInt("IrradianceMap", 12);
        shader.SetInt("PrefilteredEnvMap", 13);
        shader.SetInt("BRDF_LUT", 14);
        shader.SetInt("ProbeSH0", 15);
        shader.SetInt("ProbeSH1", 16);
        shader.SetInt("ProbeSH2", 17);
        shader.SetInt("ProbeSH3", 18);
        shader.SetInt("ShadowCascadesRaw", 19);
        shader.SetInt("ReflectionProbes", 20);
        shader.SetInt("ReflectionCellToLayer", 21);
    }

    // The pass-global texture binds (the units SetSamplerUniforms assigned). Once per pass.
    void SetPassTextures() {
        GL.ActiveTexture(TextureUnit.Texture7);
        GL.BindTexture(TextureTarget.Texture2D, prepassDepthCopy);
        GL.ActiveTexture(TextureUnit.Texture8);
        GL.BindTexture(TextureTarget.Texture2D, screenAoTexture);
        GL.ActiveTexture(TextureUnit.Texture9);
        GL.BindTexture(TextureTarget.Texture2DArray, punctualShadows.DepthTextureId);

        // Probe captures override the sun shadow with the bake's volume-fitted map. Unit 19 is
        // the same texture through the raw (non-compare) sampler object for PCSS blocker reads.
        var sunShadowArray = sunShadowOverride != 0 ? sunShadowOverride : shadowMap.DepthTextureId;
        GL.ActiveTexture(TextureUnit.Texture10);
        GL.BindTexture(TextureTarget.Texture2DArray, sunShadowArray);
        GL.ActiveTexture(TextureUnit.Texture19);
        GL.BindTexture(TextureTarget.Texture2DArray, sunShadowArray);

        // Sky reflections fallback needs the cubemap bound during the lit pass too (the skybox
        // draw only binds it afterwards, which would leave unit 11 empty on the first frame).
        if (skyboxRenderer.cubemapTexture is not null) {
            GL.ActiveTexture(TextureUnit.Texture11);
            GL.BindTexture(TextureTarget.TextureCubeMap, skyboxRenderer.cubemapTexture.UID);
        }

        GL.ActiveTexture(TextureUnit.Texture12);
        GL.BindTexture(TextureTarget.TextureCubeMap, irradianceMap);
        GL.ActiveTexture(TextureUnit.Texture13);
        GL.BindTexture(TextureTarget.TextureCubeMap, prefilteredMap);
        GL.ActiveTexture(TextureUnit.Texture14);
        GL.BindTexture(TextureTarget.Texture2D, brdfLut);

        if (probeVolumeReady) {
            GL.ActiveTexture(TextureUnit.Texture15);
            GL.BindTexture(TextureTarget.Texture3D, probeSH[0]);
            GL.ActiveTexture(TextureUnit.Texture16);
            GL.BindTexture(TextureTarget.Texture3D, probeSH[1]);
            GL.ActiveTexture(TextureUnit.Texture17);
            GL.BindTexture(TextureTarget.Texture3D, probeSH[2]);
            GL.ActiveTexture(TextureUnit.Texture18);
            GL.BindTexture(TextureTarget.Texture3D, probeSH[3]);
        }

        if (reflectionVolumeReady) {
            GL.ActiveTexture(TextureUnit.Texture20);
            GL.BindTexture(TextureTarget.TextureCubeMapArray, reflectionArray);
            GL.ActiveTexture(TextureUnit.Texture21);
            GL.BindTexture(TextureTarget.Texture3D, reflectionCellToLayer);
        }

        GL.ActiveTexture(TextureUnit.Texture0); // material binds expect unit 0 active
    }

    // Writes every PassData member from the renderer's current per-frame fields. Called once
    // per RenderMeshes call (the bake re-fills per face with its own view/projection).
    void FillPassData(ref Matrix4 view, ref Matrix4 projection, Vector3 cameraPos) {
        GLUniformBlock b = passData;
        b.Set("view", ref view);
        b.Set("projection", ref projection);
        b.Set("SkyRotation", ref skyRotation);
        for (var i = 0; i < activeCascadeCount; i++)
            b.Set("CascadeMatrices", i, ref cascadeMatrices[i]);
        for (var s = 0; s < shadowedSpotCount; s++)
            b.Set("SpotShadowMatrix", s, ref spotShadowMatrices[s]);
        for (var p = 0; p < shadowedPointCount * 6; p++)
            b.Set("PointShadowMatrix", p, ref pointShadowMatrices[p]);

        b.Set("CascadeBias", cascadeBias);
        b.Set("CascadeTexelWorld", cascadeTexelWorld);
        b.Set("CascadeDepthRangeW", cascadeDepthWorld);

        b.Set("CameraPos", cameraPos);
        b.Set("ShadowStrength", shadowStrength);
        b.Set("LightDirection", sunDirection);
        b.Set("SunAngularRadius",
            MathHelper.DegreesToRadians(DirectionalLight.Instance?.AngularDiameter ?? 0.53f) * 0.5f);
        b.Set("LightColor", sunColor);
        b.Set("CascadeBlend", cascadeBlend);
        b.Set("AmbientLight", ambientFallback);
        b.Set("ShadowSoftness", MathF.Max(PostFX.ShadowSoftness, 0.01f));
        b.Set("ShadowColor", shadowColor);
        // Small floor only against true-zero roughness (degenerate NDF); real mirrors stay
        // mirrors. (See the old uniform path for history.)
        b.Set("minRoughness", 0.045f);
        b.Set("AmbientTint", ambientTint);
        b.Set("ReflectionIntensity", reflectionIntensity);
        b.Set("FogColor", sceneFogColor);
        b.Set("FogDensity", sceneFogDensity);
        b.Set("ProbeVolumeMin", probeVolumeMin);
        b.Set("ProbeExposure", PostFX.ExposureMultiplier);
        b.Set("ProbeVolumeInvSize", probeVolumeInvSize);
        // Sky luminance scale x camera pre-exposure (see the pre-exposure note in Render).
        b.Set("SkyExposure", skyExposureBase * PostFX.ExposureMultiplier);
        b.Set("ReflectionVolumeMin", reflectionVolumeMin);
        b.Set("MaxPrefilterMips", (float)(GLEnvironmentMaps.PrefilterMipCount - 1));
        b.Set("ReflectionVolumeInvSize", reflectionVolumeInvSize);
        b.Set("ReflectionMaxMips", (float)(ReflectionMipCount - 1));

        for (var i = 0; i < pointLightCount; i++) {
            b.Set("PointLightPosition", i, pointPositions[i]);
            b.Set("PointLightColor", i, pointColors[i]);
            b.Set("PointLightRange", i, pointRanges[i]);
            b.Set("PointShadowSlot", i, pointShadowSlots[i]);
        }

        for (var i = 0; i < spotLightCount; i++) {
            b.Set("SpotLightPosition", i, spotPositions[i]);
            b.Set("SpotLightDirection", i, spotDirections[i]);
            b.Set("SpotLightColor", i, spotColors[i]);
            b.Set("SpotLightRange", i, spotRanges[i]);
            b.Set("SpotLightCosInner", i, spotCosInner[i]);
            b.Set("SpotLightCosOuter", i, spotCosOuter[i]);
            b.Set("SpotShadowSlot", i, spotShadowSlots[i]);
        }

        for (var s = 0; s < shadowedSpotCount; s++)
            b.Set("SpotShadowBias", s, spotShadowBiases[s]);
        for (var p = 0; p < shadowedPointCount; p++)
            b.Set("PointShadowBias", p, pointShadowBiases[p]);

        b.Set("ScreenSize", screenAoTargetSize);
        b.Set("PointLightCount", pointLightCount);
        b.Set("SpotLightCount", spotLightCount);
        b.Set("CascadeCount", activeCascadeCount);
        b.Set("ShadowFiltering", Math.Clamp(PostFX.ShadowFiltering, 0, 2));
        b.Set("renderMode", renderMode);

        // The ReflectionVolume's IsActive is the live master switch: off -> the shader ignores
        // the volume and glossy surfaces fall back to the global skybox, no re-bake needed.
        ReflectionVolume reflVol = ReflectionVolume.Active is { IsActive: true } rv ? rv : null;
        var reflectionsLive = reflectionVolumeReady && reflVol is not null;
        b.Set("ReflectionGridX", reflectionGridX);
        b.Set("ReflectionGridY", reflectionGridY);
        b.Set("ReflectionGridZ", reflectionGridZ);
        b.Set("UseIBL", irradianceMap != 0);
        b.Set("UseProbeVolume", probeVolumeReady);
        b.Set("UseReflectionVolume", reflectionsLive);
        b.Set("HasScreenAO", screenAoTexture != 0);
        b.Set("ReflectionBlendWithSky", reflVol?.BlendWithSky ?? false);
        b.Set("EnableAtmosphericScattering", sceneFogEnabled);
        b.Set("ReflectionIntensityLocal", MathF.Max(reflVol?.Intensity ?? 0f, 0f));
    }

    // LEGACY: per-uniform upload for shaders that predate the PassData block.
    void SetPassUniformsLegacy(Shader shader, ref Matrix4 view, ref Matrix4 projection, Vector3 cameraPos) {
        // Sun + ambient (resolved once per frame in BeginRender).
        shader.SetFloat3("LightDirection", sunDirection);
        shader.SetFloat3("LightColor", sunColor);
        shader.SetFloat3("AmbientLight", ambientFallback);

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

        // Punctual shadows: per-light slots, per-slot matrices/bias, and the depth array.
        for (var i = 0; i < pointLightCount; i++)
            shader.SetInt(PointShadowSlotNames[i], pointShadowSlots[i]);
        for (var i = 0; i < spotLightCount; i++)
            shader.SetInt(SpotShadowSlotNames[i], spotShadowSlots[i]);
        for (var s = 0; s < shadowedSpotCount; s++) {
            shader.SetMatrix4(SpotShadowMatrixNames[s], ref spotShadowMatrices[s]);
            shader.SetFloat(SpotShadowBiasNames[s], spotShadowBiases[s]);
        }
        for (var p = 0; p < shadowedPointCount; p++) {
            shader.SetFloat(PointShadowBiasNames[p], pointShadowBiases[p]);
            for (var f = 0; f < 6; f++)
                shader.SetMatrix4(PointShadowMatrixNames[p * 6 + f], ref pointShadowMatrices[p * 6 + f]);
        }
        GL.ActiveTexture(TextureUnit.Texture9);
        GL.BindTexture(TextureTarget.Texture2DArray, punctualShadows.DepthTextureId);
        shader.SetInt("PunctualShadows", 9);

        // Screen-space AO from the depth prepass (TAA path only; 0 = absent).
        GL.ActiveTexture(TextureUnit.Texture8);
        GL.BindTexture(TextureTarget.Texture2D, screenAoTexture);
        shader.SetInt("ScreenAO", 8);
        shader.SetBool("HasScreenAO", screenAoTexture != 0);
        shader.SetFloat2("ScreenSize", screenAoTargetSize);

        // Small floor only against true-zero roughness (degenerate NDF); real mirrors stay
        // mirrors. The old 0.08 floor banned sharp reflections scene-wide - the grazing-rim
        // problem it fought is now handled by the single specular-occlusion term.
        shader.SetFloat("minRoughness", 0.045f);

        // Sun disk angular radius (degrees diameter on the component -> radians radius here).
        shader.SetFloat("SunAngularRadius",
            MathHelper.DegreesToRadians(DirectionalLight.Instance?.AngularDiameter ?? 0.53f) * 0.5f);

        // Shadows (cascaded) + scene lighting environment.
        for (var i = 0; i < activeCascadeCount; i++)
            shader.SetMatrix4(CascadeMatrixNames[i], ref cascadeMatrices[i]);
        shader.SetFloat4("CascadeBias", cascadeBias);
        shader.SetInt("CascadeCount", activeCascadeCount);
        shader.SetFloat("CascadeBlend", cascadeBlend);
        shader.SetInt("ShadowFiltering", Math.Clamp(PostFX.ShadowFiltering, 0, 2));
        shader.SetFloat("ShadowSoftness", MathF.Max(PostFX.ShadowSoftness, 0.01f));
        shader.SetFloat4("CascadeTexelWorld", cascadeTexelWorld);
        shader.SetFloat4("CascadeDepthRangeW", cascadeDepthWorld);
        shader.SetFloat3("ShadowColor", shadowColor);
        shader.SetFloat("ShadowStrength", shadowStrength);
        shader.SetFloat3("AmbientTint", ambientTint);
        shader.SetFloat("ReflectionIntensity", reflectionIntensity);
        shader.SetFloat3("FogColor", sceneFogColor);
        shader.SetFloat("FogDensity", sceneFogDensity);
        // Probe captures override the sun shadow with the bake's volume-fitted map.
        var sunShadowArray = sunShadowOverride != 0 ? sunShadowOverride : shadowMap.DepthTextureId;
        GL.ActiveTexture(TextureUnit.Texture10);
        GL.BindTexture(TextureTarget.Texture2DArray, sunShadowArray);
        // Same texture again on unit 19, where the raw (non-compare) sampler object is bound:
        // PCSS reads blocker depths there while unit 10 keeps hardware-compare PCF taps.
        GL.ActiveTexture(TextureUnit.Texture19);
        GL.BindTexture(TextureTarget.Texture2DArray, sunShadowArray);
        shader.SetInt("ShadowCascadesRaw", 19);

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
        // Sky luminance scale x camera pre-exposure (see the pre-exposure note in Render).
        shader.SetFloat("SkyExposure", skyExposureBase * PostFX.ExposureMultiplier);
        shader.SetMatrix4("SkyRotation", ref skyRotation);

        // Baked probe volume: position-aware diffuse irradiance, re-exposed at sample time
        // (the SH grid stores physical, un-exposed values).
        shader.SetBool("UseProbeVolume", probeVolumeReady);
        if (probeVolumeReady) {
            GL.ActiveTexture(TextureUnit.Texture15);
            GL.BindTexture(TextureTarget.Texture3D, probeSH[0]);
            GL.ActiveTexture(TextureUnit.Texture16);
            GL.BindTexture(TextureTarget.Texture3D, probeSH[1]);
            GL.ActiveTexture(TextureUnit.Texture17);
            GL.BindTexture(TextureTarget.Texture3D, probeSH[2]);
            GL.ActiveTexture(TextureUnit.Texture18);
            GL.BindTexture(TextureTarget.Texture3D, probeSH[3]);
            shader.SetInt("ProbeSH0", 15);
            shader.SetInt("ProbeSH1", 16);
            shader.SetInt("ProbeSH2", 17);
            shader.SetInt("ProbeSH3", 18);
            shader.SetFloat3("ProbeVolumeMin", probeVolumeMin);
            shader.SetFloat3("ProbeVolumeInvSize", probeVolumeInvSize);
            shader.SetFloat("ProbeExposure", PostFX.ExposureMultiplier);
        }

        // Baked reflection volume: local prefiltered specular cubemaps (cube-map array + cell->layer
        // map). The cubes store PHYSICAL radiance, re-exposed by SkyExposure (already set above) at
        // sample time exactly like the global prefiltered map, so local and sky reflections match EV.
        // The component's IsActive (IsEnabled toggle) is the live master switch: off -> the shader
        // ignores the volume and glossy surfaces fall back to the global skybox, no re-bake needed.
        ReflectionVolume reflVol = ReflectionVolume.Active is { IsActive: true } rv ? rv : null;
        var reflectionsLive = reflectionVolumeReady && reflVol is not null;
        shader.SetBool("UseReflectionVolume", reflectionsLive);
        if (reflectionsLive) {
            GL.ActiveTexture(TextureUnit.Texture20);
            GL.BindTexture(TextureTarget.TextureCubeMapArray, reflectionArray);
            GL.ActiveTexture(TextureUnit.Texture21);
            GL.BindTexture(TextureTarget.Texture3D, reflectionCellToLayer);
            shader.SetInt("ReflectionProbes", 20);
            shader.SetInt("ReflectionCellToLayer", 21);
            shader.SetFloat3("ReflectionVolumeMin", reflectionVolumeMin);
            shader.SetFloat3("ReflectionVolumeInvSize", reflectionVolumeInvSize);
            shader.SetInt("ReflectionGridX", reflectionGridX);
            shader.SetInt("ReflectionGridY", reflectionGridY);
            shader.SetInt("ReflectionGridZ", reflectionGridZ);
            shader.SetFloat("ReflectionMaxMips", ReflectionMipCount - 1);
            shader.SetFloat("ReflectionIntensityLocal", MathF.Max(reflVol.Intensity, 0f));
            shader.SetBool("ReflectionBlendWithSky", reflVol.BlendWithSky);
        }

        // Sampler slots.
        shader.SetInt("Diffuse", 0);
        shader.SetInt("Normal", 1);
        shader.SetInt("Metallic", 2);
        shader.SetInt("Roughness", 3);
        shader.SetInt("AO", 4);
        shader.SetInt("Emissive", 5);
        shader.SetInt("SceneDepth", 7);
        shader.SetInt("ShadowCascades", 10);
        shader.SetInt("Skybox", 11);
        shader.SetInt("IrradianceMap", 12);
        shader.SetInt("PrefilteredEnvMap", 13);
        shader.SetInt("BRDF_LUT", 14);

        shader.SetInt("renderMode", renderMode);
        shader.SetBool("EnableAtmosphericScattering", sceneFogEnabled);
        shader.SetMatrix4("view", ref view);
        shader.SetMatrix4("projection", ref projection);
        shader.SetFloat3("CameraPos", cameraPos);
    }

    // Global normal-map intensity multiplier (on top of each material's NormalStrength). 1.5
    // makes the common subtle game normal maps read with proper surface relief out of the box;
    // it scales tangent-space XY in the shader, so it can push past the authored amplitude.
    public float NormalStrength { get; set; } = 1.5f;

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
