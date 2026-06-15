using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.Constraints;
using BepuPhysics.Trees;
using BepuUtilities;
using BepuUtilities.Memory;
using static BallisticEngine.Bepu.BepuMath;
using BepuMesh = BepuPhysics.Collidables.Mesh;
using TkVector3 = System.Numerics.Vector3;   // engine math is System.Numerics now (was OpenTK)

namespace BallisticEngine.Bepu;

// IPhysicsWorld over BepuPhysics 2 (pure managed .NET, no native binaries). This module is the
// ONLY place in the repo allowed to reference BepuPhysics — everything engine-side goes through
// the Abstraction/Physics interfaces, mirroring how AssetPipeline owns Assimp/Stb/Magick.
//
// The Simulation is created lazily and torn down wholesale on Reset() (play-mode enter/leave);
// outstanding BepuBody wrappers are invalidated so stale component references become no-ops.
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

    // Wrappers and contact materials by handle value (bodies and statics have separate
    // handle spaces). Material reads happen from narrowphase worker threads, but nothing
    // mutates these during Step, so plain dictionaries are safe.
    readonly Dictionary<int, BepuBody> bodiesByHandle = new();
    readonly Dictionary<int, BepuBody> staticsByHandle = new();
    readonly Dictionary<int, ContactMaterial> bodyMaterials = new();
    readonly Dictionary<int, ContactMaterial> staticMaterials = new();

    // Per-compound-child trigger flags, indexed by COMPOUND CHILD INDEX (= CompoundBuilder add
    // order, 1:1 with the order children were added in TryBuildShape — NOT the description.Shapes
    // index, since unsupported parts are skipped). A single body can mix solid and trigger children;
    // the narrowphase consults this per child to decide solve-vs-overlap. Single-shape bodies get a
    // one-element array (childIndex 0). Read on worker threads; never mutated during Step.
    readonly Dictionary<int, bool[]> bodyChildTriggers = new();
    readonly Dictionary<int, bool[]> staticChildTriggers = new();

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

    // Maps an engine "bounciness" (0..1, Unity's coefficient of restitution) to a Bepu contact
    // spring DAMPING RATIO so the measured rebound energy ≈ bounciness² (rebound apex ≈ b²·drop).
    // Bepu's contact is a spring: damping ratio 1 = critically damped (no bounce), 0 = undamped
    // (full bounce). The relationship between damping ratio and the realized restitution is highly
    // non-linear at a fixed 30 Hz / 60 Hz-substepped solver, so this is an EMPIRICAL fit measured
    // by the P1 restitution harness (e:/tmp/bal-phys-overhaul/measure): a near-undamped spring is
    // needed before a 0.5 sphere rebounds meaningfully, and the curve is steep near the top. The
    // piecewise-power fit below was tuned so b ∈ {0,.3,.5,.7,.9,1} land within ±0.05 of b² rebound.
    // Restitution is applied at the VELOCITY level (see BepuContactTracker.ApplyRestitutionImpulse),
    // not through the contact spring. Bepu's spring-based bounce saturates near a ~0.1 rebound ratio
    // even fully undamped at this solver rate (measured: high frequency kills it, the substep budget
    // can't represent the spring's oscillation) — so the spring stays CRITICALLY DAMPED for rock-
    // solid resting/stacking, and a measured velocity-flip impulse on contact Enter provides the
    // real coefficient-of-restitution bounce. This keeps Bepu's stability guarantees AND gives a
    // true e²-energy rebound, which the spring model alone cannot.

    TkVector3 gravity = new(0f, -9.81f, 0f);
    internal Vector3 GravityNumerics = new(0f, -9.81f, 0f);

    public TkVector3 Gravity {
        get => gravity;
        set {
            gravity = value;
            GravityNumerics = ToNumerics(value);
        }
    }

    // Injected from the engine's LayerManager at bootstrap (see IPhysicsWorld). Read on narrowphase
    // worker threads; only mutated single-threaded between steps, so the field read is safe.
    public Func<int, int, bool> LayerCollisionMatrix { get; set; }

    // Consulted by the narrowphase before generating contacts. Null predicate = collide everything.
    internal bool LayersCollide(CollidableReference a, CollidableReference b) {
        Func<int, int, bool> matrix = LayerCollisionMatrix;
        if (matrix is null)
            return true;
        return matrix(GetMaterial(a).Layer, GetMaterial(b).Layer);
    }

    void EnsureSimulation() {
        // 4 substeps = 240Hz effective contact integration at the engine's 60Hz fixed step:
        // contact springs (incl. the Bounciness approximation) actually resolve instead of
        // sitting at the stability limit, and stacks stay solid with few velocity iterations.
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
        Contacts.Flush(); // narrowphase workers recorded contacts during the timestep
    }

    public void Reset() {
        foreach (BepuBody body in bodiesByHandle.Values)
            body.Invalidate();
        foreach (BepuBody body in staticsByHandle.Values)
            body.Invalidate();

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

    // ---- Bodies -------------------------------------------------------------

    public IPhysicsBody AddBody(in PhysicsBodyDescription description) {
        if (description.Shapes is null || description.Shapes.Length == 0)
            return null;

        EnsureSimulation();

        if (!TryBuildShape(in description, out TypedIndex shapeIndex, out BodyInertia inertia,
                out Vector3 centerOffset, out bool[] childTriggers))
            return null;

        // Compound shapes are recentered around their center of mass; the body pose must sit
        // there, while IPhysicsBody.Position keeps reporting the entity origin.
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
            // Continuous detection with a bounded speculative margin: a fast faller crosses
            // more than its own size per 60Hz step and tunnels straight through thin one-sided
            // meshes under speculative contacts alone. The sweep only runs when velocity
            // outpaces the margin (≳6 m/s here), so slow/resting bodies pay nothing.
            var collidable = new CollidableDescription(shapeIndex, 0.1f,
                ContinuousDetection.Continuous(1e-3f, 1e-3f));

            // FreezeRotation: zero the inverse inertia tensor so NO torque can rotate the body —
            // the standard way to build an upright character capsule. Without it a freely-rotating
            // capsule converts the tiniest contact asymmetry into a roll, and friction turns that
            // roll into a phantom sideways drift (a resting player slides off on its own). Mass
            // (inverse mass) is untouched, so it still falls and responds to linear forces.
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

        Contacts.OnBodyRemoved(bepuBody); // queue Exits for anything it was touching

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

        // Shapes are per-body in this engine (never shared), so dispose with the body.
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

    // Is child `childIndex` of this collidable a trigger? childIndex is the compound child index
    // Bepu hands the per-child narrowphase (= our build order). Out-of-range / missing → not a
    // trigger. A whole-body trigger (legacy PhysicsBodyDescription.IsTrigger) also makes every
    // child a trigger via the material flag, so callers OR this with GetMaterial(...).IsTrigger.
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

    // Pose of either a body or a static — used by the contact tracker on narrowphase worker
    // threads (read-only access to pose memory during collision detection is safe).
    internal RigidPose GetPose(CollidableReference collidable) =>
        collidable.Mobility == CollidableMobility.Static
            ? Simulation.Statics[collidable.StaticHandle].Pose
            : Simulation.Bodies[collidable.BodyHandle].Pose;

    // ---- Shapes -------------------------------------------------------------

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

        // A mesh is concave: legal only as the sole shape of a non-dynamic body.
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
            childTriggers = [parts[0].IsTrigger]; // single shape → child index 0
            return true;
        }

        if (single) {
            shapeIndex = AddConvex(parts[0].Shape, description.Mass, out inertia);
            childTriggers = [parts[0].IsTrigger];
            return shapeIndex.Exists;
        }

        // Multiple shapes or an offset single shape -> compound. Build the trigger flags in the SAME
        // order children are actually added (skipped parts don't get a compound child, so this stays
        // 1:1 with the compound's child index — the index Bepu hands the per-child narrowphase).
        var builder = new CompoundBuilder(bufferPool, Simulation.Shapes, parts.Length);
        var triggerList = new List<bool>(parts.Length);
        try {
            int added = 0;
            foreach (PhysicsShapePart part in parts) {
                var localPose = new RigidPose(ToNumerics(part.LocalPosition), ToNumerics(part.LocalRotation));
                // Weight children by volume so the compound's mass distribution follows the
                // geometry (CompoundBuilder normalizes weights into the total mass).
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
        // Bepu mesh triangles are ONE-SIDED, solid from the side OPPOSITE the right-handed
        // winding normal — the reverse of the engine's render convention (OpenGL CCW front
        // faces). Swap two indices so the RENDERED front face is the solid side, Unity-style:
        // the surface you can see is the surface that collides; backfaces pass through.
        // (Do NOT emit both windings to fake double-sided collision: a fast impact penetrates
        // slightly and the flipped triangle then ejects the body out the back.)
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

    // Bepu shapes reject zero/negative dimensions; clamp to a millimeter.
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

    // ---- Queries ------------------------------------------------------------

    struct ClosestRayHitHandler : IRayHitHandler {
        public float T;
        public Vector3 Normal;
        public CollidableReference Collidable;
        public bool Hit;
        public int LayerMask;
        public BepuPhysicsWorld World;

        // Filter by layer mask up front so excluded layers cost nothing past the broadphase.
        public bool AllowTest(CollidableReference collidable) =>
            LayerMask == ~0 || (LayerMask & (1 << World.GetMaterial(collidable).Layer)) != 0;
        public bool AllowTest(CollidableReference collidable, int childIndex) => true;

        public void OnRayHit(in RayData ray, ref float maximumT, float t, in Vector3 normal,
            CollidableReference collidable, int childIndex) {
            maximumT = t; // clip subsequent tests to the nearest hit so far
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

    // ---- Shape-cast (sweep) -------------------------------------------------

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
                maximumT = t; // clip the traversal to the nearest hit so far
            if (t >= T)
                return;
            T = t;
            Location = hitLocation;
            Normal = hitNormal;
            Collidable = collidable;
            Hit = true;
        }

        // Shape already overlapping at the start of the sweep: treat as a zero-distance hit.
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
        TkVector3 dir = direction / dirLen; // unit velocity → sweep T equals travel distance

        var pose = new RigidPose(ToNumerics(position), ToNumerics(rotation));
        var velocity = new BodyVelocity { Linear = ToNumerics(dir) };
        var handler = new ClosestSweepHitHandler { T = float.MaxValue, LayerMask = layerMask, World = this };

        // Bepu's Sweep is generic over the (unmanaged) convex shape type, so dispatch per kind.
        // Concave meshes are not valid sweep shapes — reject them like a dynamic mesh body.
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
        // Sweep returns the contact location too; prefer it when present (more accurate than the
        // shape-origin projection above for an off-axis touch).
        TkVector3 location = ToOpenTK(handler.Location);
        if (location.LengthSquared() > 0f)
            hit.Point = location;
        TkVector3 normal = ToOpenTK(handler.Normal);
        hit.Normal = normal.LengthSquared() > 0f ? normal.Normalized() : -dir;
        hit.Body = Lookup(handler.Collidable);
        return true;
    }

    // ---- Overlap queries ----------------------------------------------------

    // Broadphase AABB sweep collecting collidables whose bounds intersect the query box, layer- and
    // distance-filtered. v1 precision: the broadphase test is an AABB-vs-AABB overlap, then spheres
    // do a precise center-distance refine; box queries return the AABB-level set (conservative, like
    // Unity's OverlapBox NonAlloc fast path). Good for trigger/aggro/pickup volumes.
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

        // Gather the broadphase candidates, then refine: keep only bodies whose origin is within
        // radius (a cheap, slightly loose sphere test — exact shape-vs-sphere would need per-shape
        // closest-point math; this matches the conservative-then-refine contract above).
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

        // Rotated box -> its world AABB (the broadphase is AABB-based). Conservative for rotated
        // queries; v1 accepts the slop (documented).
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

    // ---- Precise overlap (narrowphase shape test) ---------------------------

    // Collects EVERY body the swept shape touches at (or very near) zero distance — i.e. true
    // overlaps with the query shape, not just AABB candidates. A near-zero-distance sweep reports
    // every initially-overlapping collidable through OnHit (t≈0) / OnHitAtZeroT; we keep all of them
    // (layer-filtered, deduplicated) instead of clipping to the nearest like a cast.
    struct OverlapSweepCollector : ISweepHitHandler {
        public BepuPhysicsWorld World;
        public int LayerMask;
        public List<IPhysicsBody> Results;

        public bool AllowTest(CollidableReference collidable) =>
            LayerMask == ~0 || (LayerMask & (1 << World.GetMaterial(collidable).Layer)) != 0;
        public bool AllowTest(CollidableReference collidable, int child) => true;

        const float OverlapEpsilon = 1e-3f; // a hit within this travel distance counts as an overlap

        void Add(CollidableReference collidable) {
            BepuBody body = World.Lookup(collidable);
            if (body is not null && !Results.Contains(body))
                Results.Add(body);
        }

        public void OnHit(ref float maximumT, float t, in Vector3 hitLocation, in Vector3 hitNormal,
            CollidableReference collidable) {
            // Do NOT lower maximumT — we want to keep visiting every overlapping leaf, not converge on
            // the nearest. Only count hits already touching at the start of the sweep.
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
        // A short sweep in an arbitrary direction; only the zero-distance (initial overlap) hits are
        // kept, so the direction is irrelevant. Velocity is unit so maxT is a distance.
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
