using BepuPhysics;
using BepuPhysics.Collidables;
using static BallisticEngine.Bepu.BepuMath;
using NumVector3 = System.Numerics.Vector3;
using TkQuaternion = System.Numerics.Quaternion;   // engine math is System.Numerics now (was OpenTK)
using TkVector3 = System.Numerics.Vector3;

namespace BallisticEngine.Bepu;

// Wraps one Bepu body or static. Holds the compound recentering offset so the engine-facing
// Position is always the ENTITY origin, not the center of mass. After Invalidate() (body
// removed / world reset) every member degrades to a harmless no-op/default.
sealed class BepuBody : IPhysicsBody {
    readonly BepuPhysicsWorld world;
    internal readonly BodyHandle BodyHandle;
    internal readonly StaticHandle StaticHandle;
    internal readonly TypedIndex ShapeIndex;

    // Center of mass in body-local space (zero for non-recentered shapes).
    readonly NumVector3 centerOffset;

    public bool IsStatic { get; }
    internal bool Valid { get; private set; } = true;
    public object UserData { get; set; }

    internal BepuBody(BepuPhysicsWorld world, BodyHandle handle, TypedIndex shapeIndex, NumVector3 centerOffset) {
        this.world = world;
        BodyHandle = handle;
        ShapeIndex = shapeIndex;
        this.centerOffset = centerOffset;
        IsStatic = false;
    }

    internal BepuBody(BepuPhysicsWorld world, StaticHandle handle, TypedIndex shapeIndex, NumVector3 centerOffset) {
        this.world = world;
        StaticHandle = handle;
        ShapeIndex = shapeIndex;
        this.centerOffset = centerOffset;
        IsStatic = true;
    }

    internal void Invalidate() => Valid = false;

    BodyReference Body => world.Simulation.Bodies[BodyHandle];

    public bool IsAwake => Valid && !IsStatic && Body.Awake;

    public TkVector3 Position {
        get {
            if (!Valid)
                return default;
            RigidPose pose = IsStatic ? world.Simulation.Statics[StaticHandle].Pose : Body.Pose;
            return ToOpenTK(pose.Position - NumVector3.Transform(centerOffset, pose.Orientation));
        }
        set {
            if (!Valid)
                return;
            if (IsStatic) {
                StaticReference staticRef = world.Simulation.Statics[StaticHandle];
                RigidPose pose = staticRef.Pose;
                pose.Position = ToNumerics(value) + NumVector3.Transform(centerOffset, pose.Orientation);
                world.Simulation.Statics.ApplyDescription(StaticHandle,
                    new StaticDescription(pose.Position, pose.Orientation, ShapeIndex));
                return;
            }

            BodyReference body = Body;
            body.Pose.Position = ToNumerics(value) + NumVector3.Transform(centerOffset, body.Pose.Orientation);
            body.UpdateBounds();
        }
    }

    public TkQuaternion Rotation {
        get {
            if (!Valid)
                return TkQuaternion.Identity;
            return ToOpenTK(IsStatic
                ? world.Simulation.Statics[StaticHandle].Pose.Orientation
                : Body.Pose.Orientation);
        }
        set {
            if (!Valid)
                return;
            // Preserve the entity origin while rotating: the center-of-mass position moves
            // when the orientation changes.
            TkVector3 origin = Position;
            if (IsStatic) {
                StaticReference staticRef = world.Simulation.Statics[StaticHandle];
                System.Numerics.Quaternion orientation = ToNumerics(value);
                world.Simulation.Statics.ApplyDescription(StaticHandle, new StaticDescription(
                    ToNumerics(origin) + NumVector3.Transform(centerOffset, orientation),
                    orientation, ShapeIndex));
                return;
            }

            BodyReference body = Body;
            body.Pose.Orientation = ToNumerics(value);
            body.Pose.Position = ToNumerics(origin) + NumVector3.Transform(centerOffset, body.Pose.Orientation);
            body.UpdateBounds();
        }
    }

    public TkVector3 LinearVelocity {
        get => Valid && !IsStatic ? ToOpenTK(Body.Velocity.Linear) : default;
        set {
            if (!Valid || IsStatic)
                return;
            Body.Velocity.Linear = ToNumerics(value);
        }
    }

    public TkVector3 AngularVelocity {
        get => Valid && !IsStatic ? ToOpenTK(Body.Velocity.Angular) : default;
        set {
            if (!Valid || IsStatic)
                return;
            Body.Velocity.Angular = ToNumerics(value);
        }
    }

    public void ApplyImpulse(TkVector3 impulse) {
        if (!Valid || IsStatic)
            return;
        WakeUp();
        Body.ApplyLinearImpulse(ToNumerics(impulse));
    }

    public void ApplyImpulse(TkVector3 impulse, TkVector3 worldPoint) {
        if (!Valid || IsStatic)
            return;
        WakeUp();
        BodyReference body = Body;
        body.ApplyImpulse(ToNumerics(impulse), ToNumerics(worldPoint) - body.Pose.Position);
    }

    public void ApplyAngularImpulse(TkVector3 impulse) {
        if (!Valid || IsStatic)
            return;
        WakeUp();
        Body.ApplyAngularImpulse(ToNumerics(impulse));
    }

    public void WakeUp() {
        if (!Valid || IsStatic)
            return;
        BodyReference body = Body;
        if (!body.Awake)
            body.Awake = true;
    }
}
