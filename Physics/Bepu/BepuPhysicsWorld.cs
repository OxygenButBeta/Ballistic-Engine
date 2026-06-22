using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.Constraints;
using BepuPhysics.Trees;
using BepuUtilities;
using BepuUtilities.Memory;
using static BallisticEngine.Bepu.BepuMath;
using BepuMesh = BepuPhysics.Collidables.Mesh;
using TkVector3 = System.Numerics.Vector3;

namespace BallisticEngine.Bepu;

public sealed class BepuPhysicsWorld : IPhysicsWorld {
    internal Simulation Simulation;
    readonly BufferPool bufferPool = new();
    readonly ThreadDispatcher dispatcher =
        new(Math.Max(1, Environment.ProcessorCount - 2));

    internal readonly BepuContactTracker Contacts;

    public BepuPhysicsWorld() {
        Contacts = new BepuContactTracker(this, dispatcher.ThreadCount);
    }

    public IReadOnlyList<PhysicsContactEvent> ContactEvents => Contacts.Events;

    readonly Dictionary<int, BepuBody> bodiesByHandle = new();
    readonly Dictionary<int, BepuBody> staticsByHandle = new();
    readonly Dictionary<int, ContactMaterial> bodyMaterials = new();
    readonly Dictionary<int, ContactMaterial> staticMaterials = new();

    readonly Dictionary<int, bool[]> bodyChildTriggers = new();
    readonly Dictionary<int, bool[]> staticChildTriggers = new();

    readonly List<BepuConstraint> constraints = new();

    internal readonly struct ContactMaterial {
        public readonly float Friction;
        public readonly float Bounciness;
        public readonly bool IsTrigger;
        public readonly int Layer;

        public ContactMaterial(float friction, float bounciness, bool isTrigger = false, int layer = 0) {
            Friction = friction;
            Bounciness = bounciness;
            IsTrigger = isTrigger;
            Layer = layer;
        }
    }

    TkVector3 gravity = new(0f, -9.81f, 0f);
    internal Vector3 GravityNumerics = new(0f, -9.81f, 0f);

    public TkVector3 Gravity {
        get => gravity;
        set {
            gravity = value;
            GravityNumerics = ToNumerics(value);
        }
    }

    public Func<int, int, bool> LayerCollisionMatrix { get; set; }

    internal bool LayersCollide(CollidableReference a, CollidableReference b) {
        Func<int, int, bool> matrix = LayerCollisionMatrix;
        if (matrix is null)
            return true;
        return matrix(GetMaterial(a).Layer, GetMaterial(b).Layer);
    }

    void EnsureSimulation() {
        Simulation ??= Simulation.Create(
            bufferPool,
            new NarrowPhaseCallbacks { World = this },
            new PoseIntegratorCallbacks { World = this },
            new SolveDescription(velocityIterationCount: 2, substepCount: 4));
    }

    public void Step(float deltaTime) {
        if (Simulation is null || deltaTime <= 0f)
            return;

        Simulation.Timestep(deltaTime, dispatcher);
        Contacts.Flush();
    }

    public void Reset() {
        foreach (BepuBody body in bodiesByHandle.Values)
            body.Invalidate();
        foreach (BepuBody body in staticsByHandle.Values)
            body.Invalidate();

        foreach (BepuConstraint constraint in constraints)
            constraint.Invalidate();
        constraints.Clear();

        bodiesByHandle.Clear();
        staticsByHandle.Clear();
        bodyMaterials.Clear();
        staticMaterials.Clear();
        bodyChildTriggers.Clear();
        staticChildTriggers.Clear();

        Contacts.Clear();
        Simulation?.Dispose();
        Simulation = null;
        bufferPool.Clear();
    }

    public IPhysicsBody AddBody(in PhysicsBodyDescription description) {
        if (description.Shapes is null || description.Shapes.Length == 0)
            return null;

        EnsureSimulation();

        if (!TryBuildShape(in description, out TypedIndex shapeIndex, out BodyInertia inertia,
                out Vector3 centerOffset, out bool[] childTriggers))
            return null;

        Quaternion orientation = ToNumerics(description.Rotation);
        var pose = new RigidPose(
            ToNumerics(description.Position) + Vector3.Transform(centerOffset, orientation),
            orientation);

        var material = new ContactMaterial(description.Friction, description.Bounciness,
            description.IsTrigger, description.Layer);
        BepuBody wrapper;

        if (description.Type == PhysicsBodyType.Static) {
            StaticHandle handle = Simulation.Statics.Add(
                new StaticDescription(pose.Position, pose.Orientation, shapeIndex));
            wrapper = new BepuBody(this, handle, shapeIndex, centerOffset);
            staticsByHandle[handle.Value] = wrapper;
            staticMaterials[handle.Value] = material;
            staticChildTriggers[handle.Value] = childTriggers;
        }
        else {
            var collidable = new CollidableDescription(shapeIndex, 0.1f,
                ContinuousDetection.Continuous(1e-3f, 1e-3f));

            if (description.Type == PhysicsBodyType.Dynamic && description.FreezeRotation)
                inertia.InverseInertiaTensor = default;

            BodyDescription bodyDescription = description.Type == PhysicsBodyType.Kinematic
                ? BodyDescription.CreateKinematic(pose, collidable, 0.01f)
                : BodyDescription.CreateDynamic(pose, inertia, collidable, 0.01f);

            BodyHandle handle = Simulation.Bodies.Add(bodyDescription);
            wrapper = new BepuBody(this, handle, shapeIndex, centerOffset);
            bodiesByHandle[handle.Value] = wrapper;
            bodyMaterials[handle.Value] = material;
            bodyChildTriggers[handle.Value] = childTriggers;
        }

        return wrapper;
    }

    public void RemoveBody(IPhysicsBody body) {
        if (body is not BepuBody bepuBody || !bepuBody.Valid || Simulation is null)
            return;

        Contacts.OnBodyRemoved(bepuBody);

        if (bepuBody.IsStatic) {
            Simulation.Statics.Remove(bepuBody.StaticHandle);
            staticsByHandle.Remove(bepuBody.StaticHandle.Value);
            staticMaterials.Remove(bepuBody.StaticHandle.Value);
            staticChildTriggers.Remove(bepuBody.StaticHandle.Value);
        }
        else {
            Simulation.Bodies.Remove(bepuBody.BodyHandle);
            bodiesByHandle.Remove(bepuBody.BodyHandle.Value);
            bodyMaterials.Remove(bepuBody.BodyHandle.Value);
            bodyChildTriggers.Remove(bepuBody.BodyHandle.Value);
        }

        Simulation.Shapes.RecursivelyRemoveAndDispose(bepuBody.ShapeIndex, bufferPool);
        bepuBody.Invalidate();
    }

    internal ContactMaterial GetMaterial(CollidableReference collidable) {
        Dictionary<int, ContactMaterial> source =
            collidable.Mobility == CollidableMobility.Static ? staticMaterials : bodyMaterials;
        int handle = collidable.Mobility == CollidableMobility.Static
            ? collidable.StaticHandle.Value
            : collidable.BodyHandle.Value;
        return source.TryGetValue(handle, out ContactMaterial material)
            ? material
            : new ContactMaterial(0.6f, 0f);
    }

    internal bool GetChildTrigger(CollidableReference collidable, int childIndex) {
        Dictionary<int, bool[]> source =
            collidable.Mobility == CollidableMobility.Static ? staticChildTriggers : bodyChildTriggers;
        int handle = collidable.Mobility == CollidableMobility.Static
            ? collidable.StaticHandle.Value
            : collidable.BodyHandle.Value;
        return source.TryGetValue(handle, out bool[] triggers)
               && childIndex >= 0 && childIndex < triggers.Length
               && triggers[childIndex];
    }

    internal RigidPose GetPose(CollidableReference collidable) =>
        collidable.Mobility == CollidableMobility.Static
            ? Simulation.Statics[collidable.StaticHandle].Pose
            : Simulation.Bodies[collidable.BodyHandle].Pose;

    bool TryBuildShape(in PhysicsBodyDescription description, out TypedIndex shapeIndex,
        out BodyInertia inertia, out Vector3 centerOffset, out bool[] childTriggers) {
        shapeIndex = default;
        inertia = default;
        centerOffset = Vector3.Zero;
        childTriggers = null;

        PhysicsShapePart[] parts = description.Shapes;
        bool single = parts.Length == 1 &&
                      parts[0].LocalPosition == TkVector3.Zero &&
                      parts[0].LocalRotation == Quaternion.Identity;

        if (parts.Length == 1 && parts[0].Shape is MeshShape meshShape) {
            if (description.Type == PhysicsBodyType.Dynamic) {
                Debugging.LogError("Physics: mesh shapes are static/kinematic only; dynamic body rejected.");
                return false;
            }
            if (!single) {
                Debugging.LogError("Physics: mesh shapes cannot carry a local offset; center must be zero.");
                return false;
            }

            shapeIndex = AddMesh(meshShape);
            childTriggers = [parts[0].IsTrigger];
            return true;
        }

        if (single) {
            shapeIndex = AddConvex(parts[0].Shape, description.Mass, out inertia);
            childTriggers = [parts[0].IsTrigger];
            return shapeIndex.Exists;
        }

        var builder = new CompoundBuilder(bufferPool, Simulation.Shapes, parts.Length);
        var triggerList = new List<bool>(parts.Length);
        try {
            int added = 0;
            foreach (PhysicsShapePart part in parts) {
                var localPose = new RigidPose(ToNumerics(part.LocalPosition), ToNumerics(part.LocalRotation));
                float weight = MathF.Max(1e-4f, VolumeOf(part.Shape));
                switch (part.Shape) {
                    case BoxShape box:
                        builder.Add(MakeBox(box), localPose, weight);
                        triggerList.Add(part.IsTrigger);
                        added++;
                        break;
                    case SphereShape sphere:
                        builder.Add(MakeSphere(sphere), localPose, weight);
                        triggerList.Add(part.IsTrigger);
                        added++;
                        break;
                    case CapsuleShape capsule:
                        builder.Add(MakeCapsule(capsule), localPose, weight);
                        triggerList.Add(part.IsTrigger);
                        added++;
                        break;
                    default:
                        Debugging.LogWarning($"Physics: {part.Shape?.GetType().Name} is not allowed inside a compound body; part skipped.");
                        break;
                }
            }

            if (added == 0)
                return false;
            childTriggers = triggerList.ToArray();

            Buffer<CompoundChild> children;
            if (description.Type == PhysicsBodyType.Dynamic) {
                builder.BuildDynamicCompound(out children, out inertia, out centerOffset);
            }
            else {
                builder.BuildKinematicCompound(out children, out centerOffset);
            }

            shapeIndex = Simulation.Shapes.Add(new Compound(children));
            return true;
        }
        finally {
            builder.Dispose();
        }
    }

    TypedIndex AddConvex(PhysicsShape shape, float mass, out BodyInertia inertia) {
        switch (shape) {
            case BoxShape boxShape: {
                Box box = MakeBox(boxShape);
                inertia = box.ComputeInertia(mass);
                return Simulation.Shapes.Add(box);
            }
            case SphereShape sphereShape: {
                Sphere sphere = MakeSphere(sphereShape);
                inertia = sphere.ComputeInertia(mass);
                return Simulation.Shapes.Add(sphere);
            }
            case CapsuleShape capsuleShape: {
                Capsule capsule = MakeCapsule(capsuleShape);
                inertia = capsule.ComputeInertia(mass);
                return Simulation.Shapes.Add(capsule);
            }
            default:
                Debugging.LogError($"Physics: unsupported shape {shape?.GetType().Name}.");
                inertia = default;
                return default;
        }
    }

    TypedIndex AddMesh(MeshShape meshShape) {
        int triangleCount = meshShape.Indices.Length / 3;
        bufferPool.Take(triangleCount, out Buffer<Triangle> triangles);
        for (int i = 0; i < triangleCount; i++) {
            triangles[i] = new Triangle(
                ToNumerics(meshShape.Vertices[meshShape.Indices[i * 3 + 0]]),
                ToNumerics(meshShape.Vertices[meshShape.Indices[i * 3 + 2]]),
                ToNumerics(meshShape.Vertices[meshShape.Indices[i * 3 + 1]]));
        }

        var mesh = new BepuMesh(triangles, ToNumerics(meshShape.Scale), bufferPool);
        return Simulation.Shapes.Add(mesh);
    }

    const float MinDimension = 1e-3f;

    static Box MakeBox(BoxShape box) => new(
        MathF.Max(MinDimension, box.Size.X),
        MathF.Max(MinDimension, box.Size.Y),
        MathF.Max(MinDimension, box.Size.Z));

    static Sphere MakeSphere(SphereShape sphere) => new(MathF.Max(MinDimension, sphere.Radius));

    static Capsule MakeCapsule(CapsuleShape capsule) => new(
        MathF.Max(MinDimension, capsule.Radius),
        MathF.Max(0f, capsule.Length));

    static float VolumeOf(PhysicsShape shape) => shape switch {
        BoxShape b => MathF.Max(MinDimension, b.Size.X) * MathF.Max(MinDimension, b.Size.Y) * MathF.Max(MinDimension, b.Size.Z),
        SphereShape s => 4f / 3f * MathF.PI * MathF.Pow(MathF.Max(MinDimension, s.Radius), 3f),
        CapsuleShape c => MathF.PI * c.Radius * c.Radius * (4f / 3f * c.Radius + c.Length),
        _ => 0f,
    };

    struct ClosestRayHitHandler : IRayHitHandler {
        public float T;
        public Vector3 Normal;
        public CollidableReference Collidable;
        public bool Hit;
        public int LayerMask;
        public BepuPhysicsWorld World;

        public bool AllowTest(CollidableReference collidable) =>
            LayerMask == ~0 || (LayerMask & (1 << World.GetMaterial(collidable).Layer)) != 0;
        public bool AllowTest(CollidableReference collidable, int childIndex) => true;

        public void OnRayHit(in RayData ray, ref float maximumT, float t, in Vector3 normal,
            CollidableReference collidable, int childIndex) {
            maximumT = t;
            if (t > T)
                return;
            T = t;
            Normal = normal;
            Collidable = collidable;
            Hit = true;
        }
    }

    public bool Raycast(TkVector3 origin, TkVector3 direction, float maxDistance, int layerMask,
        out PhysicsRayHit hit) {
        hit = default;
        if (Simulation is null)
            return false;

        var handler = new ClosestRayHitHandler { T = float.MaxValue, LayerMask = layerMask, World = this };
        Simulation.RayCast(ToNumerics(origin), ToNumerics(direction), maxDistance, ref handler);
        if (!handler.Hit)
            return false;

        hit.Distance = handler.T;
        hit.Point = origin + direction * handler.T;
        TkVector3 normal = ToOpenTK(handler.Normal);
        hit.Normal = normal.LengthSquared() > 0f ? normal.Normalized() : -direction;
        hit.Body = Lookup(handler.Collidable);
        return true;
    }

    internal BepuBody Lookup(CollidableReference collidable) =>
        collidable.Mobility == CollidableMobility.Static
            ? staticsByHandle.GetValueOrDefault(collidable.StaticHandle.Value)
            : bodiesByHandle.GetValueOrDefault(collidable.BodyHandle.Value);

    public IPhysicsConstraint AddConstraint(in PhysicsConstraintDescription description) {
        if (Simulation is null)
            return null;

        if (description.BodyA is not BepuBody bodyA || !bodyA.Valid || bodyA.IsStatic) {
            Debugging.LogError("Physics: AddConstraint needs a non-static Rigidbody as body A.");
            return null;
        }
        BodyHandle handleA = bodyA.BodyHandle;

        BodyHandle handleB;
        BodyHandle anchorBody = default;
        bool hasAnchor = false;
        if (description.BodyB is BepuBody bodyB && bodyB.Valid && !bodyB.IsStatic) {
            handleB = bodyB.BodyHandle;
        }
        else {
            Vector3 worldAnchor = ToNumerics(bodyA.Position) +
                                  Vector3.Transform(ToNumerics(description.LocalAnchorA), ToNumerics(bodyA.Rotation));
            anchorBody = Simulation.Bodies.Add(
                BodyDescription.CreateKinematic(new RigidPose(worldAnchor), default, -1f));
            handleB = anchorBody;
            hasAnchor = true;
        }

        SpringSettings spring = SpringFor(description);
        ConstraintHandle handle = BuildConstraint(in description, handleA, handleB, spring);
        if (handle.Value < 0) {
            if (hasAnchor)
                Simulation.Bodies.Remove(anchorBody);
            return null;
        }

        var wrapper = new BepuConstraint(this, handle, anchorBody, hasAnchor, bodyA,
            description.BodyB as BepuBody);
        constraints.Add(wrapper);
        return wrapper;
    }

    void WakeAndNudge(BepuBody body) {
        if (body is null || body.IsStatic || !Simulation.Bodies.BodyExists(body.BodyHandle))
            return;
        Simulation.Awakener.AwakenBody(body.BodyHandle);
        body.LinearVelocity += GravityNumerics * (1f / 60f);
    }

    static SpringSettings SpringFor(in PhysicsConstraintDescription d) =>
        d.Frequency > 0f ? new SpringSettings(d.Frequency, d.DampingRatio)
                         : new SpringSettings(30f, 1f);

    ConstraintHandle BuildConstraint(in PhysicsConstraintDescription d, BodyHandle a, BodyHandle b,
        SpringSettings spring) {
        Vector3 offsetA = ToNumerics(d.LocalAnchorA);
        Vector3 offsetB = ToNumerics(d.LocalAnchorB);
        switch (d.Type) {
            case PhysicsConstraintType.BallSocket:
                return Simulation.Solver.Add(a, b, new BallSocket {
                    LocalOffsetA = offsetA, LocalOffsetB = offsetB, SpringSettings = spring,
                });

            case PhysicsConstraintType.Hinge: {
                Vector3 axis = SafeAxis(d.Axis);
                return Simulation.Solver.Add(a, b, new Hinge {
                    LocalOffsetA = offsetA, LocalHingeAxisA = axis,
                    LocalOffsetB = offsetB, LocalHingeAxisB = axis,
                    SpringSettings = spring,
                });
            }

            case PhysicsConstraintType.Fixed: {
                RigidPose poseA = Simulation.Bodies[a].Pose;
                RigidPose poseB = Simulation.Bodies[b].Pose;
                Quaternion invOrientA = Quaternion.Conjugate(poseA.Orientation);
                Vector3 localOffset = Vector3.Transform(poseB.Position - poseA.Position, invOrientA);
                Quaternion localOrientation = Quaternion.Normalize(
                    Quaternion.Concatenate(poseB.Orientation, invOrientA));
                return Simulation.Solver.Add(a, b, new Weld {
                    LocalOffset = localOffset,
                    LocalOrientation = localOrientation,
                    SpringSettings = spring,
                });
            }

            case PhysicsConstraintType.Spring: {
                float target = MathF.Max(0f, d.TargetDistance);
                return Simulation.Solver.Add(a, b,
                    new DistanceServo(offsetA, offsetB, target, spring, ServoSettings.Default));
            }

            case PhysicsConstraintType.Slider:
                return Simulation.Solver.Add(a, b, new PointOnLineServo {
                    LocalOffsetA = offsetA, LocalOffsetB = offsetB,
                    LocalDirection = SafeAxis(d.Axis),
                    ServoSettings = ServoSettings.Default, SpringSettings = spring,
                });

            default:
                Debugging.LogError($"Physics: unsupported constraint type {d.Type}.");
                return new ConstraintHandle(-1);
        }
    }

    static Vector3 SafeAxis(Vector3 axis) {
        float len = axis.Length();
        return len > 1e-6f ? axis / len : Vector3.UnitY;
    }

    public void RemoveConstraint(IPhysicsConstraint constraint) {
        if (constraint is not BepuConstraint bepu || !bepu.IsValid || Simulation is null)
            return;

        Simulation.Solver.Remove(bepu.Handle);
        if (bepu.HasAnchor)
            Simulation.Bodies.Remove(bepu.AnchorBody);

        WakeAndNudge(bepu.BodyA);
        WakeAndNudge(bepu.BodyB);

        constraints.Remove(bepu);
        bepu.Invalidate();
    }

    struct ClosestSweepHitHandler : ISweepHitHandler {
        public float T;
        public Vector3 Location;
        public Vector3 Normal;
        public CollidableReference Collidable;
        public bool Hit;
        public int LayerMask;
        public BepuPhysicsWorld World;

        public bool AllowTest(CollidableReference collidable) =>
            LayerMask == ~0 || (LayerMask & (1 << World.GetMaterial(collidable).Layer)) != 0;
        public bool AllowTest(CollidableReference collidable, int child) => true;

        public void OnHit(ref float maximumT, float t, in Vector3 hitLocation, in Vector3 hitNormal,
            CollidableReference collidable) {
            if (t < maximumT)
                maximumT = t;
            if (t >= T)
                return;
            T = t;
            Location = hitLocation;
            Normal = hitNormal;
            Collidable = collidable;
            Hit = true;
        }

        public void OnHitAtZeroT(ref float maximumT, CollidableReference collidable) {
            maximumT = 0f;
            T = 0f;
            Location = default;
            Normal = default;
            Collidable = collidable;
            Hit = true;
        }
    }

    public bool ShapeCast(PhysicsShape shape, TkVector3 position, Quaternion rotation,
        TkVector3 direction, float maxDistance, int layerMask, out PhysicsRayHit hit) {
        hit = default;
        if (Simulation is null)
            return false;

        float dirLen = direction.Length();
        if (dirLen < 1e-6f || maxDistance <= 0f)
            return false;
        TkVector3 dir = direction / dirLen;

        var pose = new RigidPose(ToNumerics(position), ToNumerics(rotation));
        var velocity = new BodyVelocity { Linear = ToNumerics(dir) };
        var handler = new ClosestSweepHitHandler { T = float.MaxValue, LayerMask = layerMask, World = this };

        switch (shape) {
            case BoxShape box:
                Simulation.Sweep(MakeBox(box), pose, velocity, maxDistance, bufferPool, ref handler);
                break;
            case SphereShape sphere:
                Simulation.Sweep(MakeSphere(sphere), pose, velocity, maxDistance, bufferPool, ref handler);
                break;
            case CapsuleShape capsule:
                Simulation.Sweep(MakeCapsule(capsule), pose, velocity, maxDistance, bufferPool, ref handler);
                break;
            default:
                Debugging.LogError($"Physics: ShapeCast needs a convex shape (box/sphere/capsule); got {shape?.GetType().Name}.");
                return false;
        }

        if (!handler.Hit)
            return false;

        hit.Distance = handler.T;
        hit.Point = position + dir * handler.T;
        TkVector3 location = ToOpenTK(handler.Location);
        if (location.LengthSquared() > 0f)
            hit.Point = location;
        TkVector3 normal = ToOpenTK(handler.Normal);
        hit.Normal = normal.LengthSquared() > 0f ? normal.Normalized() : -dir;
        hit.Body = Lookup(handler.Collidable);
        return true;
    }

    struct OverlapCollector : IBreakableForEach<CollidableReference> {
        public BepuPhysicsWorld World;
        public int LayerMask;
        public List<IPhysicsBody> Results;

        public bool LoopBody(CollidableReference collidable) {
            if (LayerMask != ~0 && (LayerMask & (1 << World.GetMaterial(collidable).Layer)) == 0)
                return true;
            BepuBody body = World.Lookup(collidable);
            if (body is not null && !Results.Contains(body))
                Results.Add(body);
            return true;
        }
    }

    void QueryBounds(Vector3 min, Vector3 max, int layerMask, List<IPhysicsBody> results) {
        var collector = new OverlapCollector { World = this, LayerMask = layerMask, Results = results };
        Simulation.BroadPhase.GetOverlaps(new BoundingBox(min, max), ref collector);
    }

    public int OverlapSphere(TkVector3 center, float radius, int layerMask, List<IPhysicsBody> results) {
        if (Simulation is null || radius <= 0f)
            return 0;

        Vector3 c = ToNumerics(center);
        var r = new Vector3(radius);

        int before = results.Count;
        var candidates = new List<IPhysicsBody>();
        QueryBounds(c - r, c + r, layerMask, candidates);
        float r2 = radius * radius;
        foreach (IPhysicsBody body in candidates) {
            Vector3 p = ToNumerics(body.Position);
            if ((p - c).LengthSquared() <= r2 && !results.Contains(body))
                results.Add(body);
        }
        return results.Count - before;
    }

    public int OverlapBox(TkVector3 center, TkVector3 halfExtents, System.Numerics.Quaternion orientation,
        int layerMask, List<IPhysicsBody> results) {
        if (Simulation is null)
            return 0;

        Vector3 c = ToNumerics(center);
        Vector3 h = ToNumerics(halfExtents);
        System.Numerics.Quaternion q = ToNumerics(orientation);
        Vector3 ex =
            Vector3.Abs(Vector3.Transform(new Vector3(h.X, 0, 0), q)) +
            Vector3.Abs(Vector3.Transform(new Vector3(0, h.Y, 0), q)) +
            Vector3.Abs(Vector3.Transform(new Vector3(0, 0, h.Z), q));

        int before = results.Count;
        QueryBounds(c - ex, c + ex, layerMask, results);
        return results.Count - before;
    }

    struct OverlapSweepCollector : ISweepHitHandler {
        public BepuPhysicsWorld World;
        public int LayerMask;
        public List<IPhysicsBody> Results;

        public bool AllowTest(CollidableReference collidable) =>
            LayerMask == ~0 || (LayerMask & (1 << World.GetMaterial(collidable).Layer)) != 0;
        public bool AllowTest(CollidableReference collidable, int child) => true;

        const float OverlapEpsilon = 1e-3f;

        void Add(CollidableReference collidable) {
            BepuBody body = World.Lookup(collidable);
            if (body is not null && !Results.Contains(body))
                Results.Add(body);
        }

        public void OnHit(ref float maximumT, float t, in Vector3 hitLocation, in Vector3 hitNormal,
            CollidableReference collidable) {
            if (t <= OverlapEpsilon)
                Add(collidable);
        }

        public void OnHitAtZeroT(ref float maximumT, CollidableReference collidable) => Add(collidable);
    }

    public int OverlapShape(PhysicsShape shape, TkVector3 position, Quaternion rotation, int layerMask,
        List<IPhysicsBody> results) {
        if (Simulation is null)
            return 0;

        var pose = new RigidPose(ToNumerics(position), ToNumerics(rotation));
        var velocity = new BodyVelocity { Linear = new Vector3(0f, -1f, 0f) };
        var handler = new OverlapSweepCollector { World = this, LayerMask = layerMask, Results = results };
        const float tinySweep = 1e-3f;

        int before = results.Count;
        switch (shape) {
            case BoxShape box:
                Simulation.Sweep(MakeBox(box), pose, velocity, tinySweep, bufferPool, ref handler);
                break;
            case SphereShape sphere:
                Simulation.Sweep(MakeSphere(sphere), pose, velocity, tinySweep, bufferPool, ref handler);
                break;
            case CapsuleShape capsule:
                Simulation.Sweep(MakeCapsule(capsule), pose, velocity, tinySweep, bufferPool, ref handler);
                break;
            default:
                Debugging.LogError($"Physics: OverlapShape needs a convex shape (box/sphere/capsule); got {shape?.GetType().Name}.");
                return 0;
        }
        return results.Count - before;
    }
}
