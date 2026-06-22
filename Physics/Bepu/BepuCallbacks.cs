using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.Constraints;
using BepuUtilities;

namespace BallisticEngine.Bepu;

struct NarrowPhaseCallbacks : INarrowPhaseCallbacks {
    public BepuPhysicsWorld World;

    public void Initialize(Simulation simulation) {
    }

    public bool AllowContactGeneration(int workerIndex, CollidableReference a, CollidableReference b,
        ref float speculativeMargin) =>
        World.LayersCollide(a, b) &&
        (a.Mobility == CollidableMobility.Dynamic || b.Mobility == CollidableMobility.Dynamic ||
         World.GetMaterial(a).IsTrigger || World.GetMaterial(b).IsTrigger);

    public bool AllowContactGeneration(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB) =>
        true;

    const float TouchDepthThreshold = -0.005f;

    public bool ConfigureContactManifold<TManifold>(int workerIndex, CollidablePair pair,
        ref TManifold manifold, out PairMaterialProperties pairMaterial)
        where TManifold : unmanaged, IContactManifold<TManifold> {
        BepuPhysicsWorld.ContactMaterial a = World.GetMaterial(pair.A);
        BepuPhysicsWorld.ContactMaterial b = World.GetMaterial(pair.B);

        float bounciness = MathF.Max(a.Bounciness, b.Bounciness);
        pairMaterial.FrictionCoefficient = MathF.Sqrt(MathF.Max(0f, a.Friction) * MathF.Max(0f, b.Friction));
        pairMaterial.MaximumRecoveryVelocity = 4f;
        pairMaterial.SpringSettings = new SpringSettings(30f, 1f);

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

        if (!isTrigger && bounciness > 0f && manifold.Count > 0)
            World.Contacts.SampleApproach(workerIndex, pair, in bestOffset, in bestNormal, bounciness);

        if (bestDepth >= TouchDepthThreshold)
            World.Contacts.Record(workerIndex, pair, in bestOffset, in bestNormal, isTrigger,
                isTrigger ? 0f : bounciness);

        return !isTrigger;
    }

    public bool ConfigureContactManifold(int workerIndex, CollidablePair pair, int childIndexA,
        int childIndexB, ref ConvexContactManifold manifold) {
        bool childTrigger = World.GetChildTrigger(pair.A, childIndexA) ||
                            World.GetChildTrigger(pair.B, childIndexB);
        if (!childTrigger)
            return true;

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
                Vector3 worldPoint = World.GetPose(pair.A).Position + bestOffset;
                World.Contacts.RecordChild(workerIndex, pair, childIndexA, childIndexB,
                    in worldPoint, in bestNormal, isTrigger: true);
            }
        }

        manifold.Count = 0;
        return false;
    }

    public void Dispose() {
    }
}

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
