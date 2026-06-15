using OpenTK.Mathematics;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace BallisticEngine;

// A no-GPU IBallisticEngineRuntime for headless hosts (bal simulate, future test runners):
// EngineBootstrap runs end-to-end — scripts compile, assets import, scenes load, physics and
// scripts PLAY — with every render-asset call absorbed by null objects. Lives in the engine
// library (not the CLI) because IEngineTimer.Update is internal to this assembly.
//
// What works headless: the full play loop (SceneManager.Update -> FixedTick/Tick, Bepu physics,
// contact events, particles/trails CPU sim, audio graceful-degrade, BEvents). What doesn't:
// anything that renders (HDCamera.RenderCamera, screenshots) — no host calls it here.
public sealed class HeadlessRuntime : IBallisticEngineRuntime {
    public event Action<double> WindowUpdateCallback { add { } remove { } }
    public event Action<double> WindowRenderCallback { add { } remove { } }
    public event Action OnWindowShow { add { } remove { } }

    // input: a ScriptedInput for deterministic playback (bal simulate --input), or null for
    // no input at all.
    public HeadlessRuntime(IInputProvider input = null) =>
        InputProvider = input ?? new NullInput();

    public IEngineTimer EngineTimer { get; } = new ManualTimer();
    public IInputProvider InputProvider { get; }
    public IWindow Window { get; } = new NullWindow();
    public RenderAsset RenderAsset { get; } = new NullRenderAsset();
    public ILogger Logger => null; // hosts subscribe Debugging.OnMessage instead

    // Host-driven clock: EngineBootstrap.UpdateFrame(delta) advances it; nothing else does.
    sealed class ManualTimer : IEngineTimer {
        public double DeltaTime { get; private set; }
        public double TotalTime { get; private set; }

        void IEngineTimer.Update(double deltaTime) {
            DeltaTime = deltaTime;
            TotalTime += deltaTime;
        }
    }

    sealed class NullInput : IInputProvider {
        public bool IsKeyDown(Keys key) => false;
        public bool IsKeyPressed(Keys key) => false;
        public bool IsMouseButtonPressed(MouseButton button) => false;
        public bool IsMouseButtonDown(MouseButton button) => false;
        public Vector2 ScrollDelta => Vector2.Zero;
        public Vector2 MousePosition => Vector2.Zero;
        public Vector2 MouseDelta => Vector2.Zero;
        public bool IsGamepadConnected(int playerIndex) => false;
        public bool IsGamepadButtonDown(int playerIndex, int button) => false;
        public bool IsGamepadButtonPressed(int playerIndex, int button) => false;
        public float GetGamepadAxis(int playerIndex, int axis) => 0f;
    }

    sealed class NullWindow : IWindow {
        public int Width => 1280;
        public int Height => 720;
        public void SetFrequency(int frequency) { }
        public void Run() { }
        public void Close() { }
        public void SwapFrameBuffers() { }
        public float FrameRate => 60f;
        public event Action<int, int> OnResizeCallback { add { } remove { } }
        public CursorMode CursorMode { get; set; }
    }

    // Render-asset null objects: Mesh/Texture constructors run (assets load with their CPU data,
    // which colliders and gameplay need); every GPU call is a no-op.
    sealed class NullRenderAsset : RenderAsset {
        public NullRenderAsset() => Current = this;

        public override HDRenderer Renderer { get => null; protected set { } }
        public override void Initialize() { }
        public override RenderContext CreateRenderContext() => new NullRenderContext();
        public override GPUBuffer<uint> CreateIndexBuffer(RenderContext renderContext) => new NullBuffer<uint>(renderContext);
        public override GPUBuffer<Vector3> CreateVertexBuffer3(RenderContext renderContext) => new NullBuffer<Vector3>(renderContext);
        public override GPUBuffer<Vector2> CreateUVBuffer(RenderContext renderContext) => new NullBuffer<Vector2>(renderContext);
        public override GPUBuffer<Vector3> CreateNormalBuffer(RenderContext renderContext) => new NullBuffer<Vector3>(renderContext);
        public override GPUBuffer<Vector4> CreateTangentBuffer(RenderContext renderContext) => new NullBuffer<Vector4>(renderContext);
        public override GPUBuffer<Vector4> CreateBoneIndexBuffer(RenderContext renderContext) => new NullBuffer<Vector4>(renderContext);
        public override GPUBuffer<Vector4> CreateBoneWeightBuffer(RenderContext renderContext) => new NullBuffer<Vector4>(renderContext);
        public override GPUBuffer<T> CreateBuffer<T>(RenderContext renderContext) => new NullBuffer<T>(renderContext);
        public override InstancedBuffer CreateInstancedBuffer(RenderContext renderContext) => new NullInstancedBuffer(renderContext);
        public override GPUBuffer<Vector2> CreateVertexBuffer2(RenderContext renderContext) => new NullBuffer<Vector2>(renderContext);
        public override Texture2D CreateTexture2D(in TextureData data, TextureType type) => new NullTexture2D();
        public override Texture3D CreateCubemap(TextureData[] faces) => new NullTexture3D();
        public override StandardShader CreateStandardShader(string vertexCode, string fragmentCode) =>
            new NullStandardShader(vertexCode, fragmentCode);
    }

    // No-op shader for headless (scripts+physics, no GL). The headless runtime never renders, so this
    // just satisfies the RenderAsset contract; every member is a no-op.
    sealed class NullStandardShader(string vertexCode, string fragmentCode)
        : StandardShader(vertexCode, fragmentCode) {
        public override int UID => 0;
        protected override void Compile(string vertexCode, string fragmentCode) { }
        protected override void OnDispose() { }
        public override void SetBool(string name, bool value) { }
        public override void SetInt(string name, int value) { }
        public override void SetFloat(string name, float value) { }
        public override void SetFloat2(string name, Vector2 value) { }
        public override void SetFloat3(string name, Vector3 value) { }
        public override void SetFloat4(string name, Vector4 value) { }
        public override void SetMatrix4(string name, ref Matrix4 value, bool transpose = false) { }
        protected override void ActivateShader() { }
        protected override void DeactivateShader() { }
    }

    sealed class NullRenderContext : RenderContext {
        public override int UID { get; protected set; }
        public override void Dispose() { }
        public override void Activate() { }
        public override void Deactivate() { }
    }

    sealed class NullBuffer<T>(RenderContext renderContext) : GPUBuffer<T>(renderContext) where T : struct {
        protected override int UID { get; set; }
        public override void SetBufferData(in T[] data, BufferUsage usage) { }
        public override void Create() { }
        public override void Dispose() { }
        public override void Activate() { }
        public override void Deactivate() { }
    }

    sealed class NullInstancedBuffer(RenderContext renderContext) : InstancedBuffer(renderContext) {
        protected override int UID { get; set; }
        public override void SetBufferData(in Matrix4[] data, BufferUsage usage) { }
        public override void Create() { }
        public override void Dispose() { }
        public override void Activate() { }
        public override void Deactivate() { }
    }

    sealed class NullTexture2D : Texture2D {
        public override int UID { get; protected set; }
        public override void Activate() { }
        public override void Deactivate() { }
        public override void Dispose() { }
        protected internal override void Upload(in TextureData data, TextureType type) { }
    }

    sealed class NullTexture3D : Texture3D {
        public override int UID { get; protected set; }
        public override void Activate() { }
        public override void Deactivate() { }
        public override void Dispose() { }
        protected internal override void UploadFaces(TextureData[] faces) { }
    }
}
