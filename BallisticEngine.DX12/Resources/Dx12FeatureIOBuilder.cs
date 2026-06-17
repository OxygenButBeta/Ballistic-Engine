using System.Collections.Generic;
using BallisticEngine;   // IFeatureIOBuilder

namespace BallisticEngine.DX12;

// PHASE-3 (chunk 20) — the DX12 impl of the engine-agnostic IFeatureIOBuilder. When the graph compiles, the
// Dx12FeaturePassAdapter runs the wrapped RenderFeature.Declare(this) and this translates each engine-side
// string-handle declaration into a Dx12PassBuilder.Read/Write/ReadWrite against the CANONICAL graph handle of
// the SAME name (e.g. "SceneColor" → the same id every built-in pass uses, so a real DAG edge forms — the V1
// shared-handle-identity rule). A feature that declares nothing leaves the builder untouched → the adapter is
// an OPAQUE node (never culled, manual barriers — the safe escape hatch, design §5 D6), identical to an
// un-migrated built-in.
//
// Scratch handles (RequestScratch) are namespaced per-feature so two features minting "blur" don't collide; the
// returned name is what the feature passes to the recorder at Record time. In chunk 20 the proof tint feature
// requests no scratch (it ReadWrites SceneColor in place), so the scratch path is wired-but-dormant — the
// recorder's own ping-pong scratch (Dx12FeatureBlitter) is internal and not a graph handle. AllowCulling maps
// straight to the builder's opt-in (default OFF, same safety default).
internal sealed class Dx12FeatureIOBuilder : IFeatureIOBuilder {
    readonly Dx12PassBuilder builder;
    readonly string featureKey;            // unique per adapter, for scratch namespacing
    int scratchCounter;

    // The scratch names this feature requested → so the recorder can resolve them if a later verb needs them.
    public readonly List<string> Scratch = new();

    internal Dx12FeatureIOBuilder(Dx12PassBuilder builder, string featureKey) {
        this.builder = builder;
        this.featureKey = featureKey;
    }

    public void Read(string handleName) => builder.Read(builder.Resource(handleName));
    public void Write(string handleName) => builder.Write(builder.Resource(handleName));
    public void ReadWrite(string handleName) => builder.ReadWrite(builder.Resource(handleName));

    public string RequestScratch(string roleName) {
        // Namespace so distinct features (and repeats of the same scratch role) get distinct canonical handles.
        string name = $"Feature.{featureKey}.{roleName}.{scratchCounter++}";
        Scratch.Add(name);
        // A scratch is graph-owned/transient (not imported) — mint it so it participates in V2 aliasing later.
        builder.Resource(name, imported: false);
        return name;
    }

    public void AllowCulling(bool allow = true) {
        if (allow) builder.AllowCulling();
    }
}
