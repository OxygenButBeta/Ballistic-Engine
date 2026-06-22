namespace BallisticEngine.DX12;

public readonly struct Dx12RenderDoors {
    public readonly bool Minimal;

    public readonly bool Shadows;
    public readonly bool Ibl;
    public readonly bool Sky;
    public readonly bool Ssao;
    public readonly bool Bloom;
    public readonly bool AerialPersp;
    public readonly bool Fog;
    public readonly bool Volumes;
    public readonly bool Shafts;
    public readonly bool Dust;

    public Dx12RenderDoors(bool minimal, bool shadows, bool ibl, bool sky, bool ssao, bool bloom,
                           bool aerialPersp, bool fog, bool volumes, bool shafts, bool dust) {
        Minimal = minimal; Shadows = shadows; Ibl = ibl; Sky = sky; Ssao = ssao; Bloom = bloom;
        AerialPersp = aerialPersp; Fog = fog; Volumes = volumes; Shafts = shafts; Dust = dust;
    }

    public Dx12RenderDoors With(string door, bool value) => door.ToLowerInvariant() switch {
        "shadows"     => new(Minimal, value, Ibl, Sky, Ssao, Bloom, AerialPersp, Fog, Volumes, Shafts, Dust),
        "ibl"         => new(Minimal, Shadows, value, Sky, Ssao, Bloom, AerialPersp, Fog, Volumes, Shafts, Dust),
        "sky"         => new(Minimal, Shadows, Ibl, value, Ssao, Bloom, AerialPersp, Fog, Volumes, Shafts, Dust),
        "ssao"        => new(Minimal, Shadows, Ibl, Sky, value, Bloom, AerialPersp, Fog, Volumes, Shafts, Dust),
        "bloom"       => new(Minimal, Shadows, Ibl, Sky, Ssao, value, AerialPersp, Fog, Volumes, Shafts, Dust),
        "aerialpersp" => new(Minimal, Shadows, Ibl, Sky, Ssao, Bloom, value, Fog, Volumes, Shafts, Dust),
        "fog"         => new(Minimal, Shadows, Ibl, Sky, Ssao, Bloom, AerialPersp, value, Volumes, Shafts, Dust),
        "volumes"     => new(Minimal, Shadows, Ibl, Sky, Ssao, Bloom, AerialPersp, Fog, value, Shafts, Dust),
        "shafts"      => new(Minimal, Shadows, Ibl, Sky, Ssao, Bloom, AerialPersp, Fog, Volumes, value, Dust),
        "dust"        => new(Minimal, Shadows, Ibl, Sky, Ssao, Bloom, AerialPersp, Fog, Volumes, Shafts, value),
        _ => this,
    };

    static string? Env(string name) => Environment.GetEnvironmentVariable(name);

    static bool DoorDefaultOn(bool minimal, string env) {
        string? v = Env(env);
        return minimal ? v == "1" : v != "0";
    }

    public static Dx12RenderDoors Resolve() {
        bool minimal = Env("BALLISTIC_DX12_MINIMAL") == "1";
        return new Dx12RenderDoors(
            minimal:     minimal, shadows:     DoorDefaultOn(minimal, "BALLISTIC_DX12_SHADOWS"), ibl:         DoorDefaultOn(minimal, "BALLISTIC_DX12_IBL"),
            sky:         DoorDefaultOn(minimal, "BALLISTIC_DX12_SKY"),
            ssao:        DoorDefaultOn(minimal, "BALLISTIC_DX12_SSAO"),
            bloom:       DoorDefaultOn(minimal, "BALLISTIC_DX12_BLOOM"),
            aerialPersp: DoorDefaultOn(minimal, "BALLISTIC_DX12_AP"), fog:         Env("BALLISTIC_FX_VOLUMETRIC") == "1",
            volumes:     DoorDefaultOn(minimal, "BALLISTIC_DX12_VOLUMES"), shafts:      Env("BALLISTIC_DX12_SHAFTS") == "1",
            dust:        Env("BALLISTIC_DX12_DUST") == "1");
    }
}
