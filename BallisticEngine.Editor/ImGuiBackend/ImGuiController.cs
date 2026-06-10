using ImGuiNET;
using OpenTK.Windowing.Desktop;
using SysVec2 = System.Numerics.Vector2;
using SysVec4 = System.Numerics.Vector4;

namespace BallisticEngine.Editor;

// Owns the ImGui context and device resources; call Update() before building UI and Render()
// after. DPI-aware: detects the monitor content scale, loads a real UI font (Segoe UI) at the
// scaled size and scales all style metrics, so the editor is usable on 4K displays.
internal sealed class ImGuiController : IDisposable {
    readonly GameWindow window;
    readonly ImGuiGLRenderer renderer = new();
    readonly IntPtr context;
    bool frameBegun;

    // Monitor content scale (1.0 = 96 dpi). Multiply any hand-authored pixel size by this.
    public float Scale { get; }

    public ImGuiController(GameWindow window) {
        this.window = window;

        context = ImGui.CreateContext();
        ImGui.SetCurrentContext(context);

        Scale = DetectScale(window);

        ImGuiIOPtr io = ImGui.GetIO();
        io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;
        io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;

        LoadFont(io, Scale);
        ApplyTheme(Scale);

        renderer.CreateDeviceResources();

        window.TextInput += e => ImGuiInput.OnTextInput((uint)e.Unicode);
    }

    static float DetectScale(GameWindow window) {
        if (window.TryGetCurrentMonitorScale(out float sx, out float sy))
            return Math.Clamp(Math.Max(sx, sy), 1f, 3f);
        return 1f;
    }

    static void LoadFont(ImGuiIOPtr io, float scale) {
        var segoe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "segoeui.ttf");
        if (File.Exists(segoe)) {
            io.Fonts.AddFontFromFileTTF(segoe, MathF.Round(17f * scale));
        }
        else {
            io.Fonts.AddFontDefault();
            io.FontGlobalScale = scale;
        }
    }

    public void WindowResized(int width, int height) {
        ImGui.GetIO().DisplaySize = new SysVec2(width, height);
    }

    public void Update(float deltaSeconds) {
        ImGuiIOPtr io = ImGui.GetIO();
        io.DisplaySize = new SysVec2(window.ClientSize.X, window.ClientSize.Y);
        io.DisplayFramebufferScale = SysVec2.One;
        io.DeltaTime = deltaSeconds > 0 ? deltaSeconds : 1f / 60f;

        ImGuiInput.Update(window);

        frameBegun = true;
        ImGui.NewFrame();
    }

    public void Render() {
        if (!frameBegun)
            return;

        frameBegun = false;
        ImGui.Render();
        renderer.Render(ImGui.GetDrawData());
    }

    public bool WantCaptureMouse => ImGui.GetIO().WantCaptureMouse;
    public bool WantCaptureKeyboard => ImGui.GetIO().WantCaptureKeyboard;
    public bool WantTextInput => ImGui.GetIO().WantTextInput;

    // Dark editor theme: Unreal's near-black panels with Unity-style blue selection accents.
    static void ApplyTheme(float scale) {
        ImGuiStylePtr style = ImGui.GetStyle();

        // Geometry
        style.WindowRounding = 0f;          // tiled panels, no floating-window look
        style.ChildRounding = 4f;
        style.FrameRounding = 3f;
        style.PopupRounding = 3f;
        style.GrabRounding = 3f;
        style.TabRounding = 4f;
        style.ScrollbarRounding = 8f;
        style.WindowBorderSize = 1f;
        style.FrameBorderSize = 0f;
        style.WindowPadding = new SysVec2(10, 8);
        style.FramePadding = new SysVec2(8, 5);
        style.ItemSpacing = new SysVec2(8, 6);
        style.ItemInnerSpacing = new SysVec2(6, 4);
        style.IndentSpacing = 18f;
        style.ScrollbarSize = 14f;
        style.GrabMinSize = 10f;
        style.ScaleAllSizes(scale);

        // Palette
        SysVec4 bg0 = Rgb(0x121212);     // window background (Unreal near-black)
        SysVec4 bg1 = Rgb(0x1B1B1B);     // child / popup
        SysVec4 bg2 = Rgb(0x262626);     // frames (inputs)
        SysVec4 bg3 = Rgb(0x303030);     // hovered frames
        SysVec4 header = Rgb(0x2A2A2A);  // collapsing headers
        SysVec4 accent = Rgb(0x2C5D87);  // Unity selection blue
        SysVec4 accentHi = Rgb(0x3D77B6);
        SysVec4 text = Rgb(0xDCDCDC);
        SysVec4 textDim = Rgb(0x808080);
        SysVec4 border = Rgb(0x2D2D2D);
        SysVec4 titleBg = Rgb(0x0D0D0D);

        var c = style.Colors;
        c[(int)ImGuiCol.Text] = text;
        c[(int)ImGuiCol.TextDisabled] = textDim;
        c[(int)ImGuiCol.WindowBg] = bg0;
        c[(int)ImGuiCol.ChildBg] = new SysVec4(0, 0, 0, 0);
        c[(int)ImGuiCol.PopupBg] = bg1;
        c[(int)ImGuiCol.Border] = border;
        c[(int)ImGuiCol.BorderShadow] = new SysVec4(0, 0, 0, 0);
        c[(int)ImGuiCol.FrameBg] = bg2;
        c[(int)ImGuiCol.FrameBgHovered] = bg3;
        c[(int)ImGuiCol.FrameBgActive] = accent;
        c[(int)ImGuiCol.TitleBg] = titleBg;
        c[(int)ImGuiCol.TitleBgActive] = titleBg;
        c[(int)ImGuiCol.TitleBgCollapsed] = titleBg;
        c[(int)ImGuiCol.MenuBarBg] = bg1;
        c[(int)ImGuiCol.ScrollbarBg] = bg0;
        c[(int)ImGuiCol.ScrollbarGrab] = bg3;
        c[(int)ImGuiCol.ScrollbarGrabHovered] = Rgb(0x3A3A3A);
        c[(int)ImGuiCol.ScrollbarGrabActive] = accent;
        c[(int)ImGuiCol.CheckMark] = accentHi;
        c[(int)ImGuiCol.SliderGrab] = accent;
        c[(int)ImGuiCol.SliderGrabActive] = accentHi;
        c[(int)ImGuiCol.Button] = bg2;
        c[(int)ImGuiCol.ButtonHovered] = bg3;
        c[(int)ImGuiCol.ButtonActive] = accent;
        c[(int)ImGuiCol.Header] = header;
        c[(int)ImGuiCol.HeaderHovered] = bg3;
        c[(int)ImGuiCol.HeaderActive] = accent;
        c[(int)ImGuiCol.Separator] = border;
        c[(int)ImGuiCol.SeparatorHovered] = accent;
        c[(int)ImGuiCol.SeparatorActive] = accentHi;
        c[(int)ImGuiCol.ResizeGrip] = new SysVec4(0, 0, 0, 0);
        c[(int)ImGuiCol.Tab] = bg1;
        c[(int)ImGuiCol.TabHovered] = accentHi;
        c[(int)ImGuiCol.TabSelected] = accent;
        c[(int)ImGuiCol.TabDimmed] = bg1;
        c[(int)ImGuiCol.TabDimmedSelected] = header;
        c[(int)ImGuiCol.TextSelectedBg] = accent;
        c[(int)ImGuiCol.DragDropTarget] = accentHi;
        c[(int)ImGuiCol.NavHighlight] = accentHi;
        c[(int)ImGuiCol.ModalWindowDimBg] = new SysVec4(0, 0, 0, 0.5f);
    }

    static SysVec4 Rgb(int hex) => new(
        ((hex >> 16) & 0xFF) / 255f,
        ((hex >> 8) & 0xFF) / 255f,
        (hex & 0xFF) / 255f,
        1f);

    public void Dispose() {
        renderer.Dispose();
        ImGui.DestroyContext(context);
    }
}
