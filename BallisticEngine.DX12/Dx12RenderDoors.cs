using System;

namespace BallisticEngine.DX12;

// Cached per-feature on/off "doors" for the DX12 renderer, resolved ONCE at renderer init from the
// BALLISTIC_DX12_* / BALLISTIC_FX_* environment variables. Two purposes:
//
//   1. THE "BARE MINIMUM" DIAGNOSTIC SWITCH. BALLISTIC_DX12_MINIMAL=1 strips the frame to the minimal
//      correct base — G-buffer + deferred sun/punctual + composite — and forces every other pass OFF
//      (shadows, IBL, sky, SSAO, SSR, SSGI/GI/DDGI, fog, aerial perspective, bloom, TAA, FSR, the
//      volume→PostFX bridge). Features are then re-enabled ONE AT A TIME via a per-stage override door
//      (e.g. BALLISTIC_DX12_MINIMAL=1 BALLISTIC_DX12_SHADOWS=1) so any reappearance of "ugly" is pinned
//      to exactly one pass. GPU-driven geometry + Hi-Z are NOT stripped — they're byte-identical and
//      the minimal frame must also be the FAST baseline.
//
//   2. KILLS the ~per-frame Environment.GetEnvironmentVariable churn (each is a process-env hashtable
//      lookup + string alloc; the renderer read ~47 of them EVERY frame). Reading them once into typed
//      fields honours the project's "no string/reflection churn in the render hot path" rule.
//
// Semantics: each door is a tri-state collapse. When MINIMAL is OFF the default matches the historical
// behaviour exactly (a feature that was "on unless ENV==0" stays so) → with MINIMAL unset and no env
// set, the doors are byte-identical to the pre-refactor scattered reads. When MINIMAL is ON, a feature
// is OFF unless its own door is explicitly forced ON (ENV==1).
public readonly struct Dx12RenderDoors {
    public readonly bool Minimal;

    // Per-pass enables (already collapsed to the final on/off the gate sites want).
    public readonly bool Shadows;       // sun cascade shadows (+ the deferred shadow term)
    public readonly bool Ibl;           // IBL bake (irradiance/prefilter from the sky) + the UseIBL ambient path
    public readonly bool Sky;           // sky draw (procedural sky / skybox cubemap) into the HDR background
    public readonly bool Ssao;          // screen-space AO
    public readonly bool Bloom;         // bloom
    public readonly bool AerialPersp;   // aerial-perspective haze
    public readonly bool Fog;           // volumetric fog
    public readonly bool Volumes;       // the Volume framework → PostFX bridge (VolumeManager.Update + Apply)

    public Dx12RenderDoors(bool minimal, bool shadows, bool ibl, bool sky, bool ssao, bool bloom,
                           bool aerialPersp, bool fog, bool volumes) {
        Minimal = minimal; Shadows = shadows; Ibl = ibl; Sky = sky; Ssao = ssao; Bloom = bloom;
        AerialPersp = aerialPersp; Fog = fog; Volumes = volumes;
    }

    // Return a copy with ONE door flipped (the struct is readonly → rebuild by value). Used by the editor's
    // live "Render Pass Toggles" window to flip a door-gated pass at runtime: the renderer's `Doors` field is
    // reassigned to the returned value and copied into the next frame's Dx12FrameContext — no env round-trip,
    // no per-frame cost. `door` is the case-insensitive field name (Shadows/Ibl/Sky/Ssao/Bloom/AerialPersp/
    // Fog/Volumes); Minimal is intentionally not flippable here (it's a launch-time diagnostic switch).
    public Dx12RenderDoors With(string door, bool value) => door.ToLowerInvariant() switch {
        "shadows"     => new(Minimal, value, Ibl, Sky, Ssao, Bloom, AerialPersp, Fog, Volumes),
        "ibl"         => new(Minimal, Shadows, value, Sky, Ssao, Bloom, AerialPersp, Fog, Volumes),
        "sky"         => new(Minimal, Shadows, Ibl, value, Ssao, Bloom, AerialPersp, Fog, Volumes),
        "ssao"        => new(Minimal, Shadows, Ibl, Sky, value, Bloom, AerialPersp, Fog, Volumes),
        "bloom"       => new(Minimal, Shadows, Ibl, Sky, Ssao, value, AerialPersp, Fog, Volumes),
        "aerialpersp" => new(Minimal, Shadows, Ibl, Sky, Ssao, Bloom, value, Fog, Volumes),
        "fog"         => new(Minimal, Shadows, Ibl, Sky, Ssao, Bloom, AerialPersp, value, Volumes),
        "volumes"     => new(Minimal, Shadows, Ibl, Sky, Ssao, Bloom, AerialPersp, Fog, value),
        _ => this,
    };

    static string? Env(string name) => Environment.GetEnvironmentVariable(name);

    // Resolve a feature that is "ON unless ENV==0" in normal mode, and "OFF unless ENV==1" under MINIMAL.
    static bool DoorDefaultOn(bool minimal, string env) {
        string? v = Env(env);
        return minimal ? v == "1" : v != "0";
    }

    public static Dx12RenderDoors Resolve() {
        bool minimal = Env("BALLISTIC_DX12_MINIMAL") == "1";
        return new Dx12RenderDoors(
            minimal:     minimal,
            // Shadows have no dedicated env door historically (always on when a sun exists). Add one
            // (BALLISTIC_DX12_SHADOWS) so the stage harness can force them back on under MINIMAL; absent
            // MINIMAL the door is "on unless ==0" → unchanged default.
            shadows:     DoorDefaultOn(minimal, "BALLISTIC_DX12_SHADOWS"),
            // IBL + sky were gated purely by ProceduralSky/Skybox presence (no env door). Under MINIMAL
            // they're forced off unless explicitly re-enabled; absent MINIMAL they stay "on" (the scene
            // presence check at the gate site still applies — these doors only ADD a master-off).
            ibl:         DoorDefaultOn(minimal, "BALLISTIC_DX12_IBL"),
            sky:         DoorDefaultOn(minimal, "BALLISTIC_DX12_SKY"),
            ssao:        DoorDefaultOn(minimal, "BALLISTIC_DX12_SSAO"),
            bloom:       DoorDefaultOn(minimal, "BALLISTIC_DX12_BLOOM"),
            aerialPersp: DoorDefaultOn(minimal, "BALLISTIC_DX12_AP"),
            // Fog historically was OFF unless BALLISTIC_FX_VOLUMETRIC==1 (or a volume enabled it). Keep that
            // exactly: the door only carries the env-force; the PostFX.VolumetricEnabled check stays at the site.
            fog:         Env("BALLISTIC_FX_VOLUMETRIC") == "1",
            volumes:     DoorDefaultOn(minimal, "BALLISTIC_DX12_VOLUMES"));
    }
}
