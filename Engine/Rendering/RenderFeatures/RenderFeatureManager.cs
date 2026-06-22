namespace BallisticEngine;

public static class RenderFeatureManager {
    static readonly List<RenderFeature> active = new();

    public static IReadOnlyList<RenderFeature> Active => active;

    static RenderFeature testFeature;
    static int testDoor = -1;

    static RenderFeature TestFeatureOrNull() {
        if (testDoor < 0)
            testDoor = System.Environment.GetEnvironmentVariable("BALLISTIC_DX12_FEATURE_TINT_TEST") == "1" ? 1 : 0;
        if (testDoor == 0) return null;
        return testFeature ??= new SceneColorTintFeature {
            Tint = new System.Numerics.Vector3(1f, 0.25f, 0.6f), Strength = 1f,
        };
    }

    public static int Gather() {
        active.Clear();

        RenderFeatures host = RenderFeatures.Active;
        if (host is null || !host.IsActive || host.Features is null || host.Features.Count == 0) {
            RenderFeature test = TestFeatureOrNull();
            if (test is { Active: true }) { active.Add(test); return active.Count; }
            return 0;
        }

        foreach (RenderFeature feature in host.Features) {
            if (feature is { Active: true })
                active.Add(feature);
        }
        return active.Count;
    }

    internal static void Reset() => active.Clear();
}
