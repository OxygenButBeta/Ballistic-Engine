using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.Constraints;
using BepuUtilities;

namespace BallisticEngine.Bepu;

// Contact configuration: combines the two collidables' materials per pair. Friction combines
// geometrically (Unity-like feel); bounciness is approximated through the contact springs —
// Bepu 2 has no classic restitution, so a bouncy pair gets a stiffer, less damped spring and
// a higher recovery velocity cap. Good enough for gameplay, not for billiards.
struct NarrowPhaseCallbacks : INarrowPhaseCallbacks {
    public BepuPhysicsWorld World;

    public void Initialize(Simulation simulation) {
    }

    // Solid pairs need a dynamic body (kinematic/static pairs have nothing to solve). Trigger
    // pairs additionally allow kinematic-vs-static/kinematic — a kinematic player walking into
    // a static trigger zone is THE trigger use case, and triggers never generate constraints.
    public bool AllowContactGeneration(int workerIndex, CollidableReference a, CollidableReference b,
        ref float speculativeMargin) =>
        // Layer collision matrix first (Unity-style): a pair on non-colliding layers is rejected
        // outright, before the trigger/dynamic gate. Filtering here (not post-hoc) means the solver
        // never even forms the constraint, so it's free.
        World.LayersCollide(a, b) &&
        (a.Mobility == CollidableMobility.Dynamic || b.Mobility == CollidableMobility.Dynamic ||
         World.GetMaterial(a).IsTrigger || World.GetMaterial(b).IsTrigger);

    public bool AllowContactGeneration(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB) =>
        true;

    // Contacts shallower than this (negative = separated) don't count as "touching" for
    // events. The tolerance absorbs the millimeter jitter of resting speculative contacts so
    // a sleeping-adjacent box doesn't flap Enter/Exit every few steps.
    const float TouchDepthThreshold = -0.005f;

    public bool ConfigureContactManifold<TManifold>(int workerIndex, CollidablePair pair,
        ref TManifold manifold, out PairMaterialProperties pairMaterial)
        where TManifold : unmanaged, IContactManifold<TManifold> {
        BepuPhysicsWorld.ContactMaterial a = World.GetMaterial(pair.A);
        BepuPhysicsWorld.ContactMaterial b = World.GetMaterial(pair.B);

        float bounciness = MathF.Max(a.Bounciness, b.Bounciness);
        pairMaterial.FrictionCoefficient = MathF.Sqrt(MathF.Max(0f, a.Friction) * MathF.Max(0f, b.Friction));
        // Contacts stay a CRITICALLY DAMPED spring (frequency 30 Hz, damping 1) for solid resting
        // and stacking — NO bounce comes from the spring. Real coefficient-of-restitution bounce is
        // injected as a velocity-flip impulse on contact Enter (BepuContactTracker), which the spring
        // model alone cannot deliver at this solver rate (measured: spring rebound saturates ~0.1).
        //
        // MaximumRecoveryVelocity caps how fast a body is pushed OUT of an existing penetration. The old
        // 2 m/s was needlessly timid — only ~3 cm/step of pop-out — so a heavy body that DID punch into a
        // static mesh (a fast car clipping its belly on a terrain crest) un-sank far too slowly. Recovery
        // velocity adds NO bounce (restitution is the separate velocity-flip impulse) and never fires on a
        // resting/stacked contact (depth ≈ 0), so raising it is safe for stacking and resting stability;
        // it only governs how briskly a real overlap is corrected. 4 m/s clears a deep clip in a few steps
        // without being a "pop" that flings a lightly-touching body.
        pairMaterial.MaximumRecoveryVelocity = 4f;
        pairMaterial.SpringSettings = new SpringSettings(30f, 1f);

        // Contact events: record the deepest actually-touching contact (speculative contacts
        // with negative depth are approach predictions, not touches).
        bool isTrigger = a.IsTrigger || b.IsTrigger;
        float bestDepth = float.MinValue;
        Vector3 bestOffset = default, bestNormal = default;
        for (var i = 0; i < manifold.Count; i++) {
            manifold.GetContact(i, out Vector3 offset, out Vector3 normal, out float depth, out _);
            if (depth <= bestDepth)
                continue;
            bestDepth = depth;
            bestOffset = offset;
            bestNormal = normal;
        }
        // Restitution: sample the approach speed EVERY step the pair is in the manifold, including
        // while the contact is still speculative (depth < 0). The solver bleeds the closing velocity
        // off over the several steps speculative contacts span, so by the time depth crosses the
        // touch threshold the real impact speed is already gone — we must capture its PEAK earlier.
        // The tracker keeps the per-pair max and consumes it on Enter.
        if (!isTrigger && bounciness > 0f && manifold.Count > 0)
            World.Contacts.SampleApproach(workerIndex, pair, in bestOffset, in bestNormal, bounciness);

        if (bestDepth >= TouchDepthThreshold)
            World.Contacts.Record(workerIndex, pair, in bestOffset, in bestNormal, isTrigger,
                isTrigger ? 0f : bounciness);

        // Triggers detect overlap but never solve it (no constraint = no physical response).
        return !isTrigger;
    }

    // Per-child manifold callback — fires for each compound child sub-pair BEFORE the top-level
    // generic, and is the ONLY place Bepu hands us the child index. If either side's child is a
    // trigger, we record the overlap as a trigger event and return FALSE so that child contributes
    // NO contacts to the pair reduction → no constraint → no physical push, while solid siblings
    // (which return true) still solve normally. This is what makes a single body mix solid and
    // trigger colliders, Unity-style. Non-trigger children fall through to the solver untouched.
    public bool ConfigureContactManifold(int workerIndex, CollidablePair pair, int childIndexA,
        int childIndexB, ref ConvexContactManifold manifold) {
        bool childTrigger = World.GetChildTrigger(pair.A, childIndexA) ||
                            World.GetChildTrigger(pair.B, childIndexB);
        if (!childTrigger)
            return true; // solid child: keep its contacts for the pair-level solve

        // Trigger child: record the overlap (deepest touching contact) as a trigger event, then drop
        // it from the solve. Use the child indices in the key so this trigger event is distinct from
        // any solid sibling's event on the same body pair.
        if (manifold.Count > 0) {
            float bestDepth = float.MinValue;
            Vector3 bestOffset = default, bestNormal = default;
            for (var i = 0; i < manifold.Count; i++) {
                manifold.GetContact(i, out Vector3 offset, out Vector3 normal, out float depth, out _);
                if (depth <= bestDepth)
                    continue;
                bestDepth = depth;
                bestOffset = offset;
                bestNormal = normal;
            }
            if (bestDepth >= TouchDepthThreshold) {
                // Convex-manifold offsets are relative to child A's pose / the pair frame; resolve to
                // world the same way the top-level path does.
                Vector3 worldPoint = World.GetPose(pair.A).Position + bestOffset;
                World.Contacts.RecordChild(workerIndex, pair, childIndexA, childIndexB,
                    in worldPoint, in bestNormal, isTrigger: true);
            }
        }
        // Empty the child manifold AND return false: returning false alone left the contacts in the
        // reduced pair manifold (the trigger child still pushed the body — measured), so explicitly
        // zero the count so this child contributes nothing to the solver.
        manifold.Count = 0;
        return false; // no constraint from a trigger child
    }

    public void Dispose() {
    }
}

// Velocity integration: global gravity only. Per-body damping and gravity opt-out live in
// Rigidbody (engine side) as pre-step velocity adjustments — keeping this callback branch-free
// since it runs wide (SIMD) over every active body bundle.
struct PoseIntegratorCallbacks : IPoseIntegratorCallbacks {
    public BepuPhysicsWorld World;
    Vector3Wide gravityDt;

    public readonly AngularIntegrationMode AngularIntegrationMode => AngularIntegrationMode.Nonconserving;
    public readonly bool AllowSubstepsForUnconstrainedBodies => false;
    public readonly bool IntegrateVelocityForKinematics => false;

    public void Initialize(Simulation simulation) {
    }

    public void PrepareForIntegration(float dt) {
        Vector3Wide.Broadcast(World.GravityNumerics * dt, out gravityDt);
    }

    public void IntegrateVelocity(System.Numerics.Vector<int> bodyIndices, Vector3Wide position,
        QuaternionWide orientation, BodyInertiaWide localInertia, System.Numerics.Vector<int> integrationMask,
        int workerIndex, System.Numerics.Vector<float> dt, ref BodyVelocityWide velocity) {
        velocity.Linear += gravityDt;
    }
}
