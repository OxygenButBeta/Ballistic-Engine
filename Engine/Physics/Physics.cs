
namespace BallisticEngine;

public struct RaycastHit {
    public Vector3 Point;
    public Vector3 Normal;
    public float Distance;
    public Collider Collider;
    public Rigidbody Rigidbody;
    public Entity Entity;
}

public static class Physics {
    public static IPhysicsWorld World { get; set; }

    public static float FixedTimestep { get; set; } = 1f / 60f;

    public static Vector3 Gravity {
        get => World?.Gravity ?? new Vector3(0f, -9.81f, 0f);
        set {
            if (World is not null)
                World.Gravity = value;
        }
    }

    public const int DefaultRaycastLayers = ~(1 << 2);

    public static bool Raycast(Vector3 origin, Vector3 direction, out RaycastHit hit,
        float maxDistance = float.MaxValue, int layerMask = DefaultRaycastLayers) {
        hit = default;
        if (World is null || direction == Vector3.Zero)
            return false;

        if (!World.Raycast(origin, direction.Normalized(), maxDistance, layerMask, out PhysicsRayHit rawHit))
            return false;

        hit = MapHit(rawHit);
        return true;
    }

    public static bool Raycast(Vector3 origin, Vector3 direction,
        float maxDistance = float.MaxValue, int layerMask = DefaultRaycastLayers) =>
        Raycast(origin, direction, out _, maxDistance, layerMask);

    public static bool SphereCast(Vector3 origin, float radius, Vector3 direction, out RaycastHit hit,
        float maxDistance = float.MaxValue, int layerMask = DefaultRaycastLayers) =>
        ShapeCast(new SphereShape(radius), origin, Quaternion.Identity, direction, out hit, maxDistance, layerMask);

    public static bool BoxCast(Vector3 center, Vector3 halfExtents, Vector3 direction, Quaternion orientation,
        out RaycastHit hit, float maxDistance = float.MaxValue, int layerMask = DefaultRaycastLayers) =>
        ShapeCast(new BoxShape(halfExtents * 2f), center, orientation, direction, out hit, maxDistance, layerMask);

    public static bool CapsuleCast(Vector3 origin, float radius, float height, Vector3 direction,
        Quaternion orientation, out RaycastHit hit, float maxDistance = float.MaxValue,
        int layerMask = DefaultRaycastLayers) =>
        ShapeCast(new CapsuleShape(radius, MathF.Max(0f, height - 2f * radius)), origin, orientation,
            direction, out hit, maxDistance, layerMask);

    static bool ShapeCast(PhysicsShape shape, Vector3 position, Quaternion rotation, Vector3 direction,
        out RaycastHit hit, float maxDistance, int layerMask) {
        hit = default;
        if (World is null || direction == Vector3.Zero)
            return false;
        if (!World.ShapeCast(shape, position, rotation, direction.Normalized(), maxDistance, layerMask,
                out PhysicsRayHit rawHit))
            return false;
        hit = MapHit(rawHit);
        return true;
    }

    public static List<Collider> OverlapSphere(Vector3 center, float radius, int layerMask = ~0) {
        var bodies = new List<IPhysicsBody>();
        World?.OverlapSphere(center, radius, layerMask, bodies);
        return ToColliders(bodies);
    }

    public static List<Collider> OverlapBox(Vector3 center, Vector3 halfExtents,
        Quaternion orientation, int layerMask = ~0) {
        var bodies = new List<IPhysicsBody>();
        World?.OverlapBox(center, halfExtents, orientation, layerMask, bodies);
        return ToColliders(bodies);
    }

    public static List<Collider> OverlapSpherePrecise(Vector3 center, float radius, int layerMask = ~0) =>
        OverlapShape(new SphereShape(radius), center, Quaternion.Identity, layerMask);

    public static List<Collider> OverlapBoxPrecise(Vector3 center, Vector3 halfExtents,
        Quaternion orientation, int layerMask = ~0) =>
        OverlapShape(new BoxShape(halfExtents * 2f), center, orientation, layerMask);

    static List<Collider> OverlapShape(PhysicsShape shape, Vector3 position, Quaternion rotation,
        int layerMask) {
        var bodies = new List<IPhysicsBody>();
        World?.OverlapShape(shape, position, rotation, layerMask, bodies);
        return ToColliders(bodies);
    }

    static List<Collider> ToColliders(List<IPhysicsBody> bodies) {
        var result = new List<Collider>(bodies.Count);
        foreach (IPhysicsBody body in bodies) {
            var owner = body.UserData as Behaviour;
            Collider collider = owner as Collider ?? (owner as Rigidbody)?.PrimaryCollider;
            if (collider is not null && !result.Contains(collider))
                result.Add(collider);
        }
        return result;
    }

    static RaycastHit MapHit(in PhysicsRayHit rawHit) {
        var hit = new RaycastHit {
            Point = rawHit.Point,
            Normal = rawHit.Normal,
            Distance = rawHit.Distance,
        };
        var owner = rawHit.Body?.UserData as Behaviour;
        hit.Collider = owner as Collider;
        hit.Rigidbody = owner as Rigidbody ?? hit.Collider?.AttachedRigidbody;
        hit.Entity = owner?.Entity;
        return hit;
    }

    const int MaxCatchUpSteps = 4;
    const float MaxFrameDelta = 0.25f;

    static float accumulator;

    internal static void BeginPlay() {
        accumulator = 0f;
        World?.Reset();
    }

    internal static void EndPlay() => World?.Reset();

    internal static void Advance(float delta, Action<float> fixedTick) {
        if (World is null)
            return;

        accumulator += MathF.Min(delta, MaxFrameDelta);
        int steps = 0;
        while (accumulator >= FixedTimestep) {
            if (steps++ >= MaxCatchUpSteps) {
                accumulator %= FixedTimestep;
                break;
            }

            Coroutine.FixedTick(FixedTimestep);

            fixedTick?.Invoke(FixedTimestep);

            foreach (Rigidbody rigidbody in RuntimeSet<Rigidbody>.ReadOnlyCollection)
                rigidbody.PrePhysicsStep(FixedTimestep);

            foreach (Collider collider in RuntimeSet<Collider>.ReadOnlyCollection)
                collider.SyncStaticBodyToTransform();

            World.Step(FixedTimestep);

            foreach (Rigidbody rigidbody in RuntimeSet<Rigidbody>.ReadOnlyCollection)
                rigidbody.PostPhysicsStep();

            DispatchContactEvents();

            accumulator -= FixedTimestep;
        }
    }

    static void DispatchContactEvents() {
        IReadOnlyList<PhysicsContactEvent> events = World.ContactEvents;
        if (events is null || events.Count == 0)
            return;

        for (var i = 0; i < events.Count; i++) {
            PhysicsContactEvent contactEvent = events[i];
            var ownerA = contactEvent.A?.UserData as Behaviour;
            var ownerB = contactEvent.B?.UserData as Behaviour;
            DispatchToEntity(ownerA, ownerB, in contactEvent, contactEvent.Normal, contactEvent.ChildB);
            DispatchToEntity(ownerB, ownerA, in contactEvent, -contactEvent.Normal, contactEvent.ChildA);
        }
    }

    static void DispatchToEntity(Behaviour receiver, Behaviour other, in PhysicsContactEvent contactEvent,
        Vector3 normalTowardReceiver, int otherChildIndex) {
        Entity entity = receiver?.Entity;
        if (entity is null)
            return;

        Collider otherCollider = other as Collider
                                 ?? (other as Rigidbody)?.ColliderForChild(otherChildIndex);
        var collision = new Collision(
            otherCollider,
            other as Rigidbody ?? otherCollider?.AttachedRigidbody,
            other?.Entity,
            contactEvent.Point,
            normalTowardReceiver);

        List<Behaviour> behaviours = entity.Behaviours;
        for (var i = 0; i < behaviours.Count; i++) {
            Behaviour behaviour = behaviours[i];
            if (!behaviour.IsEnabled)
                continue;
            try {
                if (contactEvent.IsTrigger) {
                    switch (contactEvent.Phase) {
                        case PhysicsContactPhase.Enter: behaviour.OnTriggerEnter(otherCollider); break;
                        case PhysicsContactPhase.Stay: behaviour.OnTriggerStay(otherCollider); break;
                        case PhysicsContactPhase.Exit: behaviour.OnTriggerExit(otherCollider); break;
                    }
                }
                else {
                    switch (contactEvent.Phase) {
                        case PhysicsContactPhase.Enter: behaviour.OnCollisionEnter(collision); break;
                        case PhysicsContactPhase.Stay: behaviour.OnCollisionStay(collision); break;
                        case PhysicsContactPhase.Exit: behaviour.OnCollisionExit(collision); break;
                    }
                }
            }
            catch (Exception exception) {
                Debugging.LogError(
                    $"{behaviour.GetType().Name}.On{(contactEvent.IsTrigger ? "Trigger" : "Collision")}{contactEvent.Phase} threw: {exception}");
            }
        }
    }
}
