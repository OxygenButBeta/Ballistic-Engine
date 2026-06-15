using System.Runtime.InteropServices;
using Hexa.NET.ImGui;
using OpenTK.Windowing.Desktop;
using SysVec2 = System.Numerics.Vector2;
using SysVec4 = System.Numerics.Vector4;

namespace BallisticEngine.Editor;

// Owns the ImGui context and device resources; call Update() before building UI and Render()
// after. DPI-aware: detects the monitor content scale, loads a real UI font (Segoe UI) at the
// scaled size and scales all style metrics, so the editor is usable on 4K displays.
// Also bakes Segoe Fluent/MDL2 icon glyphs into the atlas (see EditorIcons), a semibold
// variant for headers, and a large icon-only font for asset tiles / empty states.
internal sealed class ImGuiController : IDisposable {
    // Fonts beyond the default. Reassigned on every atlas rebuild (DPI/UI-scale change).
    public static ImFontPtr Bold { get; private set; }
    public static ImFontPtr LargeIcons { get; private set; }
    public static bool HasIcons { get; private set; }
    readonly GameWindow window;
    // GL or DX12 device backend, chosen by the active render backend. The DX12 backend records into the
    // editor swapchain's open UI command list (resolved lazily — the swapchain is created after this ctor).
    readonly IImGuiRenderer renderer;
    readonly ImGuiContextPtr context;
    bool frameBegun;

    // Monitor content scale (1.0 = 96 dpi). Multiply any hand-authored pixel size by this.
    // Not readonly: the editor re-detects it when the window moves to a different-DPI monitor.
    public float Scale { get; private set; }

    public ImGuiController(GameWindow window) {
        this.window = window;

        context = ImGui.CreateContext();
        ImGui.SetCurrentContext(context);

        Scale = EffectiveScale(window);

        ImGuiIOPtr io = ImGui.GetIO();
        // Keyboard nav is intentionally OFF: it lets ImGui interpret keys like Z/Space as widget
        // actions (e.g. toggling the focused CollapsingHeader), which collided with the editor's
        // global Ctrl+Z. The editor is mouse-driven; nav isn't needed.
        io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;

        // Single-window docking: panels can dock/undock/resize/tab inside the main window and the
        // layout persists. NOT ViewportsEnable — we don't tear panels out into separate OS windows.
        io.ConfigFlags |= ImGuiConfigFlags.DockingEnable;

        // Disable ImGui's automatic imgui.ini file IO — the editor persists the dock layout PER
        // PROJECT itself (EditorLayout.Save/Load via SaveIniSettingsToMemory), so the working dir
        // never accumulates a stray imgui.ini and each project remembers its own arrangement.
        unsafe { io.IniFilename = null; }

        LoadFont(io, Scale);
        ApplyGeometry(Scale);
        ApplyColors(EditorPrefs.Current.Accent);

        // DX12-only (GL deleted): the ImGui DX12 backend records into the editor swapchain's open UI command
        // list; the swapchain is created after this ctor (in the window's OnLoad), so resolve it lazily at
        // render time.
        renderer = new ImGuiDx12Renderer(() => (window as Dx12BallisticEngineWindow)?.SwapChain?.CommandList);
        renderer.CreateDeviceResources();

        window.TextInput += e => ImGuiInput.OnTextInput((uint)e.Unicode);
    }

    // Effective UI scale = monitor DPI Ã— the user's UiScale preference (Unity-style UI scale slider).
    float EffectiveScale(GameWindow window) => DetectScale(window) * EditorPrefs.Current.UiScale;

    // Re-detects the effective scale (monitor DPI moved, or the user changed the UI-scale slider) and,
    // if it changed, rebuilds the font at the new size, re-applies geometry from base sizes (so the
    // cumulative ScaleAllSizes doesn't compound) and re-uploads the font atlas. Safe to call every
    // frame â€” it no-ops unless the scale actually changed.
    public void RefreshScale() {
        float target = EffectiveScale(window);
        if (Math.Abs(target - Scale) < 0.01f)
            return;

        Scale = target;

        ImGuiIOPtr io = ImGui.GetIO();
        io.Fonts.Clear();
        LoadFont(io, Scale);

        ApplyGeometry(Scale);                       // re-sets every size to base, then scales
        ApplyColors(EditorPrefs.Current.Accent);    // colors are unaffected by scale, but keep accent
        renderer.RecreateFontTexture();
    }

    static float DetectScale(GameWindow window) {
        if (window.TryGetCurrentMonitorScale(out float sx, out float sy))
            return Math.Clamp(Math.Max(sx, sy), 1f, 3f);
        return 1f;
    }

    static unsafe void LoadFont(ImGuiIOPtr io, float scale) {
        var fontsDir = Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts");
        var regular = Path.Combine(fontsDir, "Inter-Regular.ttf");
        var semibold = Path.Combine(fontsDir, "Inter-SemiBold.ttf");
        var icons = File.Exists(Path.Combine(fontsDir, "lucide.ttf"))
            ? Path.Combine(fontsDir, "lucide.ttf") : null;
        float size = MathF.Round(16.5f * scale);   // Inter reads a touch larger than Segoe at the same px

        if (File.Exists(regular)) {
            io.Fonts.AddFontFromFileTTF(regular, size);
        }
        else {
            io.Fonts.AddFontDefault();
            io.FontGlobalScale = scale;
        }
        MergeIcons(io, icons, size);

        // SemiBold for component headers / titles; falls back to the regular font seamlessly.
        if (File.Exists(semibold)) {
            Bold = io.Fonts.AddFontFromFileTTF(semibold, size);
            MergeIcons(io, icons, size);
        }
        else {
            Bold = io.Fonts.Fonts[0];
        }

        // Icon-only display font for asset tiles and empty states (baked big = crisp).
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

    // Merges icon glyphs into the last-added font so labels can mix text and icons freely.
    // Slightly smaller than the text size + a small downward offset centers them on the line;
    // a uniform advance keeps icon columns (hierarchy, console) aligned.
    static unsafe void MergeIcons(ImGuiIOPtr io, string iconPath, float textSize) {
        if (iconPath is null)
            return;

        // Lucide glyphs are full line-height; bake them at ~text size and nudge down slightly so they
        // sit centered on the text baseline. A uniform advance keeps icon columns aligned.
        ImFontConfigPtr cfg = ImGui.ImFontConfig();
        cfg.MergeMode = true;
        cfg.PixelSnapH = true;
        cfg.GlyphOffset = new SysVec2(0, MathF.Round(textSize * 0.16f));
        cfg.GlyphMinAdvanceX = MathF.Round(textSize * 1.15f);
        io.Fonts.AddFontFromFileTTF(iconPath, MathF.Round(textSize * 0.92f), cfg, IconRanges);
        cfg.Destroy();
    }

    // Glyph-range memory must stay alive until the atlas is built (and it rebuilds on every DPI
    // change), so it lives in process-lifetime native memory. Hexa's AddFontFromFileTTF takes a
    // uint* range pointer (ImWchar32-style). Covers Lucide's used PUA span (see EditorIcons).
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

    // Re-applies the color palette with a new accent (e.g. from the Settings panel) without
    // re-running geometry â€” ScaleAllSizes is cumulative, so calling it twice would double padding.
    public void SetAccent(SysVec4 accent) => ApplyColors(accent);

    // Geometry: spacing, rounding, borders. Run ONCE at startup (ScaleAllSizes is cumulative).
    // A flatter, tighter geometry than stock ImGui â€” small consistent rounding, slim frame borders
    // for definition, and roomier rows so the editor reads as a purpose-built tool, not a demo.
    static void ApplyGeometry(float scale) {
        ImGuiStylePtr style = ImGui.GetStyle();

        // Modern app geometry: generous corner radii on cards/popups/tabs, rounded inputs, and almost
        // no borders — surfaces are separated by background tint + spacing, not lines (the "flat panels
        // welded by 1px seams" look is the main ImGui tell).
        style.WindowRounding = 6f;
        style.ChildRounding = 8f;
        style.FrameRounding = 7f;
        style.PopupRounding = 9f;
        style.GrabRounding = 7f;
        style.TabRounding = 8f;
        style.ScrollbarRounding = 9f;
        style.WindowBorderSize = 0f;        // panels read as surfaces, not framed boxes
        style.FrameBorderSize = 0f;
        style.PopupBorderSize = 1f;         // keep a hairline on floating popups for separation
        style.ChildBorderSize = 0f;
        style.WindowPadding = new SysVec2(12, 10);
        style.FramePadding = new SysVec2(11, 7);
        style.CellPadding = new SysVec2(8, 6);
        style.ItemSpacing = new SysVec2(9, 8);
        style.ItemInnerSpacing = new SysVec2(8, 6);
        style.IndentSpacing = 20f;
        style.ScrollbarSize = 11f;          // slimmer modern scrollbar
        style.GrabMinSize = 11f;
        style.TabBarBorderSize = 0f;
        style.DockingSeparatorSize = 2f;    // thin dock split handles
        style.SeparatorTextBorderSize = 2f;
        style.SeparatorTextPadding = new SysVec2(20, 6);
        style.WindowTitleAlign = new SysVec2(0.0f, 0.5f);
        style.WindowMenuButtonPosition = ImGuiDir.None;   // no collapse arrow clutter on dock tabs
        style.ScaleAllSizes(scale);
    }

    // Distinctive "graphite" editor theme: cool blue-grey panels (not flat neutral grey), layered
    // depth, and accent-tinted interaction states so it reads as a crafted tool rather than default
    // ImGui. Safe to re-run any frame (colors only); the accent + derived tints drive every state.
    static void ApplyColors(SysVec4 accent) {
        var c = ImGui.GetStyle().Colors;

        // Dark, softly-cool elevation ramp: surfaces read as stacked layers (deeper = further back),
        // making the blue accent pop. Lifted off pure black — a graphite charcoal that's easier on the
        // eyes than near-black while still modern.
        SysVec4 bg0 = Rgb(0x1A1C20);     // window background — base graphite
        SysVec4 bg1 = Rgb(0x212429);     // child / popup — raised surface
        SysVec4 bg2 = Rgb(0x282C32);     // frames (inputs)
        SysVec4 bg3 = Rgb(0x343943);     // hovered frames
        SysVec4 header = Rgb(0x2C313A);  // collapsing headers / selected tabs
        SysVec4 accentHi = Lighten(accent, 1.32f);
        SysVec4 accentDim = Darken(accent, 0.5f);
        SysVec4 accentFaint = WithAlpha(accent, 0.22f);
        SysVec4 text = Rgb(0xECEEF2);    // bright for contrast
        SysVec4 textDim = Rgb(0x848C99);
        SysVec4 border = Rgb(0x0E1013);  // used only where a seam is still wanted (popups/tables)
        SysVec4 borderLight = Rgb(0x383E48);
        SysVec4 titleBg = Rgb(0x15171A);

        c[(int)ImGuiCol.Text] = text;
        c[(int)ImGuiCol.TextDisabled] = textDim;
        c[(int)ImGuiCol.WindowBg] = bg0;
        c[(int)ImGuiCol.ChildBg] = new SysVec4(0, 0, 0, 0);
        c[(int)ImGuiCol.PopupBg] = bg1;
        c[(int)ImGuiCol.Border] = border;
        c[(int)ImGuiCol.BorderShadow] = new SysVec4(0, 0, 0, 0);
        c[(int)ImGuiCol.FrameBg] = bg2;
        c[(int)ImGuiCol.FrameBgHovered] = Mix(bg3, accent, 0.10f);
        c[(int)ImGuiCol.FrameBgActive] = accentFaint;
        c[(int)ImGuiCol.TitleBg] = titleBg;
        c[(int)ImGuiCol.TitleBgActive] = titleBg;
        c[(int)ImGuiCol.TitleBgCollapsed] = titleBg;
        c[(int)ImGuiCol.MenuBarBg] = Rgb(0x14161A);
        c[(int)ImGuiCol.ScrollbarBg] = new SysVec4(0, 0, 0, 0);
        c[(int)ImGuiCol.ScrollbarGrab] = bg3;
        c[(int)ImGuiCol.ScrollbarGrabHovered] = borderLight;
        c[(int)ImGuiCol.ScrollbarGrabActive] = accent;
        c[(int)ImGuiCol.CheckMark] = accentHi;
        c[(int)ImGuiCol.SliderGrab] = accent;
        c[(int)ImGuiCol.SliderGrabActive] = accentHi;
        c[(int)ImGuiCol.Button] = bg2;
        c[(int)ImGuiCol.ButtonHovered] = Mix(bg3, accent, 0.18f);
        c[(int)ImGuiCol.ButtonActive] = accentDim;
        c[(int)ImGuiCol.Header] = header;
        c[(int)ImGuiCol.HeaderHovered] = Mix(header, accent, 0.16f);
        c[(int)ImGuiCol.HeaderActive] = accentFaint;
        c[(int)ImGuiCol.Separator] = WithAlpha(Rgb(0xFFFFFF), 0.06f);   // faint light hairline reads on near-black
        c[(int)ImGuiCol.SeparatorHovered] = accent;
        c[(int)ImGuiCol.SeparatorActive] = accentHi;
        c[(int)ImGuiCol.ResizeGrip] = new SysVec4(0, 0, 0, 0);
        c[(int)ImGuiCol.ResizeGripHovered] = accentFaint;
        c[(int)ImGuiCol.ResizeGripActive] = accent;
        // Tabs: selected tab carries an accent top-bar feel via a brighter fill; dimmed tabs recede.
        c[(int)ImGuiCol.Tab] = Rgb(0x1A1D23);
        c[(int)ImGuiCol.TabHovered] = bg3;
        c[(int)ImGuiCol.TabSelected] = header;
        c[(int)ImGuiCol.TabSelectedOverline] = accent;
        c[(int)ImGuiCol.TabDimmed] = Rgb(0x16191E);
        c[(int)ImGuiCol.TabDimmedSelected] = bg2;
        c[(int)ImGuiCol.TabDimmedSelectedOverline] = accentDim;
        c[(int)ImGuiCol.TextSelectedBg] = accentFaint;
        c[(int)ImGuiCol.DragDropTarget] = accentHi;
        c[(int)ImGuiCol.NavCursor] = accentHi;
        c[(int)ImGuiCol.ModalWindowDimBg] = new SysVec4(0.02f, 0.02f, 0.03f, 0.6f);
        c[(int)ImGuiCol.TableHeaderBg] = bg1;
        c[(int)ImGuiCol.TableBorderStrong] = WithAlpha(Rgb(0xFFFFFF), 0.07f);
        c[(int)ImGuiCol.TableBorderLight] = WithAlpha(Rgb(0xFFFFFF), 0.035f);
        c[(int)ImGuiCol.TableRowBg] = new SysVec4(0, 0, 0, 0);
        c[(int)ImGuiCol.TableRowBgAlt] = new SysVec4(1, 1, 1, 0.02f);

        // Docking: the empty central node + drag-preview overlay match the dark base / accent.
        c[(int)ImGuiCol.DockingEmptyBg] = bg0;
        c[(int)ImGuiCol.DockingPreview] = accentFaint;
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
