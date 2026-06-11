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
        // Shown for background asset imports, the one-frame deferred scene open, the time-sliced
        // light-probe bake, and standalone player builds (the last two report determinate progress).
        var baking = IrradianceVolume.IsBaking;
        var buildingPlayer = BuildProgress.IsBuilding;
        var busy = AsyncAssetImport.IsBusy || SceneCommands.IsLoading || baking || buildingPlayer;
        if (!busy)
            return;

        // Both the bake and the build show a determinate bar (and a taller card).
        var determinate = baking || buildingPlayer;

        ImGuiIOPtr io = ImGui.GetIO();
        SysVec2 display = io.DisplaySize;
        float dt = io.DeltaTime > 0 ? io.DeltaTime : 1f / 60f;
        anim = (anim + dt * 0.9f) % 1f;
        dots = (dots + dt) % 1.5f;

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
        var statusText = buildingPlayer ? BuildProgress.Status
            : baking ? IrradianceVolume.BakeStatus
            : SceneCommands.IsLoading ? SceneCommands.LoadingStatus
            : AsyncAssetImport.Status;
        var status = $"{statusText.TrimEnd('.')}{ellipsis}";
        draw.AddText(cardPos + new SysVec2(pad, pad),
            ImGui.GetColorU32(new SysVec4(0.92f, 0.92f, 0.95f, 1f)), status);

        // Subtext: the build step, the scene-load stage, the file being imported, or a reassuring note.
        var file = AsyncAssetImport.CurrentFile;
        var sub = buildingPlayer
            ? (string.IsNullOrEmpty(BuildProgress.Detail) ? "Producing a standalone player..." : BuildProgress.Detail)
            : baking
                ? "The scene keeps rendering while probes bake."
                : SceneCommands.IsLoading
                    ? SceneCommands.LoadingDetail
                    : string.IsNullOrEmpty(file) ? "The editor stays responsive while importing." : file;
        draw.AddText(cardPos + new SysVec2(pad, pad + 22 * s),
            ImGui.GetColorU32(new SysVec4(0.6f, 0.6f, 0.64f, 1f)), sub);

        // Cancel button (manual hit-test: the overlay is a foreground draw list, not a window). Sits
        // on its own row BELOW the subtext (right-aligned), above the progress bar — so the long
        // "scene keeps rendering..." note never runs under it.
        if (baking) {
            SysVec2 btnSize = new(86 * s, 24 * s);
            SysVec2 btnPos = new(cardPos.X + cardSize.X - pad - btnSize.X, cardPos.Y + 74 * s);
            SysVec2 mouse = io.MousePos;
            var hovered = mouse.X >= btnPos.X && mouse.X <= btnPos.X + btnSize.X &&
                          mouse.Y >= btnPos.Y && mouse.Y <= btnPos.Y + btnSize.Y;
            uint btnBg = ImGui.GetColorU32(hovered
                ? new SysVec4(0.45f, 0.22f, 0.22f, 1f)
                : new SysVec4(0.24f, 0.24f, 0.27f, 1f));
            draw.AddRectFilled(btnPos, btnPos + btnSize, btnBg, 5 * s);
            draw.AddRect(btnPos, btnPos + btnSize,
                ImGui.GetColorU32(new SysVec4(0.45f, 0.45f, 0.5f, 1f)), 5 * s);
            var label = "Cancel";
            SysVec2 textSize = ImGui.CalcTextSize(label);
            draw.AddText(btnPos + (btnSize - textSize) * 0.5f,
                ImGui.GetColorU32(new SysVec4(0.95f, 0.92f, 0.92f, 1f)), label);
            if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                IrradianceVolume.CancelRequested = true;
        }

        float barH = 8 * s;
        SysVec2 barMin = cardPos + new SysVec2(pad, cardSize.Y - pad - barH);
        SysVec2 barMax = barMin + new SysVec2(cardSize.X - pad * 2, barH);
        draw.AddRectFilled(barMin, barMax, ImGui.GetColorU32(new SysVec4(0.07f, 0.07f, 0.08f, 1f)), barH * 0.5f);

        float barW = barMax.X - barMin.X;
        uint barFill = ImGui.GetColorU32(new SysVec4(0.26f, 0.55f, 0.95f, 1f));
        if (determinate) {
            // Determinate: the bake / build knows roughly how far along it is.
            float progress = buildingPlayer ? BuildProgress.Fraction : IrradianceVolume.BakeProgress;
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
}
