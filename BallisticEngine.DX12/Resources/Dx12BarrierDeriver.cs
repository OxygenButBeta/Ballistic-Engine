using System;
using System.Collections.Generic;
using System.Text;
using Vortice.Direct3D12;

namespace BallisticEngine.DX12;

// PHASE-2 V3 — AUTO-DERIVED boundary barriers. The graph DERIVES each migrated pass's head transitions from its
// declared shared-resource usages (Dx12ResourceUsage, the usage→state map) and EMITS them before Record —
// REPLACING the phase-1 manual head transitions (gbuffer.ToShaderResource / DepthToReadOnly /
// DepthToShaderResource, ctx.SceneColor.ColorToShaderResource). Gated behind BALLISTIC_DX12_GRAPH_BARRIERS=1
// (requires GRAPH=1); default off → the migrated passes' manual head transitions run, byte-identical to V1/V2.
//
// SCOPE (deliberately narrow, plan §V3): ONLY the boundary transitions on SHARED/imported resources are derived
// (the G-buffer depth/color, the canonical SceneColor target). Pass-PRIVATE scratch ping-pong transitions
// (ssaoA/ssaoB, ssrTarget/ssrScene, ssgiTarget/ssgiDenoised/ssgiScene, bloomA/B) are NOT pass-boundary head
// transitions — they stay inline in each Record. The RT-only depth transitions (DepthToNonPixelShaderResource,
// in the GI/Reflections RT branches) are also NOT derived (RT paths are out of scope; they stay inline).
//
// WHY REUSE THE IDEMPOTENT SELF-METHODS (not hand-rolled raw barriers): a wrong derived barrier is the #1
// device-removal risk this layer (wrong timing/granularity can TDR). The resource objects already TRACK their
// own ResourceStates and emit a CORRECTLY-TIMED, correctly-scoped, idempotent transition (early-return when
// already in-state). So the deriver does NOT hand-roll ResourceBarrierTransition — it DECIDES which transition
// (the usage→method map, the V3 "derivation") and calls the proven method. This moves the DECISION from the
// pass body into the graph (the architectural V3 win) while keeping the battle-tested emit path. The "BATCHED
// into one ResourceBarrier" goal of the plan is satisfied at the granularity these methods already batch
// (gbuffer.ToShaderResource batches all colors+depth into one ExecuteSync; SceneColor is one transition).
//
// PLAN-LEVEL DEFENSE (plan §V3): a debug comparison (CompareToManual, dumped at init) computes the MANUAL set
// (each migrated pass's known head transitions, the reference) and the DERIVED set (from the Usages), then
// asserts derived ⊇ manual + same FINAL state per (pass, resourceRole). We then EMIT THE DERIVED SET ONLY (never
// both into one frame — emitting both changes the state-transition SEQUENCE GBV observes, muddying the oracle).
// "Same final state" is necessary NOT sufficient (a derived barrier can match the final state yet be wrong at a
// MID-FRAME read — wrong timing/granularity), so the REAL gate is GBV (validates state AT EACH USE) — this
// comparison is the cheap plan-level sanity check, GBV is the GPU-timeline gate.
public sealed class Dx12BarrierDeriver {
    // The boundary RESOURCE ROLE a usage targets — the identity the manual-vs-derived comparison keys on (the
    // final state per role must match). Scratch/RT roles are intentionally absent (out of V3 scope).
    public enum Role { GBufferColor, GBufferDepth, SceneColor }

    // The derived plan for ONE pass: the ordered usages + the (role → final-state) map it produces. Built once.
    public sealed class PassPlan {
        public readonly string PassName;
        public readonly List<Dx12ResourceUsage> Usages;
        public readonly Dictionary<Role, ResourceStates> FinalStates = new();   // role → state this plan leaves it in
        public PassPlan(string name, List<Dx12ResourceUsage> usages) { PassName = name; Usages = usages; }
    }

    readonly Dictionary<string, PassPlan> plans = new();   // pass name → its derived plan (null usages = no boundary)

    // Map a usage to (role, finalState) — the usage→state map. The SINGLE source of truth both the emit path and
    // the comparison read, so they can never diverge.
    public static (Role role, ResourceStates state) Map(Dx12ResourceUsage u) => u switch {
        // gbuffer.ToShaderResource() leaves ALL colors AND depth in the combined PIXEL|NON_PIXEL SRV state. The
        // role key is GBufferColor (the comparison treats the whole-g-buffer combined transition as the color
        // role's final state; depth's final state under this usage is the same combined state, asserted below).
        Dx12ResourceUsage.GBufferShaderRead =>
            (Role.GBufferColor, ResourceStates.PixelShaderResource | ResourceStates.NonPixelShaderResource),
        Dx12ResourceUsage.GBufferDepthShaderRead => (Role.GBufferDepth, ResourceStates.PixelShaderResource),
        Dx12ResourceUsage.GBufferDepthReadOnly   => (Role.GBufferDepth, ResourceStates.DepthRead),
        Dx12ResourceUsage.SceneColorShaderRead   => (Role.SceneColor,   ResourceStates.PixelShaderResource),
        _ => (Role.GBufferColor, ResourceStates.Common),
    };

    // Register a pass's derived plan (called once at Compile, only for passes that opted into BarriersDerived).
    // usages may be empty → the pass has no derived boundary transition (it removed a head transition that was a
    // no-op, or it only writes pass-private scratch). The plan still records the pass so the emit path knows the
    // pass is migrated (its manual head transitions are GONE — the deriver MUST emit, or the state is wrong).
    public void Register(string passName, List<Dx12ResourceUsage> usages) {
        var plan = new PassPlan(passName, usages);
        foreach (var u in usages) {
            var (role, state) = Map(u);
            plan.FinalStates[role] = state;   // last usage of a role wins (its final state)
        }
        plans[passName] = plan;
    }

    public bool IsMigrated(string passName) => plans.ContainsKey(passName);

    // EMIT the derived boundary transitions for `passName` against the concrete ctx resources, in declaration
    // order. Called by Dx12RenderGraph.ExecuteGraph just before pass.Record when the barriers door is on AND the
    // pass is migrated. Each call is an idempotent self-method on the state-tracked resource — a redundant one is
    // a free no-op (the manual set's idempotency, preserved). NO raw barriers (see class header). Returns silently
    // when the pass isn't migrated (its own manual head transitions still run inside Record).
    public void Emit(string passName, Dx12FrameContext ctx) {
        if (!plans.TryGetValue(passName, out var plan)) return;
        var gbuffer = ctx.GBuffer;
        foreach (var u in plan.Usages) {
            switch (u) {
                case Dx12ResourceUsage.GBufferShaderRead:      gbuffer.ToShaderResource(); break;
                case Dx12ResourceUsage.GBufferDepthShaderRead: gbuffer.DepthToShaderResource(); break;
                case Dx12ResourceUsage.GBufferDepthReadOnly:   gbuffer.DepthToReadOnly(); break;
                case Dx12ResourceUsage.SceneColorShaderRead:   ctx.SceneColor.ColorToShaderResource(); break;
            }
        }
    }

    // PLAN-LEVEL DEFENSE — compare the DERIVED set to the known MANUAL reference set per migrated pass. `manual`
    // is the reference (role → final state) the pass emitted BEFORE migration (the caller supplies it from a
    // static table of the old head transitions). Asserts derived ⊇ manual (every manual role is covered) + same
    // final state per role. Returns a human-readable report; sets `unsound` true on any mismatch. Run at init —
    // EMIT THE DERIVED SET ONLY at runtime; this is a static cross-check, not a second emit (plan §V3).
    public string CompareToManual(Dictionary<string, Dictionary<Role, ResourceStates>> manual, out bool unsound) {
        unsound = false;
        var sb = new StringBuilder();
        sb.AppendLine("[Dx12BarrierDeriver] V3 manual-vs-derived comparison (derived ⊇ manual + same final state):");
        foreach (var kv in manual) {
            string pass = kv.Key;
            var manualSet = kv.Value;
            if (!plans.TryGetValue(pass, out var plan)) {
                // The manual table lists a pass that hasn't been migrated yet → not an error (it still emits its
                // own manual head transitions). Note it so the report shows migration progress.
                sb.AppendLine($"  {pass}: NOT migrated (manual head transitions still inline) — skipped.");
                continue;
            }
            foreach (var mkv in manualSet) {
                Role role = mkv.Key;
                ResourceStates wantState = mkv.Value;
                if (!plan.FinalStates.TryGetValue(role, out var gotState)) {
                    sb.AppendLine($"  UNSOUND {pass}: manual needs {role}->{wantState} but DERIVED set has NO {role} usage.");
                    unsound = true;
                } else if (gotState != wantState) {
                    sb.AppendLine($"  UNSOUND {pass}: {role} manual final={wantState} but DERIVED final={gotState}.");
                    unsound = true;
                } else {
                    sb.AppendLine($"  OK {pass}: {role} -> {gotState} (derived == manual).");
                }
            }
            // Extra derived roles beyond the manual set are allowed (derived ⊇ manual) but flagged for review.
            foreach (var fkv in plan.FinalStates)
                if (!manualSet.ContainsKey(fkv.Key))
                    sb.AppendLine($"  NOTE {pass}: DERIVED adds {fkv.Key}->{fkv.Value} (superset of manual — allowed).");
        }
        return sb.ToString();
    }

    // The static MANUAL reference table — each migrated pass's head transitions BEFORE V3 (the comparison's
    // ground truth). Mirrors exactly the manual gbuffer/SceneColor head transitions removed from each Record.
    // Add a pass's row here the same commit it's migrated (so CompareToManual proves the derived set covers it).
    public static Dictionary<string, Dictionary<Role, ResourceStates>> ManualReference() {
        const ResourceStates GbCombined = ResourceStates.PixelShaderResource | ResourceStates.NonPixelShaderResource;
        return new Dictionary<string, Dictionary<Role, ResourceStates>> {
            // SSAO (chunk 14, first migrated): manual head was `gbuffer.DepthToShaderResource()`.
            ["SSAO"] = new() { [Role.GBufferDepth] = ResourceStates.PixelShaderResource },
            // Reference rows for the not-yet-migrated passes (CompareToManual reports them as "NOT migrated" until
            // their commit flips them — kept here so the table is the full target end-state, not just step 1).
            ["AerialPerspective"] = new() { [Role.GBufferDepth] = ResourceStates.PixelShaderResource },
            ["Fog"]               = new() { [Role.GBufferDepth] = ResourceStates.PixelShaderResource },
            ["Sky"]               = new() { [Role.GBufferDepth] = ResourceStates.DepthRead },
            ["Transparents"]      = new() { [Role.GBufferDepth] = ResourceStates.DepthRead },
            ["Deferred"]          = new() { [Role.GBufferColor] = GbCombined },
            // chunk 16 (SceneColor group): TAA reads SceneColor as SRV (native path). FSR additionally reads the
            // G-buffer depth as SRV for the upscaler reprojection. Composite reads the resolved SceneColor as SRV.
            ["TAA"]               = new() { [Role.SceneColor] = ResourceStates.PixelShaderResource, [Role.GBufferDepth] = ResourceStates.PixelShaderResource },
            ["FSR"]               = new() {
                [Role.SceneColor] = ResourceStates.PixelShaderResource,
                [Role.GBufferDepth] = ResourceStates.PixelShaderResource,
            },
            ["Composite"]         = new() { [Role.SceneColor] = ResourceStates.PixelShaderResource },
            // GI (screen path) + Reflections (SSR path): each reads SceneColor + G-buffer depth as SRV.
            ["GI"]                = new() {
                [Role.SceneColor] = ResourceStates.PixelShaderResource,
                [Role.GBufferDepth] = ResourceStates.PixelShaderResource,
            },
            ["Reflections"]       = new() {
                [Role.SceneColor] = ResourceStates.PixelShaderResource,
                [Role.GBufferDepth] = ResourceStates.PixelShaderResource,
            },
        };
    }
}
