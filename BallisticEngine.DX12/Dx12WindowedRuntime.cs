using BallisticEngine.Core.GL;
using BallisticEngine.DX12;
using BallisticEngine.GLImplementation;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace BallisticEngine;

public sealed class Dx12WindowedRuntime : GameWindow, IBallisticEngineRuntime, IWindow {
    public event Action<double> WindowUpdateCallback;
    public event Action<double> WindowRenderCallback;
    public event Action OnWindowShow {
        add => Load += value;
        remove => Load -= value;
    }

    public IEngineTimer EngineTimer { get; }
    public IInputProvider InputProvider { get; }
    public IWindow Window => this;
    public RenderAsset RenderAsset { get; } = new DirectXRenderAsset();
    public ILogger Logger { get; } = new GLLogger();

    int width, height;
    public int Width => width;
    public int Height => height;
    public float FrameRate => 60f;
    public event Action<int, int> OnResizeCallback;

    Dx12SwapChain swapChain;

    static readonly bool VSync = Environment.GetEnvironmentVariable("BALLISTIC_DX12_VSYNC") == "1";

    public void SetFrequency(int frequency) => UpdateFrequency = frequency;
    public void SwapFrameBuffers() { }

    public CursorMode CursorMode {
        get => CursorState switch {
            CursorState.Hidden => CursorMode.Hidden,
            CursorState.Grabbed => CursorMode.Locked,
            _ => CursorMode.Normal,
        };
        set => CursorState = value switch {
            CursorMode.Hidden => CursorState.Hidden,
            CursorMode.Locked => CursorState.Grabbed,
            _ => CursorState.Normal,
        };
    }

    public Dx12WindowedRuntime(int width, int height, bool fullscreen = false,
        bool borderless = false, string title = "Ballistic") : base(GameWindowSettings.Default,
        new NativeWindowSettings {
            API = ContextAPI.NoAPI,
            WindowBorder = fullscreen || borderless ? WindowBorder.Hidden : WindowBorder.Resizable,
            StartFocused = true,
            StartVisible = true,
        }) {
        this.width = width;
        this.height = height;
        Title = string.IsNullOrWhiteSpace(title) ? "Ballistic" : title;

        EngineTimer = new GLTime();
        InputProvider = new GLInput(KeyboardState, MouseState, () => JoystickStates);

        if (fullscreen) {
            MonitorInfo m = Monitors.GetPrimaryMonitor();
            this.width = m.ClientArea.Size.X;
            this.height = m.ClientArea.Size.Y;
            WindowState = WindowState.Fullscreen;
        }
        else {
            ClientSize = new OpenTK.Mathematics.Vector2i(width, height);
        }
    }

    protected override void OnTextInput(TextInputEventArgs e) {
        base.OnTextInput(e);
        foreach (var ch in char.ConvertFromUtf32(e.Unicode))
            Input.PushTypedChar(ch);
    }

    protected override void OnLoad() {
        base.OnLoad();
        IsVisible = true;
        Focus();
        width = ClientSize.X; height = ClientSize.Y;
        swapChain = new Dx12SwapChain(Dx12Backend.Device, GetHwnd(), width, height);
        (RenderAsset.Current.Renderer as DX12HDRenderer)?.ResizeSceneTarget(width, height);
    }

    unsafe nint GetHwnd() => GLFW.GetWin32Window(WindowPtr);

    protected override void OnResize(ResizeEventArgs e) {
        base.OnResize(e);
        width = e.Width;
        height = e.Height;
        if (swapChain != null) {
            swapChain.Resize(e.Width, e.Height);
            (RenderAsset.Current.Renderer as DX12HDRenderer)?.ResizeSceneTarget(e.Width, e.Height);
        }
        OnResizeCallback?.Invoke(e.Width, e.Height);
    }

    protected override void OnRenderFrame(FrameEventArgs args) {
        if (swapChain == null) { base.OnRenderFrame(args); return; }

        if (BallisticEngine.RenderThread.Enabled) { base.OnRenderFrame(args); Profiler.FrameMark(); return; }

        using (Profiler.Zone("RenderFrame"))
            WindowRenderCallback?.Invoke(args.Time);
        var r = RenderAsset.Current.Renderer as DX12HDRenderer;
        if (r?.DisplayResource != null)
            swapChain.PresentTexture(r.DisplayResource, vsync: VSync);
        base.OnRenderFrame(args);
        Profiler.FrameMark();
    }

    public void PresentFromRenderThread() {
        if (swapChain == null) return;
        var r = RenderAsset.Current.Renderer as DX12HDRenderer;
        if (r?.DisplayResource != null)
            swapChain.PresentTexture(r.DisplayResource, vsync: VSync);
        Profiler.FrameMark();
    }

    protected override void OnUpdateFrame(FrameEventArgs args) {
        using (Profiler.Zone("UpdateFrame"))
            WindowUpdateCallback?.Invoke(args.Time);
        base.OnUpdateFrame(args);
    }

    protected override void OnUnload() {
        swapChain?.Dispose();
        swapChain = null;
        base.OnUnload();
    }
}
