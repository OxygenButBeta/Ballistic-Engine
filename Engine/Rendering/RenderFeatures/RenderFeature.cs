namespace BallisticEngine;

// One authored custom render pass (phase 3 — the engine's mirror of Unity URP's ScriptableRenderPass,
// the FEATURE the user asked for: "a render-feature / custom-pass system like Unity"). A game/project
// subclasses this in GameScripts.dll, decorates its params with the existing [Range]/[Tooltip]/[ShowIf]…
// attributes, declares WHEN it runs (Event) and which resources it reads/writes (Declare), and records
// its work through a backend-agnostic IFeaturePassRecorder (Record). The DX12 backend (chunk 20) adapts
// each active feature into an IRenderPass and graph.Add's it so the existing graph schedules / culls /
// aliases / auto-barriers it exactly like a built-in.
//
// KEY DIVERGENCE FROM VolumeComponent (design §2a): a feature does NOT blend. Its params are PLAIN
// decorated members (public props/fields), NOT VolumeParameter wrappers — a feature is on or off, it
// does not cross-fade like a post-fx grade. So a feature is reflection-shaped exactly like a Behaviour:
// its members serialize through ComponentReflection / the scene YAML for free (chunk 21).
//
// ENGINE-SIDE ONLY (the seam decision, design §3): ZERO reference to BallisticEngine.DX12. The base, its
// Event enum, the recorder, and the IO-declare surface are all engine-agnostic so a game references the
// engine library only. Discovered by ComponentRegistry.Build (a RenderFeatureMenu entry).
public abstract class RenderFeature {
    // Per-feature master switch (the checkbox next to the feature's name in the editor's feature list).
    // An inactive feature contributes no pass to the frame. The backend adapter maps this to
    // IRenderPass.Enabled. Defaults true when authored (URP parity, design §5 / D5) — but the WHOLE
    // layer is inert until a RenderFeatures SceneBehaviour with >=1 feature exists in the scene.
    public bool Active { get; set; } = true;

    // WHEN this feature injects. The backend maps it 1:1 onto Dx12RenderPassEvent (same values/order).
    // Default PostProcess so a hand-added feature lands somewhere sane (after lighting/sky, before
    // composite). A concrete feature overrides this getter (or exposes it as an authored member).
    public virtual RenderPassEvent Event => RenderPassEvent.PostProcess;

    // Declare this feature's resource reads/writes against CANONICAL string handle names (e.g.
    // "SceneColor"), so the backend can form DAG edges with the built-ins (V1 cull / V2 alias / V3
    // auto-barriers — design §5 / D6). Engine-agnostic by design: the parameter is a backend-neutral
    // IFeatureIOBuilder, NEVER the DX12 Dx12PassBuilder — a feature must not reference the backend.
    //
    // DEFAULT empty = the OPAQUE / imported-everything escape hatch (never culled, manual barriers) —
    // identical to an un-migrated built-in. A feature opts into graph participation by overriding this.
    public virtual void Declare(IFeatureIOBuilder io) {
    }

    // Record the feature's GPU work through the backend-agnostic recorder. Runs at this feature's Event
    // when Active, slotted among the built-in passes. The recorder resolves named handles to concrete
    // backend targets — the feature never touches a DX12 type.
    public abstract void Record(IFeaturePassRecorder recorder);
}
