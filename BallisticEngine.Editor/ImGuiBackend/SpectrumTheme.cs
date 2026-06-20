using Hexa.NET.ImGui;
using SysVec4 = System.Numerics.Vector4;

namespace BallisticEngine.Editor;

// Adobe Spectrum dark theme, ported from the public-domain imgui_spectrum.cpp reference
// (https://github.com/adobe/imgui). A flat, light-on-dark UI built from Spectrum's GRAY ramp +
// accent hues. Kept self-contained so it can be A/B'd against the engine's own graphite theme:
// ImGuiController.ApplyColors decides which one runs. Font baking is NOT ported — the editor keeps
// its Inter font; only the color palette + a few geometry tweaks come from Spectrum.
internal static class SpectrumTheme {
    // ---- Spectrum Dark palette (ARGB hex from the reference's DarkPalette) ----
    // GRAY50..900 (note: in dark mode GRAY50 is the *darkest*, GRAY900 the lightest).
    static readonly SysVec4 Gray50  = Rgb(0x252525);
    static readonly SysVec4 Gray75  = Rgb(0x2F2F2F);
    static readonly SysVec4 Gray100 = Rgb(0x323232);
    static readonly SysVec4 Gray200 = Rgb(0x393939);
    static readonly SysVec4 Gray300 = Rgb(0x3E3E3E);
    static readonly SysVec4 Gray400 = Rgb(0x4D4D4D);
    static readonly SysVec4 Gray500 = Rgb(0x5C5C5C);
    static readonly SysVec4 Gray600 = Rgb(0x7B7B7B);
    static readonly SysVec4 Gray700 = Rgb(0x999999);
    static readonly SysVec4 Gray800 = Rgb(0xCDCDCD);
    static readonly SysVec4 Gray900 = Rgb(0xFFFFFF);

    static readonly SysVec4 Blue400 = Rgb(0x2680EB);
    static readonly SysVec4 Blue500 = Rgb(0x378EF0);
    static readonly SysVec4 Blue600 = Rgb(0x4B9CF5);
    static readonly SysVec4 Blue700 = Rgb(0x5AA9FA);

    // Applies the Spectrum color palette to the current ImGui style. Geometry (rounding/borders) is
    // left to ImGuiController.ApplyGeometry, but Spectrum's defining traits — 1px frame borders and
    // zero shadows — are set here so the look matches the reference.
    public static void Apply() {
        ImGuiStylePtr style = ImGui.GetStyle();
        style.FrameBorderSize = 1f;     // Spectrum frames are outlined, not filled-only

        var col = style.Colors;

        col[(int)ImGuiCol.Text] = Gray800;                    // text on hovered controls is gray900
        col[(int)ImGuiCol.TextDisabled] = Gray500;
        col[(int)ImGuiCol.WindowBg] = Gray100;
        col[(int)ImGuiCol.ChildBg] = new SysVec4(0, 0, 0, 0);
        col[(int)ImGuiCol.PopupBg] = Gray50;                  // applies to tooltips too
        col[(int)ImGuiCol.Border] = Gray300;
        col[(int)ImGuiCol.BorderShadow] = new SysVec4(0, 0, 0, 0);   // Spectrum: no shadows, ever
        col[(int)ImGuiCol.FrameBg] = Gray75;
        col[(int)ImGuiCol.FrameBgHovered] = Gray50;
        col[(int)ImGuiCol.FrameBgActive] = Gray200;
        col[(int)ImGuiCol.TitleBg] = Gray300;
        col[(int)ImGuiCol.TitleBgActive] = Gray200;
        col[(int)ImGuiCol.TitleBgCollapsed] = Gray400;
        col[(int)ImGuiCol.MenuBarBg] = Gray100;
        col[(int)ImGuiCol.ScrollbarBg] = Gray100;
        col[(int)ImGuiCol.ScrollbarGrab] = Gray400;
        col[(int)ImGuiCol.ScrollbarGrabHovered] = Gray600;
        col[(int)ImGuiCol.ScrollbarGrabActive] = Gray700;
        col[(int)ImGuiCol.CheckMark] = Gray50;                // checkmark drawn on a filled blue box
        col[(int)ImGuiCol.SliderGrab] = Gray700;
        col[(int)ImGuiCol.SliderGrabActive] = Gray800;
        col[(int)ImGuiCol.Button] = Gray75;                   // Spectrum "Action Button"
        col[(int)ImGuiCol.ButtonHovered] = Gray50;
        col[(int)ImGuiCol.ButtonActive] = Gray200;
        col[(int)ImGuiCol.Header] = Blue400;
        col[(int)ImGuiCol.HeaderHovered] = Blue500;
        col[(int)ImGuiCol.HeaderActive] = Blue600;
        col[(int)ImGuiCol.Separator] = Gray400;
        col[(int)ImGuiCol.SeparatorHovered] = Gray600;
        col[(int)ImGuiCol.SeparatorActive] = Gray700;
        col[(int)ImGuiCol.ResizeGrip] = Gray400;
        col[(int)ImGuiCol.ResizeGripHovered] = Gray600;
        col[(int)ImGuiCol.ResizeGripActive] = Gray700;
        col[(int)ImGuiCol.PlotLines] = Blue400;
        col[(int)ImGuiCol.PlotLinesHovered] = Blue600;
        col[(int)ImGuiCol.PlotHistogram] = Blue400;
        col[(int)ImGuiCol.PlotHistogramHovered] = Blue600;
        col[(int)ImGuiCol.TextSelectedBg] = WithAlpha(Blue400, 0.20f);
        col[(int)ImGuiCol.DragDropTarget] = new SysVec4(1f, 1f, 0f, 0.9f);
        col[(int)ImGuiCol.NavCursor] = WithAlpha(Gray900, 0.04f);
        col[(int)ImGuiCol.NavWindowingHighlight] = new SysVec4(1f, 1f, 1f, 0.7f);
        col[(int)ImGuiCol.NavWindowingDimBg] = new SysVec4(0.8f, 0.8f, 0.8f, 0.2f);
        col[(int)ImGuiCol.ModalWindowDimBg] = new SysVec4(0.2f, 0.2f, 0.2f, 0.35f);
        col[(int)ImGuiCol.Tab] = Gray300;
        col[(int)ImGuiCol.TabSelected] = Blue500;
        col[(int)ImGuiCol.TabHovered] = Blue700;
        col[(int)ImGuiCol.TabDimmed] = Gray400;
        col[(int)ImGuiCol.TabDimmedSelected] = Blue700;

        // Docking surfaces aren't in the reference; tie them to the palette so the central node and
        // drag-preview don't fall back to stock ImGui purple.
        col[(int)ImGuiCol.DockingEmptyBg] = Gray100;
        col[(int)ImGuiCol.DockingPreview] = WithAlpha(Blue400, 0.30f);
        col[(int)ImGuiCol.TableHeaderBg] = Gray200;
        col[(int)ImGuiCol.TableBorderStrong] = Gray300;
        col[(int)ImGuiCol.TableBorderLight] = Gray200;
        col[(int)ImGuiCol.TableRowBg] = new SysVec4(0, 0, 0, 0);
        col[(int)ImGuiCol.TableRowBgAlt] = WithAlpha(Gray900, 0.02f);
    }

    static SysVec4 WithAlpha(SysVec4 c, float a) => new(c.X, c.Y, c.Z, a);

    static SysVec4 Rgb(int hex) => new(
        ((hex >> 16) & 0xFF) / 255f,
        ((hex >> 8) & 0xFF) / 255f,
        (hex & 0xFF) / 255f,
        1f);
}
