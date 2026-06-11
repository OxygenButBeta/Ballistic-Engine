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
        // Bepu has no classical restitution: bounce comes from SOFT undamped contact springs
        // (the body penetrates like a trampoline and gets thrown back out) with the recovery
        // velocity cap lifted. Stiff springs would hand the impact to speculative contacts,
        // which absorb it inelastically — hence frequency goes DOWN as bounciness goes up.
        pairMaterial.MaximumRecoveryVelocity = bounciness > 0f ? float.MaxValue : 2f;
        pairMaterial.SpringSettings = new SpringSettings(30f - 25f * bounciness, 1f - bounciness);

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
        if (bestDepth >= TouchDepthThreshold)
            World.Contacts.Record(workerIndex, pair, in bestOffset, in bestNormal, isTrigger);

        // Triggers detect overlap but never solve it (no constraint = no physical response).
        return !isTrigger;
    }

    public bool ConfigureContactManifold(int workerIndex, CollidablePair pair, int childIndexA,
        int childIndexB, ref ConvexContactManifold manifold) =>
        true;

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
