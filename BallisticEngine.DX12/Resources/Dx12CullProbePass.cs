namespace BallisticEngine.DX12;

// PHASE-2 V1 — a MINIMAL cull-path coverage pass. The plan (§V1, R-NEW-8) requires ≥1 cull-enabled pass in
// the matrix, because AllowCulling is default-OFF per pass: without a pass that opts in, the culler footgun
// (the opaque-edge rule, the iterate-to-fixpoint loop, the "non-imported write with no consumer" decision)
// SHIPS UNTESTED. This pass exists ONLY to exercise that path deterministically.
//
// It opts INTO culling and WRITES exactly one NON-imported scratch resource ("CullProbeScratch") that NO other
// pass reads. So the compiler MUST cull it every frame (its only write has no live consumer and is not
// imported). When culled it never reaches graphOrder → it never records → byte-NEUTRAL on the graph path.
//
// On the phase-1 LIST path (BALLISTIC_DX12_GRAPH unset) it is registered but Enabled() returns false, so it
// never records there either → byte-neutral. Net: the pass changes NOTHING in either path; its sole job is to
// make the culler's cull-and-fixpoint logic run on every BALLISTIC_DX12_GRAPH=1 frame so the cull machinery is
// continuously exercised (the matrix's cull-enabled-pass requirement, satisfied structurally).
//
// Event = BeforeShadows (0) so it sorts first and is irrelevant to every other pass's ordering; even if it
// somehow were NOT culled, its empty Record is a no-op.
public sealed class Dx12CullProbePass : IRenderPass {
    public Dx12RenderPassEvent Event => Dx12RenderPassEvent.BeforeShadows;
    public string Name => "CullProbe";

    // Never enabled on the phase-1 list path (so it's a no-op there); on the graph path it is culled before
    // Enabled is ever consulted. Either way it never records.
    public bool Enabled(Dx12FrameContext ctx) => false;

    public void Record(Dx12FrameContext ctx) { /* unreachable — culled on the graph path, !Enabled on the list path */ }

    // Opt into culling + write a non-imported scratch nobody reads → guaranteed culled, exercising the culler.
    public void Declare(Dx12PassBuilder b) {
        b.AllowCulling();
        b.Write(b.Resource("CullProbeScratch", imported: false));
    }
}
