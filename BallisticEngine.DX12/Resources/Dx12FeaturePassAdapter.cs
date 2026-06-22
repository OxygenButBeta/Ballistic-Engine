namespace BallisticEngine.DX12;

public sealed class Dx12FeaturePassAdapter : IRenderPass {
    readonly RenderFeature feature;
    readonly Dx12FeaturePassRecorder recorder;
    readonly string name;
    readonly string featureKey;

    public Dx12FeaturePassAdapter(RenderFeature feature, Dx12FeaturePassRecorder recorder, int registrationIndex) {
        this.feature = feature;
        this.recorder = recorder;
        name = feature.GetType().Name;
        featureKey = $"{name}#{registrationIndex}";
    }

    public RenderFeature Feature => feature;

    public Dx12RenderPassEvent Event => (Dx12RenderPassEvent)(int)feature.Event;
    public string Name => name;

    public bool Enabled(Dx12FrameContext ctx) => feature.Active;

    public void Declare(Dx12PassBuilder builder) {
        var io = new Dx12FeatureIOBuilder(builder, featureKey);
        feature.Declare(io);
    }

    public void Record(Dx12FrameContext ctx) {
        recorder.Bind(ctx, feature);
        feature.Record(recorder);
    }
}
