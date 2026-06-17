using System.Collections.Generic;
using BallisticEngine;   // RenderFeatureManager, RenderFeature

namespace BallisticEngine.DX12;

// PHASE-3 (chunk 20) — the ONE bridge that crosses the engine→backend seam for render features, the exact mirror
// of VolumePostProcessing.Apply (the Volume framework's single engine→backend bridge). Per frame:
//   1. RenderFeatureManager.Gather() — the engine collects the active authored features in authored order (no
//      blend); 0 = the layer is inert this frame (no RenderFeatures SceneBehaviour, or all features inactive).
//   2. If the active set is UNCHANGED since last frame (same instances, same order) — NO-OP: the graph already
//      has the right adapters. This is the steady state (incl. the feature-free golden scenes → 0 every frame →
//      the graph stays exactly the built-in set → byte-identical to golden).
//   3. If it CHANGED — rebuild one Dx12FeaturePassAdapter per active feature and graph.SetFeaturePasses them (which
//      re-Builds + re-Compiles). A feature added to a scene later thus joins the graph on the first frame it's
//      active; removed → it leaves and the graph returns to its prior shape.
//
// The compare is by feature-instance reference + order (captures add / remove / reorder / enable-toggle, since
// Gather only returns Active features). A param-only edit (Tint value) does NOT change the set → no rebuild
// needed (the adapter reads the live feature params each Record). Called from DX12HDRenderer once per frame AFTER
// the volume bridge and BEFORE graph.Execute — gated so it's a true no-op for feature-free scenes.
public sealed class Dx12RenderFeatureBridge {
    readonly Dx12RenderGraph graph;
    readonly Dx12FeaturePassRecorder recorder;
    readonly List<RenderFeature> lastApplied = new();   // the feature set currently mounted in the graph

    public Dx12RenderFeatureBridge(Dx12RenderGraph graph, Dx12FeaturePassRecorder recorder) {
        this.graph = graph;
        this.recorder = recorder;
    }

    // Gather the active features and, only when the set changed, rebuild the graph's feature-pass segment.
    public void Apply() {
        int count = RenderFeatureManager.Gather();
        IReadOnlyList<RenderFeature> active = RenderFeatureManager.Active;

        if (SameAsLast(active, count)) return;   // steady state (incl. the feature-free 0==0 case)

        var adapters = new List<IRenderPass>(count);
        for (int i = 0; i < count; i++)
            adapters.Add(new Dx12FeaturePassAdapter(active[i], recorder, i));
        graph.SetFeaturePasses(adapters);

        // Snapshot the applied set for next frame's compare.
        lastApplied.Clear();
        for (int i = 0; i < count; i++) lastApplied.Add(active[i]);
    }

    bool SameAsLast(IReadOnlyList<RenderFeature> active, int count) {
        if (count != lastApplied.Count) return false;
        for (int i = 0; i < count; i++)
            if (!ReferenceEquals(active[i], lastApplied[i])) return false;
        return true;
    }
}
