using Hexa.NET.ImGui;
using SysVec2 = System.Numerics.Vector2;
using SysVec4 = System.Numerics.Vector4;

namespace BallisticEngine.Editor;

// Unity-style "busy" indicator: dims the whole window and shows a centered card with an animated
// indeterminate bar and status text while a background asset import runs. The editor keeps
// repainting (so the window never goes "Not Responding"), but input is gated off elsewhere so the
// user can't edit mid-import. Draw() is a no-op when nothing is busy.
//
// Drawn on the FOREGROUND draw list so it sits above every panel unconditionally â€” a windowed
// overlay would fight the panels' NoBringToFrontOnFocus ordering and could render behind them.
internal static class BusyOverlay {
    static float anim;          // 0..1 sweep position for the indeterminate bar
    static float dots;          // accumulates time for the animated "..." ellipsis

    public static void Draw(float s) {
        // Shown for background asset imports, the one-frame deferred scene open, and standalone player
        // builds. NOTE: the light-probe / reflection bake is DELIBERATELY NOT here anymore — it runs
        // non-blocking now (sky-primed so the scene is lit from frame 1, time-sliced, progressively
        // uploaded so it refines live), so it must NOT throw up a modal block. Changing probe density
        // re-fits + rebakes silently in the background while you keep editing — the "don't make me wait
        // for the bake" requirement. A small unobtrusive bake status is shown separately (DrawBakeBadge).
        var buildingPlayer = BuildProgress.IsBuilding;
        var unityImport = UnityImportWindow.IsBusy;
        var busy = AsyncAssetImport.IsBusy || SceneCommands.IsLoading || buildingPlayer || unityImport;
        if (!busy)
            return;

        // The build shows a determinate bar (and a taller card); an asset import is determinate once
        // the import stage has reported its job count (Fraction >= 0). The Unity package extract/convert
        // reports its own determinate fraction.
        var importDeterminate = AsyncAssetImport.IsBusy && AsyncAssetImport.Fraction >= 0f;
        var determinate = buildingPlayer || unityImport;

        ImGuiIOPtr io = ImGui.GetIO();
        SysVec2 display = io.DisplaySize;
        float dt = io.DeltaTime > 0 ? io.DeltaTime : 1f / 60f;
        anim = (anim + dt * 0.9f) % 1f;
        dots = (dots + dt) % 1.5f;

        // Block ALL input to the panels behind the overlay — a true modal. A full-window window that is
        // forced TO THE FRONT every frame (SetNextWindowFocus + no NoBringToFrontOnFocus, which was the
        // bug — it kept the blocker BEHIND the panels so clicks fell through). It must be drawn AFTER
        // every panel this frame (BusyOverlay.Draw is the last UI call) so it ends up on top. An
        // invisible button over the whole area eats clicks; capturing keyboard focus stops typing too.
        ImGui.SetNextWindowPos(SysVec2.Zero);
        ImGui.SetNextWindowSize(display);
        ImGui.SetNextWindowBgAlpha(0f);
        ImGui.SetNextWindowFocus();
        const ImGuiWindowFlags blockFlags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoDocking |
            ImGuiWindowFlags.NoNav;
        ImGui.Begin("##busyblocker", blockFlags);
        // A button (not InvisibleButton) so hovering it sets WantCaptureMouse, which is what actually
        // stops the click reaching a panel; it fills the window and never does anything on click.
        ImGui.SetCursorPos(SysVec2.Zero);
        ImGui.InvisibleButton("##busyeat", display, ImGuiButtonFlags.MouseButtonLeft |
            ImGuiButtonFlags.MouseButtonRight | ImGuiButtonFlags.MouseButtonMiddle);
        ImGui.End();

        var draw = ImGui.GetForegroundDrawList();

        // Full-window dim layer.
        draw.AddRectFilled(SysVec2.Zero, display, ImGui.GetColorU32(new SysVec4(0.03f, 0.03f, 0.04f, 0.6f)));

        // Centered card (taller while baking: it hosts a Cancel button; builds use the same height).
        SysVec2 cardSize = new(380 * s, (determinate ? 142 : 104) * s);
        SysVec2 cardPos = new((display.X - cardSize.X) * 0.5f, (display.Y - cardSize.Y) * 0.5f);
        uint cardBg = ImGui.GetColorU32(new SysVec4(0.10f, 0.10f, 0.12f, 1f));
        uint cardBorder = ImGui.GetColorU32(new SysVec4(1f, 1f, 1f, 0.06f));
        EditorWidgets.DropShadow(draw, cardPos, cardPos + cardSize, 10 * s, s);
        draw.AddRectFilled(cardPos, cardPos + cardSize, cardBg, 10 * s);
        draw.AddRect(cardPos, cardPos + cardSize, cardBorder, 10 * s, ImDrawFlags.None, 1f);

        float pad = 20 * s;

        // Title + animated ellipsis.
        var ellipsis = new string('.', 1 + (int)(dots / 0.5f) % 3);
        var statusText = unityImport ? UnityImportWindow.BusyStatus
            : buildingPlayer ? BuildProgress.Status
            : SceneCommands.IsLoading ? SceneCommands.LoadingStatus
            : AsyncAssetImport.Status;
        float textW = cardSize.X - pad * 2;
        var status = Truncate($"{statusText.TrimEnd('.')}{ellipsis}", textW);
        draw.AddText(cardPos + new SysVec2(pad, pad),
            ImGui.GetColorU32(new SysVec4(0.92f, 0.92f, 0.95f, 1f)), status);

        // Subtext: the build step, the scene-load stage, the file being imported, or a reassuring note.
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
            // Determinate: the bake / build / import knows roughly how far along it is.
            float progress = unityImport ? UnityImportWindow.BusyFraction
                : buildingPlayer ? BuildProgress.Fraction
                : AsyncAssetImport.Fraction;
            float fill = Math.Clamp(progress, 0f, 1f) * barW;
            if (fill > 1f)
                draw.AddRectFilled(barMin, new SysVec2(barMin.X + fill, barMax.Y), barFill, barH * 0.5f);
        }
        else {
            // Indeterminate sweeping bar.
            float segW = barW * 0.32f;
            float eased = 0.5f - 0.5f * MathF.Cos(anim * MathF.Tau); // slows at the ends
            float x0 = barMin.X + (barW - segW) * eased;
            draw.AddRectFilled(new SysVec2(x0, barMin.Y), new SysVec2(x0 + segW, barMax.Y),
                barFill, barH * 0.5f);
        }
    }

    // Non-blocking bake indicator: a small pill in the bottom-right corner with a thin progress bar,
    // shown WHILE the light-probe bake runs (which no longer blocks the UI). The user keeps editing;
    // this just tells them GI is refining in the background. Drawn on the foreground list, no input eat.
    public static void DrawBakeBadge(float s) {
        if (!IrradianceVolume.IsBaking)
            return;
        var draw = ImGui.GetForegroundDrawList();
        SysVec2 display = ImGui.GetIO().DisplaySize;
        float w = 190 * s, h = 30 * s, margin = 14 * s;
        SysVec2 pos = new(display.X - w - margin, display.Y - h - margin);
        draw.AddRectFilled(pos, pos + new SysVec2(w, h),
            ImGui.GetColorU32(new SysVec4(0.10f, 0.10f, 0.12f, 0.92f)), 6 * s);
        draw.AddRect(pos, pos + new SysVec2(w, h),
            ImGui.GetColorU32(new SysVec4(1f, 1f, 1f, 0.08f)), 6 * s);
        float prog = Math.Clamp(IrradianceVolume.BakeProgress, 0f, 1f);
        var label = $"Baking GI  {(int)(prog * 100)}%";
        draw.AddText(pos + new SysVec2(10 * s, 5 * s),
            ImGui.GetColorU32(new SysVec4(0.85f, 0.88f, 0.95f, 1f)), label);
        // Thin progress bar along the bottom edge of the pill.
        SysVec2 bMin = pos + new SysVec2(10 * s, h - 7 * s);
        float bW = w - 20 * s;
        draw.AddRectFilled(bMin, bMin + new SysVec2(bW, 3 * s),
            ImGui.GetColorU32(new SysVec4(0.07f, 0.07f, 0.08f, 1f)), 1.5f * s);
        draw.AddRectFilled(bMin, bMin + new SysVec2(bW * prog, 3 * s),
            ImGui.GetColorU32(new SysVec4(0.26f, 0.55f, 0.95f, 1f)), 1.5f * s);
    }

    // Shortens text to fit maxWidth, appending an ellipsis. A long path is trimmed from the FRONT
    // (the file name at the end is the useful part); plain status text from the back.
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
