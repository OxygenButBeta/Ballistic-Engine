using BallisticEngine.DX12;

namespace BallisticEngine.Editor;

[EditorWindowMeta("Render Pass Debug", "Window/Render Pass Debug", order: 26, Width = 340, Height = 560)]
internal sealed class RenderPassTogglesWindow : EditorWindow {
    public RenderPassTogglesWindow() {
        NoCollapse = true;
    }

    static DX12HDRenderer Renderer =>
        RenderAsset.Current?.Renderer as DX12HDRenderer;

    protected override void OnGui(IEditorGui gui) {
        DX12HDRenderer r = Renderer;
        if (r is null) {
            gui.TextWrapped("No active DX12 renderer. (Toggles apply to the live DX12 backend.)");
            return;
        }

        var pfx = r.PostFX;
        Dx12RenderDoors d = r.Doors;

        if (gui.Button("All On")) SetAll(r, pfx, true);
        gui.SameLine();
        if (gui.Button("Minimal Base")) SetAll(r, pfx, false);
        if (d.Minimal) {
            gui.SameLine();
            gui.TextDisabled("(launched MINIMAL=1)");
        }
        gui.Spacing();

        if (gui.CollapsingHeader("Lighting / Ambient", defaultOpen: true)) {
            DoorRow(gui, r, "IBL (irradiance / prefilter ambient)", "Ibl", d.Ibl);
            DoorRow(gui, r, "SSAO (GTAO)", "Ssao", d.Ssao);
            if (d.Ssao) {
                gui.Indent();
                PostBool(gui, "  SSAO enabled (volume flag)", () => pfx.SSAOEnabled, v => pfx.SSAOEnabled = v);
                gui.Unindent();
            }
        }

        if (gui.CollapsingHeader("Shadows / GI / Reflections", defaultOpen: true)) {
            DoorRow(gui, r, "Shadows (sun cascades)", "Shadows", d.Shadows);

            PostBool(gui, "Global Illumination (Aurora)", () => pfx.AuroraEnabled, v => pfx.AuroraEnabled = v);
            if (pfx.AuroraEnabled) {
                gui.Indent();
                PostBool(gui, "  Multi-bounce", () => pfx.AuroraMultiBounce, v => pfx.AuroraMultiBounce = v);
                PostBool(gui, "  GI-driven reflections", () => pfx.AuroraReflections, v => pfx.AuroraReflections = v);
                gui.Unindent();
            }

            bool refl = pfx.SsrEnabled && pfx.ReflectionMode != ReflectionMode.Off;
            if (gui.Checkbox("Reflections (SSR / RT)", ref refl)) {
                pfx.SsrEnabled = refl;
                pfx.ReflectionMode = refl
                    ? (pfx.ReflectionMode == ReflectionMode.Off ? ReflectionMode.ScreenSpace : pfx.ReflectionMode)
                    : ReflectionMode.Off;
            }
            if (refl) {
                gui.Indent();
                int mode = (int)pfx.ReflectionMode;
                gui.SetNextItemWidth(-1);
                if (gui.Combo("  Mode", ref mode, new[] { "Screen-Space", "Ray-Traced", "Off" }))
                    pfx.ReflectionMode = (ReflectionMode)mode;
                gui.Unindent();
            }
        }

        if (gui.CollapsingHeader("Atmosphere / Sky / Fog", defaultOpen: true)) {
            DoorRow(gui, r, "Sky (procedural / skybox)", "Sky", d.Sky);

            DoorRow(gui, r, "Aerial Perspective (pass door)", "AerialPersp", d.AerialPersp);
            if (d.AerialPersp) {
                gui.Indent();
                PostBool(gui, "  AP enabled (volume flag)",
                    () => pfx.AerialPerspectiveEnabled, v => pfx.AerialPerspectiveEnabled = v);
                gui.Unindent();
            }

            PostBool(gui, "Volumetric Fog", () => pfx.VolumetricEnabled, v => pfx.VolumetricEnabled = v);
            DoorRow(gui, r, "  God-ray shafts (force)", "Shafts", d.Shafts);
            DoorRow(gui, r, "  Dust motes (force)", "Dust", d.Dust);
        }

        if (gui.CollapsingHeader("Post-Processing", defaultOpen: true)) {
            DoorRow(gui, r, "Bloom", "Bloom", d.Bloom);
            PostBool(gui, "TAA (temporal AA)", () => pfx.TaaEnabled, v => pfx.TaaEnabled = v);
            DoorRow(gui, r, "Volume -> PostFX bridge", "Volumes", d.Volumes);
            if (!d.Volumes)
                gui.TextDisabled("  (bridge off: volume profiles won't drive PostFX)");
        }
    }

    static void DoorRow(IEditorGui gui, DX12HDRenderer r, string label, string door, bool value) {
        bool v = value;
        if (gui.Checkbox(label, ref v)) r.SetDoor(door, v);
    }

    static void PostBool(IEditorGui gui, string label, System.Func<bool> get, System.Action<bool> set) {
        bool v = get();
        if (gui.Checkbox(label, ref v)) set(v);
    }

    static void SetAll(DX12HDRenderer r, PostProcessSettings pfx, bool on) {
        r.SetDoor("Shadows", on);
        r.SetDoor("Ibl", on);
        r.SetDoor("Sky", on);
        r.SetDoor("Ssao", on);
        r.SetDoor("Bloom", on);
        r.SetDoor("AerialPersp", on);
        r.SetDoor("Volumes", on);
        r.SetDoor("Shafts", on);
        r.SetDoor("Dust", on);
        pfx.SSAOEnabled = on;
        pfx.AuroraEnabled = on;
        pfx.TaaEnabled = on;
        pfx.VolumetricEnabled = on;
        pfx.AerialPerspectiveEnabled = on;
        pfx.SsrEnabled = on;
        pfx.ReflectionMode = on
            ? (pfx.ReflectionMode == ReflectionMode.Off ? ReflectionMode.ScreenSpace : pfx.ReflectionMode)
            : ReflectionMode.Off;
    }
}
