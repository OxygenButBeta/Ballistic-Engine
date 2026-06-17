using Hexa.NET.ImGui;
using BallisticEngine.DX12;
using SysVec2 = System.Numerics.Vector2;

namespace BallisticEngine.Editor;

// A live "Render Pass Toggles" debug window: flip the DX12 renderer's per-pass on/off without restarting
// with BALLISTIC_DX12_* env vars. Two kinds of toggle, exactly mirroring how each pass is gated in the engine:
//   - DOOR-gated passes (Shadows/IBL/Sky/SSAO/Bloom/AerialPerspective/Volume-bridge) → renderer.SetDoor(...);
//     the renderer copies its Doors struct into the next frame's context, so the change takes effect next frame.
//   - POSTFX/Volume-gated passes (Fog/SSR/GI) → flip the renderer's live PostFX flags (the same object the
//     Volume framework writes every frame). These were already live; this window just gives them a checkbox too.
// Standalone, fully-static (no field on EditorApplication needed). Self-registers a Window-menu entry in
// EditorMenus. Diagnostic only — nothing here is serialized; it resets to the env-resolved doors on relaunch.
internal static class RenderPassTogglesWindow {
    static bool open;
    public static bool IsOpen => open;
    public static void Toggle() => open = !open;
    public static void Open() => open = true;

    // The live DX12 renderer, or null when the active backend isn't DX12 (then the window shows a notice).
    static DX12HDRenderer Renderer =>
        RenderAsset.Current?.Renderer as DX12HDRenderer;

    public static void Draw(float scale) {
        if (!open) return;

        ImGui.SetNextWindowSize(new SysVec2(300 * scale, 360 * scale), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Render Pass Toggles###RenderPassToggles", ref open, ImGuiWindowFlags.NoCollapse)) {
            ImGui.End();
            return;
        }

        DX12HDRenderer r = Renderer;
        if (r is null) {
            ImGui.TextWrapped("No active DX12 renderer. (Toggles apply to the live DX12 backend.)");
            ImGui.End();
            return;
        }

        ImGui.TextDisabled("Door-gated passes (live)");
        ImGui.Separator();
        Dx12RenderDoors d = r.Doors;
        DoorRow(r, "Shadows", "Shadows", d.Shadows);
        DoorRow(r, "IBL (irradiance / prefilter ambient)", "Ibl", d.Ibl);
        DoorRow(r, "Sky", "Sky", d.Sky);
        DoorRow(r, "SSAO", "Ssao", d.Ssao);
        DoorRow(r, "Bloom", "Bloom", d.Bloom);
        DoorRow(r, "Aerial Perspective (haze)", "AerialPersp", d.AerialPersp);
        DoorRow(r, "Volume -> PostFX bridge", "Volumes", d.Volumes);

        ImGui.Spacing();
        ImGui.TextDisabled("PostFX / volume-gated passes (live)");
        ImGui.Separator();
        var pfx = r.PostFX;
        bool fog = pfx.VolumetricEnabled;
        if (ImGui.Checkbox("Volumetric Fog", ref fog)) pfx.VolumetricEnabled = fog;
        // SSR / RT-reflections AND GI (SSGI / RT-GI / DDGI / screen probes) are both HARD-DISABLED engine-wide
        // (reflections 2026-06-18, GI 2026-06-17): the whole indirect-lighting stack is taken out of the system
        // at the renderer's gates (Dx12ReflectionsPass.Enabled via SsrEnabled / Dx12GiPass.Enabled via giMode),
        // so neither can be re-enabled at runtime. Shown read-only/disabled to make that explicit.
        ImGui.BeginDisabled();
        bool ssr = false;
        ImGui.Checkbox("SSR / Reflections — disabled", ref ssr);
        bool giOn = false;
        ImGui.Checkbox("GI (SSGI / RT) — disabled", ref giOn);
        ImGui.EndDisabled();

        if (d.Minimal) {
            ImGui.Spacing();
            ImGui.TextDisabled("(launched with BALLISTIC_DX12_MINIMAL=1)");
        }

        ImGui.End();
    }

    static void DoorRow(DX12HDRenderer r, string label, string door, bool value) {
        bool v = value;
        if (ImGui.Checkbox(label, ref v)) r.SetDoor(door, v);
    }
}
