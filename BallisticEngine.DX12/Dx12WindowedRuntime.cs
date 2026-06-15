using BallisticEngine.Core.GL;            // GLTime, GLInput (OpenTK input/timer — not GL-API-coupled)
using BallisticEngine.DX12;               // Dx12Backend, Dx12SwapChain
using BallisticEngine.GLImplementation;   // GLLogger
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace BallisticEngine;

// The windowed DX12 PLAYER host: an OpenTK GameWindow with ContextAPI.NoAPI (no GL context) whose HWND
// drives a DX12 swapchain. Mirrors GLBallisticEngineWindow but presents through Dx12SwapChain instead of
// Context.SwapBuffers — the engine renders the scene into the renderer's LDR target (PresentToScreen path),
// and each frame the host blits that into the backbuffer and flips. No ImGui (that's the editor). This is
// the piece that lets the standalone player run on DX12 windowed, so GL can be deleted. Selected by
// Program.cs when BALLISTIC_BACKEND=dx12 (and not the deterministic headless screenshot path).
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

    public void SetFrequency(int frequency) => UpdateFrequency = frequency;
    public void SwapFrameBuffers() { }   // present is driven by OnRenderFrame

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
            API = ContextAPI.NoAPI,   // DX12 owns the surface via the HWND; NativeWindow.Context is null
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
            ClientSize = new Vector2i(width, height);
        }
    }

    protected override void OnLoad() {
        base.OnLoad();
        IsVisible = true;
        Focus();
        width = ClientSize.X; height = ClientSize.Y;
        swapChain = new Dx12SwapChain(Dx12Backend.Device, GetHwnd(), width, height);
    }

    unsafe nint GetHwnd() => GLFW.GetWin32Window(WindowPtr);

    protected override void OnResize(ResizeEventArgs e) {
        base.OnResize(e);
        width = e.Width;
        height = e.Height;
        if (swapChain != null) {
            swapChain.Resize(e.Width, e.Height);
            // Keep the renderer's LDR target the same size as the backbuffer so the present blit (CopyResource)
            // matches; the renderer renders the scene at the window resolution.
            (RenderAsset.Current.Renderer as DX12HDRenderer)?.ResizeSceneTarget(e.Width, e.Height);
        }
        OnResizeCallback?.Invoke(e.Width, e.Height);
    }

    protected override void OnRenderFrame(FrameEventArgs args) {
        if (swapChain == null) { base.OnRenderFrame(args); return; }
        using (Profiler.Zone("RenderFrame"))
            WindowRenderCallback?.Invoke(args.Time);   // engine renders the scene into the renderer's LDR target
        var r = RenderAsset.Current.Renderer as DX12HDRenderer;
        if (r?.DisplayResource != null)
            swapChain.PresentTexture(r.DisplayResource, vsync: true);
        base.OnRenderFrame(args);
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
