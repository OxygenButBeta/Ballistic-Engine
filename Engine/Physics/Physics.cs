
namespace BallisticEngine;

// What a Physics.Raycast hit, mapped back to scene objects (Unity's RaycastHit).
public struct RaycastHit {
    public Vector3 Point;
    public Vector3 Normal;
    public float Distance;
    public Collider Collider;     // null when the body is a collider-less Rigidbody
    public Rigidbody Rigidbody;   // null when the hit body is a static collider
    public Entity Entity;
}

// Static facade over the physics backend (Unity's `Physics` class). The world implementation
// is injected by EngineBootstrap (the Physics/Bepu module); components talk ONLY through
// IPhysicsWorld/IPhysicsBody so the Engine layer stays free of BepuPhysics references.
//
// Simulation runs at a fixed timestep, in play mode only, driven by SceneManager.Update:
// behaviours' FixedTick fires, rigidbodies push pending forces/kinematic targets into the
// world, the world steps, and simulated poses write back to transforms.
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

    // The "Ignore Raycast" builtin layer (index 2, Unity parity) is excluded from the default mask
    // so a collider on it is invisible to unmasked raycasts.
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

    // Convenience overload: no out-hit, just "did it hit anything".
    public static bool Raycast(Vector3 origin, Vector3 direction,
        float maxDistance = float.MaxValue, int layerMask = DefaultRaycastLayers) =>
        Raycast(origin, direction, out _, maxDistance, layerMask);

    // ---- Shape casts (sweeps) ------------------------------------------------
    // Like a ray, but with thickness: slides a convex shape and returns the first body it touches.
    // Used for robust ground-finding (vehicle wheels), character step/ledge probing, and any "what's
    // in front of this shape" query a zero-width ray would miss. Unity's SphereCast/BoxCast/CapsuleCast.

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

    // Every body intersecting a sphere, layer-filtered (Unity's OverlapSphere). Returns colliders.
    public static List<Collider> OverlapSphere(Vector3 center, float radius, int layerMask = ~0) {
        var bodies = new List<IPhysicsBody>();
        World?.OverlapSphere(center, radius, layerMask, bodies);
        return ToColliders(bodies);
    }

    // Every body intersecting an oriented box (Unity's OverlapBox). halfExtents = box size / 2.
    public static List<Collider> OverlapBox(Vector3 center, Vector3 halfExtents,
        Quaternion orientation, int layerMask = ~0) {
        var bodies = new List<IPhysicsBody>();
        World?.OverlapBox(center, halfExtents, orientation, layerMask, bodies);
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

    // ---- Stepping (engine-internal) -----------------------------------------

    // After a long stall (asset import hitch, breakpoint) the accumulator could demand dozens
    // of catch-up steps; cap them and drop the remaining debt instead of freezing the frame.
    const int MaxCatchUpSteps = 4;
    const float MaxFrameDelta = 0.25f;

    static float accumulator;

    internal static void BeginPlay() {
        accumulator = 0f;
        World?.Reset();
    }

    internal static void EndPlay() => World?.Reset();

    // Runs zero or more fixed steps for this frame. fixedTick fans FixedTick out to the
    // scene's behaviours BEFORE each physics step, so gameplay code sees pre-step state and
    // its forces apply in the same step (Unity's FixedUpdate ordering).
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

            // Resume WaitForFixedTick awaiters before behaviour FixedTick + the physics step, so an
            // awaited fixed-step resume sees the same pre-step state FixedTick does.
            Coroutine.FixedTick(FixedTimestep);

            fixedTick?.Invoke(FixedTimestep);

            foreach (Rigidbody rigidbody in RuntimeSet<Rigidbody>.ReadOnlyCollection)
                rigidbody.PrePhysicsStep(FixedTimestep);

            World.Step(FixedTimestep);

            foreach (Rigidbody rigidbody in RuntimeSet<Rigidbody>.ReadOnlyCollection)
                rigidbody.PostPhysicsStep();

            DispatchContactEvents();

            accumulator -= FixedTimestep;
        }
    }

    // ---- Contact event dispatch ----------------------------------------------

    // Fans the backend's per-step contact events out to OnCollision*/OnTrigger* on every
    // enabled behaviour of BOTH entities involved. Runs after PostPhysicsStep so callbacks
    // see post-step transforms (and may safely add forces, destroy entities, etc.).
    static void DispatchContactEvents() {
        IReadOnlyList<PhysicsContactEvent> events = World.ContactEvents;
        if (events is null || events.Count == 0)
            return;

        for (var i = 0; i < events.Count; i++) {
            PhysicsContactEvent contactEvent = events[i];
            var ownerA = contactEvent.A?.UserData as Behaviour;
            var ownerB = contactEvent.B?.UserData as Behaviour;
            DispatchToEntity(ownerA, ownerB, in contactEvent, contactEvent.Normal);
            DispatchToEntity(ownerB, ownerA, in contactEvent, -contactEvent.Normal);
        }
    }

    static void DispatchToEntity(Behaviour receiver, Behaviour other, in PhysicsContactEvent contactEvent,
        Vector3 normalTowardReceiver) {
        Entity entity = receiver?.Entity;
        if (entity is null)
            return;

        Collider otherCollider = other as Collider ?? (other as Rigidbody)?.PrimaryCollider;
        var collision = new Collision(
            otherCollider,
            other as Rigidbody ?? otherCollider?.AttachedRigidbody,
            other?.Entity,
            contactEvent.Point,
            normalTowardReceiver);

        // Index loop: a callback may AddComponent (appends are picked up; removals at worst
        // skip one entry this step). Exceptions are contained per behaviour — one broken
        // script must not kill the physics loop (engine never-throw convention).
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
