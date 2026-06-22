using Hexa.NET.ImGui;
using SysVec4 = System.Numerics.Vector4;

namespace BallisticEngine.Editor;

internal static class DraculaTheme {
    public static void Apply() {
        ImGuiStylePtr style = ImGui.GetStyle();
        style.FrameBorderSize = 0f;
        style.WindowBorderSize = 0f;
        style.PopupBorderSize = 1f;

        var c = style.Colors;

        SysVec4 bg0 = Rgb(0x1E1E22);
        SysVec4 bg1 = Rgb(0x26262C);
        SysVec4 bg2 = Rgb(0x2C2D33);
        SysVec4 bg3 = Rgb(0x35363E);
        SysVec4 dark = Rgb(0x17171A);

        SysVec4 accent = Rgb(0x9A86C4);
        SysVec4 accentHi = Rgb(0xB7A6DC);
        SysVec4 accentFaint = WithAlpha(accent, 0.22f);

        SysVec4 text = Rgb(0xE4E4EA);
        SysVec4 textDim = Rgb(0x7E8294);

        c[(int)ImGuiCol.Text] = text;
        c[(int)ImGuiCol.TextDisabled] = textDim;

        c[(int)ImGuiCol.WindowBg] = bg0;
        c[(int)ImGuiCol.ChildBg] = new SysVec4(0, 0, 0, 0);
        c[(int)ImGuiCol.PopupBg] = bg1;

        c[(int)ImGuiCol.Border] = WithAlpha(Rgb(0xFFFFFF), 0.05f);
        c[(int)ImGuiCol.BorderShadow] = new SysVec4(0, 0, 0, 0);

        c[(int)ImGuiCol.FrameBg] = bg2;
        c[(int)ImGuiCol.FrameBgHovered] = Mix(bg3, accent, 0.12f);
        c[(int)ImGuiCol.FrameBgActive] = accentFaint;

        c[(int)ImGuiCol.TitleBg] = dark;
        c[(int)ImGuiCol.TitleBgActive] = dark;
        c[(int)ImGuiCol.TitleBgCollapsed] = dark;
        c[(int)ImGuiCol.MenuBarBg] = dark;

        c[(int)ImGuiCol.ScrollbarBg] = new SysVec4(0, 0, 0, 0);
        c[(int)ImGuiCol.ScrollbarGrab] = bg3;
        c[(int)ImGuiCol.ScrollbarGrabHovered] = Rgb(0x44454F);
        c[(int)ImGuiCol.ScrollbarGrabActive] = accent;

        c[(int)ImGuiCol.CheckMark] = accentHi;
        c[(int)ImGuiCol.SliderGrab] = accent;
        c[(int)ImGuiCol.SliderGrabActive] = accentHi;
        c[(int)ImGuiCol.Button] = bg2;
        c[(int)ImGuiCol.ButtonHovered] = Mix(bg3, accent, 0.20f);
        c[(int)ImGuiCol.ButtonActive] = accentFaint;
        c[(int)ImGuiCol.Header] = bg1;
        c[(int)ImGuiCol.HeaderHovered] = Mix(bg1, accent, 0.16f);
        c[(int)ImGuiCol.HeaderActive] = accentFaint;

        c[(int)ImGuiCol.Separator] = WithAlpha(Rgb(0xFFFFFF), 0.06f);
        c[(int)ImGuiCol.SeparatorHovered] = accent;
        c[(int)ImGuiCol.SeparatorActive] = accentHi;
        c[(int)ImGuiCol.ResizeGrip] = new SysVec4(0, 0, 0, 0);
        c[(int)ImGuiCol.ResizeGripHovered] = accentFaint;
        c[(int)ImGuiCol.ResizeGripActive] = accent;

        c[(int)ImGuiCol.Tab] = dark;
        c[(int)ImGuiCol.TabHovered] = bg2;
        c[(int)ImGuiCol.TabSelected] = bg1;
        c[(int)ImGuiCol.TabSelectedOverline] = accent;
        c[(int)ImGuiCol.TabDimmed] = dark;
        c[(int)ImGuiCol.TabDimmedSelected] = bg0;

        c[(int)ImGuiCol.TableHeaderBg] = bg1;
        c[(int)ImGuiCol.TableBorderStrong] = WithAlpha(Rgb(0xFFFFFF), 0.07f);
        c[(int)ImGuiCol.TableBorderLight] = WithAlpha(Rgb(0xFFFFFF), 0.035f);
        c[(int)ImGuiCol.TableRowBg] = new SysVec4(0, 0, 0, 0);
        c[(int)ImGuiCol.TableRowBgAlt] = new SysVec4(1, 1, 1, 0.02f);

        c[(int)ImGuiCol.PlotLines] = accent;
        c[(int)ImGuiCol.PlotHistogram] = accent;
        c[(int)ImGuiCol.TextSelectedBg] = accentFaint;
        c[(int)ImGuiCol.DragDropTarget] = accentHi;
        c[(int)ImGuiCol.NavCursor] = accentHi;
        c[(int)ImGuiCol.ModalWindowDimBg] = new SysVec4(0.02f, 0.02f, 0.03f, 0.6f);

        c[(int)ImGuiCol.DockingPreview] = accentFaint;
        c[(int)ImGuiCol.DockingEmptyBg] = bg0;
    }

    static SysVec4 Mix(SysVec4 a, SysVec4 b, float t) => new(
        a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t, a.W);

    static SysVec4 WithAlpha(SysVec4 c, float a) => new(c.X, c.Y, c.Z, a);

    static SysVec4 Rgb(int hex) => new(
        ((hex >> 16) & 0xFF) / 255f,
        ((hex >> 8) & 0xFF) / 255f,
        (hex & 0xFF) / 255f,
        1f);
}
