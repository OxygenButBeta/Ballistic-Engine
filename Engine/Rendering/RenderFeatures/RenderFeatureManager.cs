namespace BallisticEngine;

// Gathers the active authored RenderFeatures in authored order each frame (the engine's mirror of
// VolumeManager — design §2a). UNLIKE VolumeManager there is NO BLEND: a feature is on or off, so the
// manager just produces the ORDERED ACTIVE SET the backend bridge consumes (chunk 20 turns each into an
// IRenderPass and graph.Add's it). Early-outs on empty exactly like VolumeManager.Update early-outs on
// `volumes.Count == 0`, so a scene with no RenderFeatures is byte-identical to today (the pixel-neutral
// default).
//
// The list is read from the scene-wide RenderFeatures.Active (the SceneBehaviour holding the ordered
// list) — the same "renderer reads a SceneBehaviour's static Active per frame" pattern as Skybox.Active.
public static class RenderFeatureManager {
    // Reused scratch so the per-frame gather allocates nothing in steady state.
    static readonly List<RenderFeature> active = new();

    // The active authored features in authored order, refreshed by the most recent Gather(). Empty when
    // no RenderFeatures behaviour exists or all its features are inactive. The backend reads this.
    public static IReadOnlyList<RenderFeature> Active => active;

    // CHUNK-20 PROOF DOOR (BALLISTIC_DX12_FEATURE_TINT_TEST): inject ONE SceneColorTintFeature when no scene
    // authors a RenderFeatures host yet (serialization is chunk 21). Default OFF → completely inert, so the
    // golden scenes are byte-identical; set to render the seam's positive test (the feature visibly tints the
    // frame) WITHOUT depending on YAML round-trip. Env read is cached (cheap; never on the production path).
    // The Tint is a strong magenta so the change is unmistakable in the positive capture.
    static RenderFeature testFeature;
    static int testDoor = -1;   // -1 unread, 0 off, 1 on
    static RenderFeature TestFeatureOrNull() {
        if (testDoor < 0)
            testDoor = System.Environment.GetEnvironmentVariable("BALLISTIC_DX12_FEATURE_TINT_TEST") == "1" ? 1 : 0;
        if (testDoor == 0) return null;
        return testFeature ??= new SceneColorTintFeature {
            Tint = new System.Numerics.Vector3(1f, 0.25f, 0.6f),   // strong magenta — unmistakable in the capture
            Strength = 1f,
        };
    }

    // Collect the active features in authored order for this frame. Returns the count (0 = the layer is
    // inert this frame — the backend skips the bridge entirely). Mirrors VolumeManager.Update's shape:
    // reset the working set, early-out on empty, then fill in order.
    public static int Gather() {
        active.Clear();

        RenderFeatures host = RenderFeatures.Active;
        if (host is null || !host.IsActive || host.Features is null || host.Features.Count == 0) {
            // No authored host — the chunk-20 proof door can still inject one tint feature (default off → 0).
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

    // Drops the gathered set so a script reload doesn't pin unloaded-assembly RenderFeature instances —
    // mirrors VolumeManager.ResetStack (called from EngineBootstrap.ReloadGameScripts before the ALC
    // unload). Script-authored feature types live in the collectible ALC; clearing here lets the next
    // assembly's types re-populate cleanly. RenderFeatures.Active is reset by the scene Clear/re-attach.
    internal static void Reset() => active.Clear();
}
