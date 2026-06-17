using BallisticEngine;   // RenderFeature, IFeatureIOBuilder

namespace BallisticEngine.DX12;

// PHASE-3 (chunk 20) — the IRenderPass the DX12 backend builds on behalf of ONE authored RenderFeature (the
// §3 seam: a game authors a RenderFeature against the engine library only; THIS adapter — backend-side — turns
// it into a graph pass). The bridge (Dx12RenderFeatureBridge) builds one adapter per active feature and
// graph.Add's it, so V1 cull / V2 alias / V3 auto-barriers treat a feature exactly like a built-in.
//
// The four IRenderPass members map straight across:
//   Event   = (Dx12RenderPassEvent)(int)feature.Event   — the engine RenderPassEvent enum is value-identical to
//             Dx12RenderPassEvent (verified-in-lockstep, chunk 19), so the cast is the 1:1 map.
//   Name    = the feature's type name (TimePass label + the inspector).
//   Enabled = feature.Active (the per-feature master switch).
//   Declare = run feature.Declare through a Dx12FeatureIOBuilder that translates string handles → builder
//             reads/writes against the canonical graph handles. A feature that declares nothing → an opaque
//             node (never culled, manual barriers), identical to an un-migrated built-in (design §5 D6).
//   Record  = bind the shared recorder to ctx + this feature, then drive feature.Record(recorder). The feature
//             touches only the backend-agnostic recorder — never a DX12 type.
public sealed class Dx12FeaturePassAdapter : IRenderPass {
    readonly RenderFeature feature;
    readonly Dx12FeaturePassRecorder recorder;   // shared (DX12HDRenderer-owned); re-bound per Record
    readonly string name;
    readonly string featureKey;                  // unique per adapter, for the IO builder's scratch namespacing

    public Dx12FeaturePassAdapter(RenderFeature feature, Dx12FeaturePassRecorder recorder, int registrationIndex) {
        this.feature = feature;
        this.recorder = recorder;
        name = feature.GetType().Name;
        // Scratch namespace: type name + this adapter's index, so the SAME feature type added twice (URP allows
        // it, design §5 D1) mints distinct scratch handles.
        featureKey = $"{name}#{registrationIndex}";
    }

    // The wrapped feature (so the bridge can compare the active set for a cheap rebuild-only-on-change).
    public RenderFeature Feature => feature;

    public Dx12RenderPassEvent Event => (Dx12RenderPassEvent)(int)feature.Event;
    public string Name => name;

    public bool Enabled(Dx12FrameContext ctx) => feature.Active;

    public void Declare(Dx12PassBuilder builder) {
        var io = new Dx12FeatureIOBuilder(builder, featureKey);
        feature.Declare(io);   // default-empty Declare touches nothing → opaque node (the safe escape hatch)
    }

    public void Record(Dx12FrameContext ctx) {
        recorder.Bind(ctx, feature);
        feature.Record(recorder);
    }
}
