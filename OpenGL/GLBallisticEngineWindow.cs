using BallisticEngine.Core.GL;
using BallisticEngine.GLImplementation;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;

namespace BallisticEngine;

class GLBallisticEngineWindow : GameWindow, IBallisticEngineRuntime, IWindow {
    public event Action<double> WindowUpdateCallback;
    public event Action<double> WindowRenderCallback;

    public event Action OnWindowShow {
        add => Load += value;
        remove => Load -= value;
    }

    public IEngineTimer EngineTimer { get; }
    public IInputProvider InputProvider { get; }
    public IWindow Window => this;
    public RenderAsset RenderAsset { get; } = new OpenGLRenderAsset();
    public ILogger Logger { get; } = new GLLogger();
    int width, height;
    public int Width => width;
    public int Height => height;


    double elapsedTime;
    int frameCountInCurrentSecond;

    public void SetFrequency(int frequency) {
        UpdateFrequency = frequency;
    }

    public void SwapFrameBuffers() => Context.SwapBuffers();
    public float FrameRate => currentFps;
    public event Action<int, int> OnResizeCallback;


    public GLBallisticEngineWindow(int width, int height) : base(GameWindowSettings.Default,
        NativeWindowSettings.Default) {
        this.width = width;
        this.height = height;
        Title = "Ballistic ";

        EngineTimer = new GLTime();
        InputProvider = new GLInput(KeyboardState, MouseState);

        CenterWindow(new Vector2i(width, height));
    }


    protected override void OnResize(ResizeEventArgs e) {
        base.OnResize(e);
        GL.Viewport(0, 0, e.Width, e.Height);
        width = e.Width;
        height = e.Height;
        OnResizeCallback?.Invoke(e.Width, e.Height);
    }

    protected override void OnRenderFrame(FrameEventArgs args) {
        WindowRenderCallback!.Invoke(args.Time);
        base.OnRenderFrame(args);
        Context.SwapBuffers();
    }

    protected override void OnUpdateFrame(FrameEventArgs args) {
        WindowUpdateCallback!.Invoke(args.Time);
        UpdateFrameRate(args.Time);
        base.OnUpdateFrame(args);
    }

    int currentFps;

    void UpdateFrameRate(double deltaTime) {
        elapsedTime += deltaTime;
        frameCountInCurrentSecond++;

        if (elapsedTime >= 1.0) {
            currentFps = frameCountInCurrentSecond;

            frameCountInCurrentSecond = 0;
            elapsedTime = 0.0;

            Console.Title = $"FPS: {currentFps}";
        }
    }
}