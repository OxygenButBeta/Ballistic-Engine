using System.Runtime.InteropServices;
using Hexa.NET.ImGui;
using OpenTK.Windowing.Desktop;
using SysVec2 = System.Numerics.Vector2;
using SysVec4 = System.Numerics.Vector4;

namespace BallisticEngine.Editor;

internal sealed class ImGuiController : IDisposable {
    public static ImFontPtr Bold { get; private set; }
    public static ImFontPtr LargeIcons { get; private set; }
    public static bool HasIcons { get; private set; }
    readonly GameWindow window;

    readonly IImGuiRenderer renderer;
    readonly ImGuiContextPtr context;
    bool frameBegun;

    public float Scale { get; private set; }

    public ImGuiController(GameWindow window) {
        this.window = window;

        context = ImGui.CreateContext();
        ImGui.SetCurrentContext(context);

        Scale = EffectiveScale(window);

        ImGuiIOPtr io = ImGui.GetIO();
        io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;

        io.ConfigFlags |= ImGuiConfigFlags.DockingEnable;

        unsafe { io.IniFilename = null; }

        LoadFont(io, Scale);
        ApplyGeometry(Scale);
        ApplyColors(EditorPrefs.Current.Accent);

        renderer = new ImGuiDx12Renderer(() => (window as Dx12BallisticEngineWindow)?.SwapChain?.CommandList);
        renderer.CreateDeviceResources();

        window.TextInput += e => ImGuiInput.OnTextInput((uint)e.Unicode);
    }

    float EffectiveScale(GameWindow window) => DetectScale(window) * EditorPrefs.Current.UiScale;

    public void RefreshScale() {
        float target = EffectiveScale(window);
        if (Math.Abs(target - Scale) < 0.01f)
            return;

        Scale = target;

        ImGuiIOPtr io = ImGui.GetIO();
        io.Fonts.Clear();
        LoadFont(io, Scale);

        ApplyGeometry(Scale);
        ApplyColors(EditorPrefs.Current.Accent);
        renderer.RecreateFontTexture();
    }

    static float DetectScale(GameWindow window) {
        if (window.TryGetCurrentMonitorScale(out float sx, out float sy))
            return Math.Clamp(Math.Max(sx, sy), 1f, 3f);
        return 1f;
    }

    static unsafe void LoadFont(ImGuiIOPtr io, float scale) {
        var fontsDir = Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts");
        var calibri = Path.Combine(fontsDir, "Calibri-Regular.ttf");
        var calibriBold = Path.Combine(fontsDir, "Calibri-Bold.ttf");
        var interRegular = Path.Combine(fontsDir, "Inter-Regular.ttf");
        var interSemibold = Path.Combine(fontsDir, "Inter-SemiBold.ttf");
        var icons = File.Exists(Path.Combine(fontsDir, "lucide.ttf"))
            ? Path.Combine(fontsDir, "lucide.ttf") : null;
        float baseSize = 15.5f;
        float size = MathF.Round(baseSize * scale);
        EditorTheme.BodySize = size;
        EditorTheme.UiScale = scale;

        bool haveCalibri = File.Exists(calibri);
        string regularTtf = haveCalibri ? calibri : (File.Exists(interRegular) ? interRegular : null);
        string boldTtf = File.Exists(calibriBold) ? calibriBold
                       : (File.Exists(interSemibold) ? interSemibold : regularTtf);

        ImFontPtr bodyFont, captionFont, headerFont, displayFont;
        if (regularTtf is not null) {
            bodyFont = AddTextFont(io, regularTtf, size);
        }
        else {
            bodyFont = io.Fonts.AddFontDefault();
            io.FontGlobalScale = scale;
        }
        MergeIcons(io, icons, size);
        Bold = bodyFont;

        captionFont = regularTtf is not null
            ? LoadSizedWithIcons(io, regularTtf, icons, MathF.Round(baseSize * EditorTheme.CaptionScale * scale))
            : bodyFont;

        if (boldTtf is not null) {
            headerFont  = LoadSizedWithIcons(io, boldTtf, icons, MathF.Round(baseSize * EditorTheme.HeaderScale * scale));
            displayFont = LoadSizedWithIcons(io, boldTtf, icons, MathF.Round(baseSize * EditorTheme.DisplayScale * scale));
            Bold = headerFont;
        }
        else {
            headerFont = displayFont = bodyFont;
            Bold = bodyFont;
        }

        EditorTheme.Body    = bodyFont;
        EditorTheme.Caption = captionFont;
        EditorTheme.Header  = headerFont;
        EditorTheme.Display = displayFont;

        HasIcons = icons is not null;
        if (HasIcons) {
            ImFontConfigPtr cfg = ImGui.ImFontConfig();
            cfg.PixelSnapH = true;
            LargeIcons = io.Fonts.AddFontFromFileTTF(icons, MathF.Round(34f * scale), cfg, IconRanges);
            cfg.Destroy();
        }
        else {
            LargeIcons = io.Fonts.Fonts[0];
        }
    }

    static unsafe ImFontPtr LoadSizedWithIcons(ImGuiIOPtr io, string ttf, string icons, float px) {
        ImFontPtr f = AddTextFont(io, ttf, px);
        MergeIcons(io, icons, px);
        return f;
    }

    static unsafe ImFontPtr AddTextFont(ImGuiIOPtr io, string ttf, float px) {
        ImFontConfigPtr cfg = ImGui.ImFontConfig();
        cfg.OversampleH = 3;
        cfg.OversampleV = 1;
        cfg.PixelSnapH = true;
        cfg.RasterizerMultiply = 1.0f;
        ImFontPtr f = io.Fonts.AddFontFromFileTTF(ttf, px, cfg);
        cfg.Destroy();
        return f;
    }

    static unsafe void MergeIcons(ImGuiIOPtr io, string iconPath, float textSize) {
        if (iconPath is null)
            return;

        ImFontConfigPtr cfg = ImGui.ImFontConfig();
        cfg.MergeMode = true;
        cfg.PixelSnapH = true;
        cfg.GlyphOffset = new SysVec2(0, MathF.Round(textSize * 0.16f));
        cfg.GlyphMinAdvanceX = MathF.Round(textSize * 1.15f);
        io.Fonts.AddFontFromFileTTF(iconPath, MathF.Round(textSize * 0.92f), cfg, IconRanges);
        cfg.Destroy();
    }

    static IntPtr iconRanges;

    static unsafe uint* IconRanges {
        get {
            if (iconRanges == IntPtr.Zero) {
                iconRanges = Marshal.AllocHGlobal(sizeof(uint) * 3);
                var r = (uint*)iconRanges;
                r[0] = (uint)EditorIcons.RangeLow; r[1] = (uint)EditorIcons.RangeHigh; r[2] = 0;
            }
            return (uint*)iconRanges;
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

    public void SetAccent(SysVec4 accent) => ApplyColors(accent);

    static void ApplyGeometry(float scale) {
        ImGuiStylePtr style = ImGui.GetStyle();

        style.WindowRounding = 3f;
        style.ChildRounding = 3f;
        style.FrameRounding = 3f;
        style.PopupRounding = 3f;
        style.GrabRounding = 3f;
        style.TabRounding = 3f;
        style.ScrollbarRounding = 3f;
        style.WindowBorderSize = 0f;
        style.FrameBorderSize = 1f;
        style.PopupBorderSize = 1f;
        style.ChildBorderSize = 0f;
        style.TabBorderSize = 0f;
        style.WindowPadding = new SysVec2(10, 8);
        style.FramePadding = new SysVec2(10, 7);
        style.CellPadding = new SysVec2(8, 6);
        style.ItemSpacing = new SysVec2(8, 7);
        style.ItemInnerSpacing = new SysVec2(7, 5);
        style.IndentSpacing = 16f;
        style.ScrollbarSize = 12f;
        style.GrabMinSize = 10f;
        style.TabBarBorderSize = 1f;
        style.DockingSeparatorSize = 2f;
        style.SeparatorTextBorderSize = 2f;
        style.SeparatorTextPadding = new SysVec2(18, 5);
        style.WindowTitleAlign = new SysVec2(0.0f, 0.5f);
        style.WindowMenuButtonPosition = ImGuiDir.None;
        style.ScaleAllSizes(scale);
    }

    static void ApplyColors(SysVec4 accent) {
        if (Environment.GetEnvironmentVariable("BALLISTIC_SPECTRUM") == "1") {
            SpectrumTheme.Apply();
            return;
        }
        if (Environment.GetEnvironmentVariable("BALLISTIC_GRAPHITE") != "1") {
            DraculaTheme.Apply();
            return;
        }

        var c = ImGui.GetStyle().Colors;

        SysVec4 bg0 = Rgb(0x101012);
        SysVec4 bg1 = Rgb(0x222226);
        SysVec4 bg2 = Rgb(0x303035);
        SysVec4 bg3 = Rgb(0x42424A);
        SysVec4 header = Rgb(0x36363B);
        SysVec4 headerHi = Rgb(0x46464E);
        SysVec4 shell = Rgb(0x0A0A0C);
        SysVec4 accentHi = Lighten(accent, 1.18f);
        SysVec4 accentDim = Darken(accent, 0.58f);
        SysVec4 accentMid = WithAlpha(accent, 0.60f);
        SysVec4 accentFaint = WithAlpha(accent, 0.22f);
        SysVec4 accentRow = WithAlpha(accent, 0.30f);
        SysVec4 text = Rgb(0xF2F3F4);
        SysVec4 textDim = Rgb(0xA6A6AB);
        SysVec4 border = Rgb(0x060608);
        SysVec4 frameBorder = Rgb(0x070708);
        SysVec4 borderLight = Rgb(0x4E4E55);
        SysVec4 titleBg = shell;

        c[(int)ImGuiCol.Text] = text;
        c[(int)ImGuiCol.TextDisabled] = textDim;
        c[(int)ImGuiCol.WindowBg] = bg0;
        c[(int)ImGuiCol.ChildBg] = new SysVec4(0, 0, 0, 0);
        c[(int)ImGuiCol.PopupBg] = bg1;
        c[(int)ImGuiCol.Border] = frameBorder;
        c[(int)ImGuiCol.BorderShadow] = new SysVec4(0, 0, 0, 0);
        c[(int)ImGuiCol.FrameBg] = bg2;
        c[(int)ImGuiCol.FrameBgHovered] = Mix(bg2, accent, 0.16f);
        c[(int)ImGuiCol.FrameBgActive] = Mix(bg2, accent, 0.28f);
        c[(int)ImGuiCol.TitleBg] = titleBg;
        c[(int)ImGuiCol.TitleBgActive] = header;
        c[(int)ImGuiCol.TitleBgCollapsed] = titleBg;
        c[(int)ImGuiCol.MenuBarBg] = shell;
        c[(int)ImGuiCol.ScrollbarBg] = new SysVec4(0, 0, 0, 0);
        c[(int)ImGuiCol.ScrollbarGrab] = bg3;
        c[(int)ImGuiCol.ScrollbarGrabHovered] = borderLight;
        c[(int)ImGuiCol.ScrollbarGrabActive] = accent;
        c[(int)ImGuiCol.CheckMark] = accentHi;
        c[(int)ImGuiCol.SliderGrab] = accent;
        c[(int)ImGuiCol.SliderGrabActive] = accentHi;
        c[(int)ImGuiCol.Button] = bg3;
        c[(int)ImGuiCol.ButtonHovered] = Mix(bg3, accent, 0.26f);
        c[(int)ImGuiCol.ButtonActive] = Mix(bg3, accent, 0.55f);
        c[(int)ImGuiCol.Header] = Mix(header, accent, 0.20f);
        c[(int)ImGuiCol.HeaderHovered] = Mix(headerHi, accent, 0.12f);
        c[(int)ImGuiCol.HeaderActive] = accentRow;
        c[(int)ImGuiCol.Separator] = WithAlpha(Rgb(0xFFFFFF), 0.08f);
        c[(int)ImGuiCol.SeparatorHovered] = accent;
        c[(int)ImGuiCol.SeparatorActive] = accentHi;
        c[(int)ImGuiCol.ResizeGrip] = new SysVec4(0, 0, 0, 0);
        c[(int)ImGuiCol.ResizeGripHovered] = accentFaint;
        c[(int)ImGuiCol.ResizeGripActive] = accent;
        c[(int)ImGuiCol.Tab] = shell;
        c[(int)ImGuiCol.TabHovered] = Mix(header, accent, 0.14f);
        c[(int)ImGuiCol.TabSelected] = Mix(bg1, accent, 0.14f);
        c[(int)ImGuiCol.TabSelectedOverline] = accentHi;
        c[(int)ImGuiCol.TabDimmed] = Rgb(0x0A0A0B);
        c[(int)ImGuiCol.TabDimmedSelected] = Mix(bg1, accent, 0.07f);
        c[(int)ImGuiCol.TabDimmedSelectedOverline] = accentMid;
        c[(int)ImGuiCol.TextSelectedBg] = accentRow;
        c[(int)ImGuiCol.DragDropTarget] = accentHi;
        c[(int)ImGuiCol.NavCursor] = accentHi;
        c[(int)ImGuiCol.ModalWindowDimBg] = new SysVec4(0.01f, 0.01f, 0.02f, 0.65f);
        c[(int)ImGuiCol.TableHeaderBg] = header;
        c[(int)ImGuiCol.TableBorderStrong] = WithAlpha(Rgb(0xFFFFFF), 0.09f);
        c[(int)ImGuiCol.TableBorderLight] = WithAlpha(Rgb(0xFFFFFF), 0.04f);
        c[(int)ImGuiCol.TableRowBg] = new SysVec4(0, 0, 0, 0);
        c[(int)ImGuiCol.TableRowBgAlt] = new SysVec4(1, 1, 1, 0.022f);

        c[(int)ImGuiCol.DockingEmptyBg] = bg0;
        c[(int)ImGuiCol.DockingPreview] = accentMid;
    }

    static SysVec4 Lighten(SysVec4 c, float f) => new(
        Math.Min(c.X * f, 1f), Math.Min(c.Y * f, 1f), Math.Min(c.Z * f, 1f), c.W);

    static SysVec4 Mix(SysVec4 a, SysVec4 b, float t) => new(
        a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t, a.W);

    static SysVec4 Darken(SysVec4 c, float f) => new(c.X * f, c.Y * f, c.Z * f, c.W);

    static SysVec4 WithAlpha(SysVec4 c, float a) => new(c.X, c.Y, c.Z, a);

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
