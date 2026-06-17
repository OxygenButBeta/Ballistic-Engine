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
        float baseSize = 17f;                        // EF5e: bumped — bigger UI text reads far less "çiğ"/cramped
        float size = MathF.Round(baseSize * scale);
        EditorTheme.BodySize = size;

        // EF5e — the BODY (default UI) font is now Inter SEMIBOLD, not Regular. Inter-Regular rendered thin /
        // "çirkin" at UI sizes (the user's complaint); a medium weight is what AAA editors (UE5/Blender) use
        // for body text — denser stems, far more legible, no longer spindly. Regular is kept only for the
        // recessive Caption (secondary hints look right thin). SemiBold-or-fallback chosen FIRST so body can
        // use it; if SemiBold is missing we fall back to Regular, then to the default font.
        bool haveSemibold = File.Exists(semibold);
        bool haveRegular = File.Exists(regular);
        string bodyTtf = haveSemibold ? semibold : (haveRegular ? regular : null);

        ImFontPtr bodyFont, captionFont, headerFont, displayFont;
        if (bodyTtf is not null) {
            bodyFont = AddTextFont(io, bodyTtf, size);
        }
        else {
            bodyFont = io.Fonts.AddFontDefault();
            io.FontGlobalScale = scale;
        }
        MergeIcons(io, icons, size);
        Bold = bodyFont;   // body is already semibold; "Bold" call sites get the same weight (overridden below if a heavier exists)

        // Phase E (RW2) — a SEMANTIC TYPE SCALE so headers read as headers and captions recede (the #1
        // flatness fix). Real distinct pixel sizes baked into the atlas (NOT just bold weight). Each falls
        // back to the body font if the .ttf is missing, so EditorTheme handles are always valid.
        // Caption: a smaller REGULAR size for secondary hints / badges (thin is correct for recessive text).
        captionFont = haveRegular
            ? LoadSizedWithIcons(io, regular, icons, MathF.Round(baseSize * EditorTheme.CaptionScale * scale))
            : bodyFont;

        // Header / Display: SemiBold at larger sizes (real type-scale, not just weight).
        if (haveSemibold) {
            headerFont  = LoadSizedWithIcons(io, semibold, icons, MathF.Round(baseSize * EditorTheme.HeaderScale * scale));
            displayFont = LoadSizedWithIcons(io, semibold, icons, MathF.Round(baseSize * EditorTheme.DisplayScale * scale));
        }
        else {
            headerFont = displayFont = bodyFont;
        }

        EditorTheme.Body    = bodyFont;
        EditorTheme.Caption = captionFont;
        EditorTheme.Header  = headerFont;
        EditorTheme.Display = displayFont;

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

    // Loads a TTF at a specific px size and merges the icon glyphs into it (so any semantic-scale font can
    // still render inline icons). Returns the font handle for EditorTheme.
    static unsafe ImFontPtr LoadSizedWithIcons(ImGuiIOPtr io, string ttf, string icons, float px) {
        ImFontPtr f = AddTextFont(io, ttf, px);
        MergeIcons(io, icons, px);
        return f;
    }

    // Loads a TEXT font (Inter) with crisp rasterization. The default stb_truetype path under-samples thin UI
    // fonts, which is why Inter read soft/"çirkin" before — EF5e: OversampleH=3 gives horizontal subpixel
    // coverage (much sharper stems at UI sizes) and PixelSnapH snaps glyph origins to the pixel grid so text
    // baselines don't blur. OversampleV stays 1 (vertical oversampling barely helps text, costs atlas space).
    static unsafe ImFontPtr AddTextFont(ImGuiIOPtr io, string ttf, float px) {
        ImFontConfigPtr cfg = ImGui.ImFontConfig();
        cfg.OversampleH = 3;
        cfg.OversampleV = 1;
        cfg.PixelSnapH = true;
        cfg.RasterizerMultiply = 1.15f;   // slightly thicken/contrast glyph coverage so text reads crisp, not washed
        ImFontPtr f = io.Fonts.AddFontFromFileTTF(ttf, px, cfg);
        cfg.Destroy();
        return f;
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

        // EF5e — REAL faithful-UE5 geometry (the EF5a pass was too timid to read). Unreal's editor chrome is
        // nearly RECTANGULAR (2-3px rounding, not the soft 4-6px), uses thin 1px borders on input frames so
        // each control reads as a distinct recessed slot (the UE5 "Details panel" tell), crisp tabs with NO
        // rounding on the tab body, and a tighter vertical rhythm. These metrics are gravitating the look
        // away from "default ImGui" decisively, not by a hair. Pure layout (behaviour unchanged).
        style.WindowRounding = 3f;
        style.ChildRounding = 3f;
        style.FrameRounding = 3f;
        style.PopupRounding = 3f;
        style.GrabRounding = 3f;
        style.TabRounding = 3f;
        style.ScrollbarRounding = 3f;
        style.WindowBorderSize = 0f;        // panels read as surfaces, not framed boxes
        style.FrameBorderSize = 1f;         // UE5: each input slot has a thin recessed border (the key tell)
        style.PopupBorderSize = 1f;         // keep a hairline on floating popups for separation
        style.ChildBorderSize = 0f;
        style.TabBorderSize = 0f;
        style.WindowPadding = new SysVec2(10, 8);
        style.FramePadding = new SysVec2(10, 7);    // taller input rows — more breathing room, less cramped
        style.CellPadding = new SysVec2(8, 6);
        style.ItemSpacing = new SysVec2(8, 7);      // a touch more vertical air between rows
        style.ItemInnerSpacing = new SysVec2(7, 5);
        style.IndentSpacing = 16f;          // shallower indent so nested rows keep their value column
        style.ScrollbarSize = 12f;
        style.GrabMinSize = 10f;
        style.TabBarBorderSize = 1f;        // a thin rule under the tab bar (UE5 separates tabs from body)
        style.DockingSeparatorSize = 2f;    // thin dock split handles
        style.SeparatorTextBorderSize = 2f;
        style.SeparatorTextPadding = new SysVec2(18, 5);
        style.WindowTitleAlign = new SysVec2(0.0f, 0.5f);
        style.WindowMenuButtonPosition = ImGuiDir.None;   // no collapse arrow clutter on dock tabs
        style.ScaleAllSizes(scale);
    }

    // EF5e — REAL faithful-UE5 "deep graphite" theme. The EF5a pass barely moved off the old palette
    // (~4 hex tones, invisible to the eye); this is the actual overhaul. Unreal's Slate editor reads as:
    // a VERY dark, slightly cool base; a HIGH-CONTRAST elevation ramp so panels / inputs / headers each
    // sit on a visibly distinct surface; thin recessed borders on every input slot; and the azure accent
    // used DECISIVELY (bright check/slider grabs, a fat accent overline on the selected tab, accent on the
    // selected tree/table row) instead of the timid faint tints before. Every interaction state derives
    // from accent + ramp. Safe to re-run any frame (colors only).
    // NOTE: the bg0..titleBg ramp below is mirrored byte-for-byte in EditorTheme (Bg0..TitleBg) for the
    // in-viewport overlay chrome — RETUNE EditorTheme TOO when this changes (its comment mandates the sync).
    static void ApplyColors(SysVec4 accent) {
        var c = ImGui.GetStyle().Colors;

        // High-contrast cool-graphite elevation ramp. Base pushed much deeper (near Slate's #0E0F12), then
        // each layer steps up CLEARLY (≈+8-10 luma per level, not +3) so depth is legible: window < child <
        // header-bar < input-frame < hovered. The accent pops hard against this dark a base. Contrasts:
        // body #EDEFF3 ≥ 13:1 on every surface; textDim ≥ 4.6:1 even on the lightest input frame.
        // EF5e — cool BLUE-slate ramp (not neutral grey). A faint blue bias in every surface gives the
        // chrome a deliberate, modern identity instead of the flat neutral-grey "default ImGui" feel; the
        // azure accent then sits in the SAME hue family so it integrates rather than clashing.
        SysVec4 bg0 = Rgb(0x0C0E13);     // window background — deepest blue-charcoal
        SysVec4 bg1 = Rgb(0x141822);     // child / popup / panel body — raised surface
        SysVec4 bg2 = Rgb(0x1E2430);     // input frames — clearly lighter so a slot reads as a slot
        SysVec4 bg3 = Rgb(0x2B3340);     // hovered frames / scrollbar grab
        SysVec4 header = Rgb(0x222B3A);  // collapsing headers / component-title bands / selected tabs (bluer)
        SysVec4 headerHi = Rgb(0x2F3A4D);// header hovered — brighter blue-slate
        SysVec4 accentHi = Lighten(accent, 1.30f);
        SysVec4 accentDim = Darken(accent, 0.55f);
        SysVec4 accentMid = WithAlpha(accent, 0.55f);
        SysVec4 accentFaint = WithAlpha(accent, 0.20f);
        SysVec4 accentRow = WithAlpha(accent, 0.32f);   // selected tree / table row — clearly readable
        SysVec4 text = Rgb(0xEDEFF3);    // bright primary text (≥13:1 on all surfaces)
        SysVec4 textDim = Rgb(0x9098A6); // secondary/disabled — clears 4.5:1 on the lightest input frame
        SysVec4 border = Rgb(0x05070A);  // outer seam (popups/tables) — near-black, defines panel edges
        SysVec4 frameBorder = Rgb(0x000000);            // 1px recessed border on input frames (the UE5 tell)
        SysVec4 borderLight = Rgb(0x3A4150);
        SysVec4 titleBg = Rgb(0x0A0C0F);

        c[(int)ImGuiCol.Text] = text;
        c[(int)ImGuiCol.TextDisabled] = textDim;
        c[(int)ImGuiCol.WindowBg] = bg0;
        c[(int)ImGuiCol.ChildBg] = new SysVec4(0, 0, 0, 0);
        c[(int)ImGuiCol.PopupBg] = bg1;
        c[(int)ImGuiCol.Border] = frameBorder;          // FrameBorderSize=1 now draws this on every input slot
        c[(int)ImGuiCol.BorderShadow] = new SysVec4(0, 0, 0, 0);
        c[(int)ImGuiCol.FrameBg] = bg2;
        c[(int)ImGuiCol.FrameBgHovered] = Mix(bg2, accent, 0.16f);
        c[(int)ImGuiCol.FrameBgActive] = Mix(bg2, accent, 0.28f);
        c[(int)ImGuiCol.TitleBg] = titleBg;
        c[(int)ImGuiCol.TitleBgActive] = header;            // focused panel's title bar reads as raised (blue-slate)
        c[(int)ImGuiCol.TitleBgCollapsed] = titleBg;
        c[(int)ImGuiCol.MenuBarBg] = Rgb(0x0A0C11);
        c[(int)ImGuiCol.ScrollbarBg] = new SysVec4(0, 0, 0, 0);
        c[(int)ImGuiCol.ScrollbarGrab] = bg3;
        c[(int)ImGuiCol.ScrollbarGrabHovered] = borderLight;
        c[(int)ImGuiCol.ScrollbarGrabActive] = accent;
        c[(int)ImGuiCol.CheckMark] = accentHi;          // bright azure check — clearly the accent
        c[(int)ImGuiCol.SliderGrab] = accent;
        c[(int)ImGuiCol.SliderGrabActive] = accentHi;
        c[(int)ImGuiCol.Button] = bg2;
        c[(int)ImGuiCol.ButtonHovered] = Mix(bg3, accent, 0.22f);
        c[(int)ImGuiCol.ButtonActive] = accentDim;
        c[(int)ImGuiCol.Header] = Mix(header, accent, 0.18f);   // selected tree row / collapsing header — azure-tinted
        c[(int)ImGuiCol.HeaderHovered] = Mix(headerHi, accent, 0.10f);
        c[(int)ImGuiCol.HeaderActive] = accentRow;
        c[(int)ImGuiCol.Separator] = WithAlpha(Rgb(0xFFFFFF), 0.08f);   // light hairline reads on near-black
        c[(int)ImGuiCol.SeparatorHovered] = accent;
        c[(int)ImGuiCol.SeparatorActive] = accentHi;
        c[(int)ImGuiCol.ResizeGrip] = new SysVec4(0, 0, 0, 0);
        c[(int)ImGuiCol.ResizeGripHovered] = accentFaint;
        c[(int)ImGuiCol.ResizeGripActive] = accent;
        // Tabs (UE5 signature): selected tab sits on the header surface with a FAT bright-azure overline +
        // a faint accent tint so the active document reads instantly; unselected tabs recede into near-black.
        c[(int)ImGuiCol.Tab] = Rgb(0x0E1119);
        c[(int)ImGuiCol.TabHovered] = headerHi;
        c[(int)ImGuiCol.TabSelected] = Mix(header, accent, 0.12f);   // selected tab carries a faint azure wash
        c[(int)ImGuiCol.TabSelectedOverline] = accentHi;
        c[(int)ImGuiCol.TabDimmed] = Rgb(0x0C0E12);
        c[(int)ImGuiCol.TabDimmedSelected] = bg2;
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

        // Docking: the empty central node + drag-preview overlay match the dark base / accent.
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
