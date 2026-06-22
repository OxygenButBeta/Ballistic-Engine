using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.Constraints;
using BepuUtilities;

namespace BallisticEngine.Bepu;

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
