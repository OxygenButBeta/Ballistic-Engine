using Hexa.NET.ImGui;
using SysVec4 = System.Numerics.Vector4;

namespace BallisticEngine.Editor;

internal static class EditorTheme {
    public static ImFontPtr Display { get; internal set; }
    public static ImFontPtr Header  { get; internal set; }
    public static ImFontPtr Body    { get; internal set; }
    public static ImFontPtr Caption { get; internal set; }

    public static float BodySize { get; internal set; } = 16.5f;

    public static float UiScale { get; internal set; } = 1f;

    public const float DisplayScale = 1.40f;
    public const float HeaderScale  = 1.05f;
    public const float CaptionScale = 0.84f;

    public static readonly SysVec4 RowLabel   = new(0.92f, 0.92f, 0.94f, 1f);
    public static readonly SysVec4 RowCaption = new(0.70f, 0.70f, 0.73f, 1f);

    public static readonly SysVec4 SliderGrabRest = Rgb(0x8A6A30);

    public static SysVec4 RowHoverFill(SysVec4 accent) => new(accent.X, accent.Y, accent.Z, 0.055f);
    public static SysVec4 RowHoverBar(SysVec4 accent)  => new(accent.X, accent.Y, accent.Z, 0.85f);

    public const float RowAccentBarWidth = 2.5f;

    public static readonly SysVec4 Bg0         = Rgb(0x101012);
    public static readonly SysVec4 Bg1         = Rgb(0x222226);
    public static readonly SysVec4 Bg2         = Rgb(0x303035);
    public static readonly SysVec4 Bg3         = Rgb(0x42424A);
    public static readonly SysVec4 HeaderBg    = Rgb(0x36363B);
    public static readonly SysVec4 Text        = Rgb(0xF2F3F4);
    public static readonly SysVec4 TextDim     = Rgb(0xA6A6AB);
    public static readonly SysVec4 Border      = Rgb(0x060608);
    public static readonly SysVec4 BorderLight = Rgb(0x4E4E55);
    public static readonly SysVec4 TitleBg     = Rgb(0x0A0A0C);

    public static readonly SysVec4 OverlayBg   = new(0.063f, 0.063f, 0.071f, 0.88f);
    public static readonly SysVec4 OverlayPill = new(0.0f, 0.0f, 0.0f, 0.30f);
    public static readonly SysVec4 OverlayBorder = new(1f, 1f, 1f, 0.07f);
    public const float OverlayRounding = 7f;
    public const float OverlayMargin   = 10f;

    public static readonly SysVec4 Error     = Rgb(0xFF8066);
    public static readonly SysVec4 Warning   = Rgb(0xFFB840);
    public static readonly SysVec4 Success   = Rgb(0x80D980);
    public static readonly SysVec4 Info      = Rgb(0xF0C060);
    public static readonly SysVec4 PrefabBlue = Rgb(0x73A8FF);
    public static readonly SysVec4 RowChild  = Rgb(0xB8BDC7);
    public static readonly SysVec4 IconMuted = new(0.45f, 0.47f, 0.52f, 0.6f);

    public static readonly SysVec4 PrimaryAction        = Rgb(0x33A352);
    public static readonly SysVec4 PrimaryActionHovered = Rgb(0x44C268);
    public static readonly SysVec4 PrimaryActionActive  = Rgb(0x2A8C44);

    public static readonly SysVec4 Destructive        = Rgb(0x8C3329);
    public static readonly SysVec4 DestructiveHovered = Rgb(0xAD4233);

    public static readonly SysVec4 FolderTint = Rgb(0xEBC25C);
    public static SysVec4 FolderTintDim => new(0xDB / 255f, 0xB3 / 255f, 0x57 / 255f, 0.75f);

    public static readonly SysVec4[] LogLevel = [
        Rgb(0x8C949F), Rgb(0xF2CC4D), Rgb(0xF26152),
    ];

    public static readonly SysVec4 Hairline = new(1f, 1f, 1f, 0.07f);
    public static readonly SysVec4 TreeGuide = new(1f, 1f, 1f, 0.16f);

    public static readonly SysVec4 PopupBg = Rgb(0x1F2127);
    public static readonly SysVec4 InputBg = Rgb(0x121419);

    static SysVec4 Rgb(int hex) => new(
        ((hex >> 16) & 0xFF) / 255f, ((hex >> 8) & 0xFF) / 255f, (hex & 0xFF) / 255f, 1f);
}
