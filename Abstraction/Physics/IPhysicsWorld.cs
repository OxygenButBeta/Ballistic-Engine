
namespace BallisticEngine;

public enum PhysicsBodyType {
    Dynamic,
    Kinematic,
    Static,
}

public struct PhysicsBodyDescription {
    public PhysicsBodyType Type;
    public Vector3 Position;
    public Quaternion Rotation;
    public float Mass;                 // dynamic bodies only
    public float Friction;             // 0..2-ish, Unity-style coefficient
    public float Bounciness;           // 0..1; approximated by the backend's contact springs
    public bool FreezeRotation;        // dynamic bodies: lock all rotation (upright character capsule)
    public bool IsTrigger;             // overlaps are detected (contact events) but never solved
    public int Layer;                  // collision layer 0..31; pairs are filtered by the layer matrix
    public PhysicsShapePart[] Shapes;  // one or more; >1 (or any offset) becomes a compound
}

public enum PhysicsContactPhase {
    Enter, // the pair started touching this step
    Stay,  // still touching (one event per step per pair)
    Exit,  // stopped touching, separated, or one body was removed
}

// One contact event between two bodies, produced by Step (and body removal). Sleeping pairs go
// quiet WITHOUT an Exit — they resume Stay when woken, Unity-style.
public struct PhysicsContactEvent {
    public PhysicsContactPhase Phase;
    public IPhysicsBody A;
    public IPhysicsBody B;
    public Vector3 Point;    // world-space representative contact point (last known, for Exit)
    public Vector3 Normal;   // unit normal pointing from B toward A
    public bool IsTrigger;   // at least one side is a trigger; the overlap was not solved
}

public struct PhysicsRayHit {
    public Vector3 Point;
    public Vector3 Normal;
    public float Distance;
    public IPhysicsBody Body;
}

// The physics backend contract. Exactly one implementation is injected at bootstrap
// (Physics/Bepu); the Engine layer talks only through this so it stays free of
// BepuPhysics references, mirroring how rendering goes through RenderAsset/GraphicAPI.
public interface IPhysicsWorld {
    Vector3 Gravity { get; set; }

    // Layer collision predicate (layerA, layerB) -> do they collide. Injected at bootstrap from
    // the engine's LayerManager so the backend stays free of Engine references (same pattern as the
    // Falcor converter / scene loader delegates). Null = collide everything.
    Func<int, int, bool> LayerCollisionMatrix { get; set; }

    // Returns null (after logging) when the description can't be built — e.g. a dynamic mesh
    // shape. Callers must tolerate null, matching the engine's never-throw asset conventions.
    IPhysicsBody AddBody(in PhysicsBodyDescription description);

    void RemoveBody(IPhysicsBody body);

    void Step(float deltaTime);

    // Contact events produced by the most recent Step, in deterministic order. The list is
    // rewritten by the next Step/Reset — consume it immediately, do not hold the reference.
    IReadOnlyList<PhysicsContactEvent> ContactEvents { get; }

    // layerMask: only bodies whose Layer bit is set are tested (~0 = hit everything).
    bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, int layerMask, out PhysicsRayHit hit);

    // Shape-cast (sweep): slide a convex shape (sphere/box/capsule) from (position, rotation) along
    // `direction` for up to maxDistance, returning the first body it touches. Unlike a ray, this has
    // THICKNESS — it catches contacts a thin ray would miss (a wheel finding the ground, a character
    // probing a step). hit.Distance is how far the shape traveled before contact; Point/Normal are at
    // the touch. Mesh/concave shapes are not valid sweep shapes (convex only). layerMask as in Raycast.
    bool ShapeCast(PhysicsShape shape, Vector3 position, Quaternion rotation, Vector3 direction,
        float maxDistance, int layerMask, out PhysicsRayHit hit);

    // Overlap queries: collect every body whose shape intersects the volume and whose Layer is in
    // the mask. Results append to `results` (caller-cleared); returns the count. Used by
    // Physics.OverlapSphere/OverlapBox. Triggers are included (Unity parity).
    int OverlapSphere(Vector3 center, float radius, int layerMask, List<IPhysicsBody> results);
    int OverlapBox(Vector3 center, Vector3 halfExtents, Quaternion orientation, int layerMask,
        List<IPhysicsBody> results);

    // Drops every body and shape (leaving/entering play mode). Outstanding IPhysicsBody
    // references become inert no-ops.
    void Reset();
}
