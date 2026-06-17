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

    // Collect the active features in authored order for this frame. Returns the count (0 = the layer is
    // inert this frame — the backend skips the bridge entirely). Mirrors VolumeManager.Update's shape:
    // reset the working set, early-out on empty, then fill in order.
    public static int Gather() {
        active.Clear();

        RenderFeatures host = RenderFeatures.Active;
        if (host is null || !host.IsActive || host.Features is null || host.Features.Count == 0)
            return 0;

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
