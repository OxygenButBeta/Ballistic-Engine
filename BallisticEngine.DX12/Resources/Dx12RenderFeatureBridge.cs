namespace BallisticEngine.DX12;

public sealed class Dx12RenderFeatureBridge {
    readonly Dx12RenderGraph graph;
    readonly Dx12FeaturePassRecorder recorder;
    readonly List<RenderFeature> lastApplied = new();

    public Dx12RenderFeatureBridge(Dx12RenderGraph graph, Dx12FeaturePassRecorder recorder) {
        this.graph = graph;
        this.recorder = recorder;
    }

    public void Apply() {
        int count = RenderFeatureManager.Gather();
        IReadOnlyList<RenderFeature> active = RenderFeatureManager.Active;

        if (SameAsLast(active, count)) return;

        var adapters = new List<IRenderPass>(count);
        for (int i = 0; i < count; i++)
            adapters.Add(new Dx12FeaturePassAdapter(active[i], recorder, i));
        graph.SetFeaturePasses(adapters);

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
