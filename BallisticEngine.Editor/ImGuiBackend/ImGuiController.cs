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
        // EF5i — switched the UI font to CALIBRI (user: Inter SemiBold read "çirkin"). Calibri is the soft,
        // rounded, highly-legible Windows humanist font; it reads clean and friendly at UI sizes where Inter
        // looked harsh. Body = Calibri Regular (Calibri's own weight is already comfortable — no SemiBold
        // needed); Header/Display = Calibri Bold at larger sizes for the type scale. Inter is kept as a
        // FALLBACK if the Calibri files are missing (older checkouts), then the default font.
        var calibri = Path.Combine(fontsDir, "Calibri-Regular.ttf");
        var calibriBold = Path.Combine(fontsDir, "Calibri-Bold.ttf");
        var interRegular = Path.Combine(fontsDir, "Inter-Regular.ttf");
        var interSemibold = Path.Combine(fontsDir, "Inter-SemiBold.ttf");
        var icons = File.Exists(Path.Combine(fontsDir, "lucide.ttf"))
            ? Path.Combine(fontsDir, "lucide.ttf") : null;
        // Calibri carries a touch more x-height presence than Inter, so the base size can come down slightly
        // and still read larger — keeps the UI from feeling oversized while staying very legible.
        // EF5i detail: 16.5→15.5 — the 16.5 menu/body text read a touch oversized; 15.5 Calibri is tighter and
        // more refined while still very legible (Calibri's generous x-height holds up at the smaller size).
        float baseSize = 15.5f;
        float size = MathF.Round(baseSize * scale);
        EditorTheme.BodySize = size;
        EditorTheme.UiScale = scale;   // EF16: published so static layout helpers convert pre-DPI metrics to px

        // Resolve the regular/bold pair: prefer Calibri, fall back to Inter (Regular + SemiBold), then default.
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
        Bold = bodyFont;   // safe default; overwritten with the real bold face below once it's loaded

        // Phase E (RW2) — a SEMANTIC TYPE SCALE so headers read as headers and captions recede (the #1
        // flatness fix). Real distinct pixel sizes baked into the atlas (NOT just bold weight). Each falls
        // back to the body font if the .ttf is missing, so EditorTheme handles are always valid.
        // Caption: a smaller size of the SAME regular face for secondary hints / badges (recessive by size).
        captionFont = regularTtf is not null
            ? LoadSizedWithIcons(io, regularTtf, icons, MathF.Round(baseSize * EditorTheme.CaptionScale * scale))
            : bodyFont;

        // Header / Display: BOLD at larger sizes (real type-scale = size + weight).
        if (boldTtf is not null) {
            headerFont  = LoadSizedWithIcons(io, boldTtf, icons, MathF.Round(baseSize * EditorTheme.HeaderScale * scale));
            displayFont = LoadSizedWithIcons(io, boldTtf, icons, MathF.Round(baseSize * EditorTheme.DisplayScale * scale));
            Bold = headerFont;   // "Bold" call sites get the bold face (at header size — the common use)
        }
        else {
            headerFont = displayFont = bodyFont;
            Bold = bodyFont;
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

    // Loads a TEXT font with crisp rasterization. The default stb_truetype path under-samples thin UI fonts —
    // OversampleH=3 gives horizontal subpixel coverage (much sharper stems at UI sizes) and PixelSnapH snaps
    // glyph origins to the pixel grid so text baselines don't blur. OversampleV stays 1 (vertical oversampling
    // barely helps text, costs atlas space).
    // EF5i: RasterizerMultiply dropped 1.15→1.0. The 1.15 thickening was added to rescue Inter's thin stems;
    // Calibri is a soft, well-hinted humanist face that needs NO artificial thickening — at 1.15 it rendered
    // muddy/heavy. 1.0 keeps Calibri clean and rounded as designed.
    static unsafe ImFontPtr AddTextFont(ImGuiIOPtr io, string ttf, float px) {
        ImFontConfigPtr cfg = ImGui.ImFontConfig();
        cfg.OversampleH = 3;
        cfg.OversampleV = 1;
        cfg.PixelSnapH = true;
        cfg.RasterizerMultiply = 1.0f;   // no artificial thickening — Calibri is soft + well-hinted as-is
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

    // EF5i — NEUTRAL graphite + warm-amber theme (the EF5e/h blue-slate identity was rejected by the user:
    // "bu genel mavilik de hoşuma gitmedi"). The chrome is now pure neutral grey (zero blue bias) like VS
    // Code Dark+ / Blender: a VERY dark base; a HIGH-CONTRAST elevation ramp so panels / inputs / headers
    // each sit on a visibly distinct surface; thin recessed borders on every input slot; and a single warm
    // AMBER accent used DECISIVELY (bright check/slider grabs, a fat accent overline on the selected tab,
    // accent on the selected tree/table row) carrying all the color identity. Every interaction state derives
    // from accent + ramp. Safe to re-run any frame (colors only).
    // NOTE: the bg0..titleBg ramp below is mirrored byte-for-byte in EditorTheme (Bg0..TitleBg) for the
    // in-viewport overlay chrome — RETUNE EditorTheme TOO when this changes (its comment mandates the sync).
    static void ApplyColors(SysVec4 accent) {
        var c = ImGui.GetStyle().Colors;

        // EF5i — NEUTRAL graphite ramp + warm amber accent (user rejected the EF5e/h blue cast: "bu genel
        // mavilik de hoşuma gitmedi"). Every surface is now pure NEUTRAL grey (R==G==B, ZERO blue bias) —
        // clean/professional like VS Code Dark+ / Blender. The ramp stays DECISIVE (≈+12-16 luma per level)
        // so window < child < input < hovered separate at a glance, with a near-black top shell band for the
        // menu/title bars. The single warm AMBER accent carries all the color identity against the cool-
        // neutral grey. Contrasts (WCAG): body #F2F3F4 ≥ 14:1 on every surface; textDim ≥ 4.6:1 on the
        // lightest input frame; amber accent ≥ 6:1 on bg0.
        // EF5i contrast/elevation retune: widen the bottom of the ramp so the PANEL BODY clearly reads as a
        // raised surface above the near-black window gutter. The whole ramp was also LIFTED (+~5-6 luma per
        // level) because the user found the surfaces "too dim" — brighter panels, still clearly stepped so
        // window < child < input < hovered separate at a glance. bg0/shell stay deep so the chrome still frames.
        SysVec4 bg0 = Rgb(0x101012);     // window background / gutter — deep neutral charcoal (lifted off near-black)
        SysVec4 bg1 = Rgb(0x222226);     // child / popup / panel body — clearly raised neutral surface
        SysVec4 bg2 = Rgb(0x303035);     // input frames — clearly lighter so a slot reads as a slot
        SysVec4 bg3 = Rgb(0x42424A);     // hovered frames / scrollbar grab — another clear step
        SysVec4 header = Rgb(0x36363B);  // collapsing headers / component-title bands — neutral, distinctly lighter
        SysVec4 headerHi = Rgb(0x46464E);// header hovered — brighter neutral graphite
        SysVec4 shell = Rgb(0x0A0A0C);   // top shell: menu bar + title bars — own near-black band so chrome frames the app
        SysVec4 accentHi = Lighten(accent, 1.18f);
        SysVec4 accentDim = Darken(accent, 0.58f);
        SysVec4 accentMid = WithAlpha(accent, 0.60f);
        SysVec4 accentFaint = WithAlpha(accent, 0.22f);
        SysVec4 accentRow = WithAlpha(accent, 0.30f);   // selected tree / table row — readable warm amber wash
        SysVec4 text = Rgb(0xF2F3F4);    // bright primary text (≥14:1 on all surfaces)
        SysVec4 textDim = Rgb(0xA6A6AB); // secondary/disabled — LIFTED (user: text too dim); still clearly recessive vs primary
        SysVec4 border = Rgb(0x060608);  // outer seam (popups/tables) — near-black, defines panel edges
        SysVec4 frameBorder = Rgb(0x070708);            // 1px recessed border on input frames (the UE5 tell)
        SysVec4 borderLight = Rgb(0x4E4E55);
        SysVec4 titleBg = shell;

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
        c[(int)ImGuiCol.TitleBgActive] = header;            // focused panel's title bar reads as raised (cobalt-slate)
        c[(int)ImGuiCol.TitleBgCollapsed] = titleBg;
        c[(int)ImGuiCol.MenuBarBg] = shell;                 // top shell band — frames the app, clearly darker than panels
        c[(int)ImGuiCol.ScrollbarBg] = new SysVec4(0, 0, 0, 0);
        c[(int)ImGuiCol.ScrollbarGrab] = bg3;
        c[(int)ImGuiCol.ScrollbarGrabHovered] = borderLight;
        c[(int)ImGuiCol.ScrollbarGrabActive] = accent;
        c[(int)ImGuiCol.CheckMark] = accentHi;          // bright azure check — clearly the accent
        c[(int)ImGuiCol.SliderGrab] = accent;
        c[(int)ImGuiCol.SliderGrabActive] = accentHi;
        // EF5i: buttons read as ACTIONABLE, not disabled — a lifted bg3 base (was the dim bg2 that looked
        // greyed out), a warm amber-leaning hover, and an unmistakable amber active state. Tints kept modest
        // so a button stays a neutral control until touched (amber is strong — a heavy resting tint reads gaudy).
        c[(int)ImGuiCol.Button] = bg3;                          // lifted neutral so it reads as a control
        c[(int)ImGuiCol.ButtonHovered] = Mix(bg3, accent, 0.26f);
        c[(int)ImGuiCol.ButtonActive] = Mix(bg3, accent, 0.55f);
        c[(int)ImGuiCol.Header] = Mix(header, accent, 0.20f);   // selected tree row / collapsing header — amber-tinted
        c[(int)ImGuiCol.HeaderHovered] = Mix(headerHi, accent, 0.12f);
        c[(int)ImGuiCol.HeaderActive] = accentRow;
        c[(int)ImGuiCol.Separator] = WithAlpha(Rgb(0xFFFFFF), 0.08f);   // light hairline reads on near-black
        c[(int)ImGuiCol.SeparatorHovered] = accent;
        c[(int)ImGuiCol.SeparatorActive] = accentHi;
        c[(int)ImGuiCol.ResizeGrip] = new SysVec4(0, 0, 0, 0);
        c[(int)ImGuiCol.ResizeGripHovered] = accentFaint;
        c[(int)ImGuiCol.ResizeGripActive] = accent;
        // EF5i — tabs that READ AS TABS. Unselected tabs recede into the shell band (clearly darker than the
        // panel body), the SELECTED tab is LIFTED to the panel-body surface + carries a faint warm amber wash +
        // a FAT bright amber overline, and hover steps up visibly. The selected tab visually "connects" to its
        // panel below it (same surface family) — the UE5 tell. Amber wash kept light (it's a strong hue).
        c[(int)ImGuiCol.Tab] = shell;                                // unselected — recessed into the shell band
        c[(int)ImGuiCol.TabHovered] = Mix(header, accent, 0.14f);
        c[(int)ImGuiCol.TabSelected] = Mix(bg1, accent, 0.14f);      // selected lifts to the panel surface + faint amber wash
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
