using Hexa.NET.ImGui;
using SysVec2 = System.Numerics.Vector2;
using SysVec4 = System.Numerics.Vector4;

namespace BallisticEngine.Editor;

internal static class BusyOverlay {
    static float anim;
    static float dots;

    public static void Draw(float s) {
        var buildingPlayer = BuildProgress.IsBuilding;
        var unityImport = UnityImportWindow.IsBusy;
        var busy = AsyncAssetImport.IsBusy || SceneCommands.IsLoading || buildingPlayer || unityImport;
        if (!busy)
            return;

        var importDeterminate = AsyncAssetImport.IsBusy && AsyncAssetImport.Fraction >= 0f;
        var determinate = buildingPlayer || unityImport;

        ImGuiIOPtr io = ImGui.GetIO();
        SysVec2 display = io.DisplaySize;
        float dt = io.DeltaTime > 0 ? io.DeltaTime : 1f / 60f;
        anim = (anim + dt * 0.9f) % 1f;
        dots = (dots + dt) % 1.5f;

        ImGui.SetNextWindowPos(SysVec2.Zero);
        ImGui.SetNextWindowSize(display);
        ImGui.SetNextWindowBgAlpha(0f);
        ImGui.SetNextWindowFocus();
        const ImGuiWindowFlags blockFlags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoDocking |
            ImGuiWindowFlags.NoNav;
        ImGui.Begin("##busyblocker", blockFlags);
        ImGui.SetCursorPos(SysVec2.Zero);
        ImGui.InvisibleButton("##busyeat", display, ImGuiButtonFlags.MouseButtonLeft |
            ImGuiButtonFlags.MouseButtonRight | ImGuiButtonFlags.MouseButtonMiddle);
        ImGui.End();

        var draw = ImGui.GetForegroundDrawList();

        draw.AddRectFilled(SysVec2.Zero, display, ImGui.GetColorU32(new SysVec4(0.03f, 0.03f, 0.04f, 0.6f)));

        SysVec2 cardSize = new(380 * s, (determinate ? 142 : 104) * s);
        SysVec2 cardPos = new((display.X - cardSize.X) * 0.5f, (display.Y - cardSize.Y) * 0.5f);
        uint cardBg = ImGui.GetColorU32(new SysVec4(0.10f, 0.10f, 0.12f, 1f));
        uint cardBorder = ImGui.GetColorU32(new SysVec4(1f, 1f, 1f, 0.06f));
        EditorWidgets.DropShadow(draw, cardPos, cardPos + cardSize, 10 * s, s);
        draw.AddRectFilled(cardPos, cardPos + cardSize, cardBg, 10 * s);
        draw.AddRect(cardPos, cardPos + cardSize, cardBorder, 10 * s, ImDrawFlags.None, 1f);

        float pad = 20 * s;

        var ellipsis = new string('.', 1 + (int)(dots / 0.5f) % 3);
        var statusText = unityImport ? UnityImportWindow.BusyStatus
            : buildingPlayer ? BuildProgress.Status
            : SceneCommands.IsLoading ? SceneCommands.LoadingStatus
            : AsyncAssetImport.Status;
        float textW = cardSize.X - pad * 2;
        var status = Truncate($"{statusText.TrimEnd('.')}{ellipsis}", textW);
        draw.AddText(cardPos + new SysVec2(pad, pad),
            ImGui.GetColorU32(new SysVec4(0.92f, 0.92f, 0.95f, 1f)), status);

        var file = AsyncAssetImport.CurrentFile;
        var sub = unityImport
            ? "Extracting and converting the Unity package..."
            : buildingPlayer
            ? (string.IsNullOrEmpty(BuildProgress.Detail) ? "Producing a standalone player..." : BuildProgress.Detail)
            : SceneCommands.IsLoading
                ? SceneCommands.LoadingDetail
                : string.IsNullOrEmpty(file) ? "The editor stays responsive while importing." : file;
        draw.AddText(cardPos + new SysVec2(pad, pad + 22 * s),
            ImGui.GetColorU32(new SysVec4(0.6f, 0.6f, 0.64f, 1f)), Truncate(sub, textW));

        float barH = 8 * s;
        SysVec2 barMin = cardPos + new SysVec2(pad, cardSize.Y - pad - barH);
        SysVec2 barMax = barMin + new SysVec2(cardSize.X - pad * 2, barH);
        draw.AddRectFilled(barMin, barMax, ImGui.GetColorU32(new SysVec4(0.07f, 0.07f, 0.08f, 1f)), barH * 0.5f);

        float barW = barMax.X - barMin.X;
        uint barFill = ImGui.GetColorU32(new SysVec4(0.26f, 0.55f, 0.95f, 1f));
        if (determinate || importDeterminate) {
            float progress = unityImport ? UnityImportWindow.BusyFraction
                : buildingPlayer ? BuildProgress.Fraction
                : AsyncAssetImport.Fraction;
            float fill = Math.Clamp(progress, 0f, 1f) * barW;
            if (fill > 1f)
                draw.AddRectFilled(barMin, new SysVec2(barMin.X + fill, barMax.Y), barFill, barH * 0.5f);
        }
        else {
            float segW = barW * 0.32f;
            float eased = 0.5f - 0.5f * MathF.Cos(anim * MathF.Tau);
            float x0 = barMin.X + (barW - segW) * eased;
            draw.AddRectFilled(new SysVec2(x0, barMin.Y), new SysVec2(x0 + segW, barMax.Y),
                barFill, barH * 0.5f);
        }
    }

    public static void DrawBakeBadge(float s) { }

    static string Truncate(string text, float maxWidth) {
        if (string.IsNullOrEmpty(text) || ImGui.CalcTextSize(text).X <= maxWidth)
            return text;
        bool isPath = text.Contains('/') || text.Contains('\\');
        if (isPath) {
            while (text.Length > 4 && ImGui.CalcTextSize("..." + text).X > maxWidth)
                text = text[1..];
            return "..." + text;
        }
        while (text.Length > 4 && ImGui.CalcTextSize(text + "...").X > maxWidth)
            text = text[..^1];
        return text + "...";
    }
}
