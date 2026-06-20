using System.Numerics;
using BallisticEngine.DX12;

namespace BallisticEngine.Editor;

// A live "Render Pass Toggles" debug window: flip the DX12 renderer's per-pass on/off without restarting
// with BALLISTIC_DX12_* env vars. Two kinds of toggle, exactly mirroring how each pass is gated in the engine:
//   - DOOR-gated passes (Shadows/IBL/Sky/SSAO/Bloom/AerialPerspective/Volume-bridge) → renderer.SetDoor(...);
//     the renderer copies its Doors struct into the next frame's context, so the change takes effect next frame.
//   - POSTFX/Volume-gated passes (Fog/SSR) → flip the renderer's live PostFX flags (the same object the
//     Volume framework writes every frame). These were already live; this window just gives them a checkbox too.
// Diagnostic only — nothing here is serialized; it resets to the env-resolved doors on relaunch.
//
// Phase-6 EditorWindow: was a fully-static singleton (static open/Toggle/Draw + a hand-wired [MenuItem] +
// two Draw() call sites). Now it's an ordinary EditorWindow discovered through [EditorWindowMeta] — the
// SAME path a user-authored window takes — so the menu entry, the toggle, and the draw are all automatic
// (UserEditorWindowRegistry). A built-in window eating its own dogfood.
[EditorWindowMeta("Render Pass Toggles", "Window/Render Pass Toggles", order: 26, Width = 300, Height = 360)]
internal sealed class RenderPassTogglesWindow : EditorWindow {
    public RenderPassTogglesWindow() {
        NoCollapse = true;   // identity/title/size come from the attribute via ConfigureFromMeta
    }

    // The live DX12 renderer, or null when the active backend isn't DX12 (then the window shows a notice).
    static DX12HDRenderer Renderer =>
        RenderAsset.Current?.Renderer as DX12HDRenderer;

    protected override void OnGui(IEditorGui gui) {
        DX12HDRenderer r = Renderer;
        if (r is null) {
            gui.TextWrapped("No active DX12 renderer. (Toggles apply to the live DX12 backend.)");
            return;
        }

        gui.TextDisabled("Door-gated passes (live)");
        gui.Separator();
        Dx12RenderDoors d = r.Doors;
        DoorRow(gui, r, "Shadows", "Shadows", d.Shadows);
        DoorRow(gui, r, "IBL (irradiance / prefilter ambient)", "Ibl", d.Ibl);
        DoorRow(gui, r, "Sky", "Sky", d.Sky);
        DoorRow(gui, r, "SSAO", "Ssao", d.Ssao);
        DoorRow(gui, r, "Bloom", "Bloom", d.Bloom);
        DoorRow(gui, r, "Aerial Perspective (haze)", "AerialPersp", d.AerialPersp);
        DoorRow(gui, r, "Volume -> PostFX bridge", "Volumes", d.Volumes);

        gui.Spacing();
        gui.TextDisabled("PostFX / volume-gated passes (live)");
        gui.Separator();
        var pfx = r.PostFX;
        bool fog = pfx.VolumetricEnabled;
        if (gui.Checkbox("Volumetric Fog", ref fog)) pfx.VolumetricEnabled = fog;
        bool ssr = pfx.SsrEnabled;
        if (gui.Checkbox("SSR / Reflections", ref ssr)) {
            pfx.SsrEnabled = ssr;
            pfx.ReflectionMode = ssr && pfx.ReflectionMode == ReflectionMode.Off ? ReflectionMode.ScreenSpace
                               : ssr ? pfx.ReflectionMode : ReflectionMode.Off;
        }

        if (d.Minimal) {
            gui.Spacing();
            gui.TextDisabled("(launched with BALLISTIC_DX12_MINIMAL=1)");
        }
    }

    static void DoorRow(IEditorGui gui, DX12HDRenderer r, string label, string door, bool value) {
        bool v = value;
        if (gui.Checkbox(label, ref v)) r.SetDoor(door, v);
    }
}
