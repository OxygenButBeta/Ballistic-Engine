
namespace BallisticEngine;

// Makes the entity physically simulated (Unity's Rigidbody). Builds one body from all enabled
// colliders on THIS entity (multiple colliders become a compound; child entities do not
// contribute in v1). With no collider, it simulates as a small sphere so it can still fall.
//
// Bodies exist only in play mode: created in OnEnabled (Scene.FireBegin / AddComponent during
// play / SetActive), destroyed in OnDisabled/OnDetach. While simulating, a dynamic body OWNS
// the transform — edits to transform.Position are overwritten each step; use Teleport,
// Velocity or the force APIs instead. Kinematic bodies are the reverse: they chase the
// transform with computed velocities so they push dynamic bodies correctly.
[Component("Rigidbody", "Physics")]
public class Rigidbody : Behaviour {
    [Header("Body")]
    [Range(0.001f, 10000f)]
    public float Mass { get; set; } = 1f;

    [Tooltip("Kinematic bodies are moved by their transform (animation/code), are immune to forces, and push dynamic bodies.")]
    public bool IsKinematic { get; set; }

    [Tooltip("Lock all rotation so the body can't tip or roll (upright character capsules). Torques are ignored; it still falls and slides.")]
    public bool FreezeRotation { get; set; }

    public bool UseGravity { get; set; } = true;

    [Tooltip("Drag on linear velocity, per second. 0 = none.")]
    [Range(0f, 10f)]
    public float LinearDamping { get; set; }

    [Tooltip("Drag on angular velocity, per second.")]
    [Range(0f, 10f)]
    public float AngularDamping { get; set; } = 0.05f;

    IPhysicsBody body;
    readonly List<Collider> boundColliders = new(capacity: 4);
    Vector3 pendingForce;
    Vector3 pendingTorque;
    // Forces applied at a world point (wheel suspension, thrusters): kept as (force, point) pairs and
    // integrated as point impulses next fixed step, so they produce the right linear AND angular push.
    readonly List<(Vector3 Force, Vector3 Point)> pendingForcesAtPoint = new(capacity: 4);

    // The transform pose as of the last physics sync, re-read THROUGH the transform so the
    // next pre-step's comparison hits the same float path (untouched transform == bitwise
    // match). A mismatch means something else moved the transform — editor gizmo, inspector,
    // or a script writing transform.Position — and the body teleports to honor it (Unity
    // parity). Caveat: a dynamic body parented under a moving parent degenerates into
    // teleport-following; don't nest rigidbodies under animated transforms.
    Vector3 syncedPosition;
    Quaternion syncedRotation;
    bool hasSyncedPose;

    // The collider reported as "the other collider" when a contact event can't pin down the exact
    // compound child (solid contacts: Bepu's reduced manifold doesn't carry a reliable child index).
    internal Collider PrimaryCollider => boundColliders.Count > 0 ? boundColliders[0] : null;

    // Resolve a compound child index (from a contact event) back to the originating collider.
    // boundColliders is built in the same order children are added to the compound (P5), so the
    // index lines up. childIndex < 0 (unknown — solid top-level path) falls back to the primary.
    internal Collider ColliderForChild(int childIndex) =>
        childIndex >= 0 && childIndex < boundColliders.Count ? boundColliders[childIndex] : PrimaryCollider;

    // ---- Runtime API (play mode; harmless defaults/no-ops in edit mode) -----

    [NotSerialized]
    public Vector3 Velocity {
        get => body?.LinearVelocity ?? Vector3.Zero;
        set {
            if (body is null)
                return;
            body.WakeUp();
            body.LinearVelocity = value;
        }
    }

    [NotSerialized]
    public Vector3 AngularVelocity {
        get => body?.AngularVelocity ?? Vector3.Zero;
        set {
            if (body is null)
                return;
            body.WakeUp();
            body.AngularVelocity = value;
        }
    }

    // Continuous force/torque in newtons; accumulated and applied over the next fixed step(s).
    public void AddForce(Vector3 force) => pendingForce += force;
    public void AddTorque(Vector3 torque) => pendingTorque += torque;

    // Continuous force in newtons applied at a WORLD point — produces both linear acceleration and a
    // torque about the center of mass (an off-center push spins the body). Accumulated and integrated
    // next fixed step. This is the workhorse for raycast vehicle suspension/grip and thrusters.
    public void AddForceAtPosition(Vector3 force, Vector3 worldPoint) =>
        pendingForcesAtPoint.Add((force, worldPoint));

    // Instantaneous impulses (kg·m/s).
    public void AddImpulse(Vector3 impulse) => body?.ApplyImpulse(impulse);
    public void AddImpulseAtPosition(Vector3 impulse, Vector3 worldPoint) =>
        body?.ApplyImpulse(impulse, worldPoint);
    public void AddAngularImpulse(Vector3 impulse) => body?.ApplyAngularImpulse(impulse);

    // Moves a DYNAMIC body instantly. (Writing the transform directly also works — the next
    // fixed step detects it and teleports — but this variant is explicit and immediate.)
    public void Teleport(Vector3 position, Quaternion rotation) {
        transform.WorldPosition = position;
        transform.WorldRotation = rotation;
        if (body is null)
            return;
        body.Position = position;
        body.Rotation = rotation;
        body.WakeUp();
        RefreshSyncedPose();
    }

    // ---- Lifecycle -----------------------------------------------------------

    protected internal override void OnAttach() {
        if (!RuntimeSet<Rigidbody>.Contains(this))
            RuntimeSet<Rigidbody>.Add(this);
    }

    protected internal override void OnDetach() {
        RuntimeSet<Rigidbody>.Remove(this);
        DestroyBody();
    }

    protected internal override void OnEnabled() {
        if (SceneManager.IsPlaying)
            CreateBody();
    }

    protected internal override void OnDisabled() => DestroyBody();

    void CreateBody() {
        if (body is not null || Physics.World is null)
            return;

        boundColliders.Clear();
        pendingForce = Vector3.Zero;
        pendingTorque = Vector3.Zero;
        pendingForcesAtPoint.Clear();

        Vector3 worldScale = transform.WorldMatrix.ExtractScale();
        var parts = new List<PhysicsShapePart>(capacity: 4);

        // Colliders on THIS entity sit at the body origin (no relative offset beyond their Center).
        foreach (Behaviour behaviour in entity.Behaviours)
            TryAddCollider(behaviour as Collider, worldScale, Vector3.Zero, Quaternion.Identity, parts);

        // P5: colliders on CHILD entities also contribute, posed relative to the body root — so a car
        // chassis can carry bumper/roof colliders on child transforms, a character its limb colliders.
        // The walk stops at any nested Rigidbody (that child owns its own body). The compound child
        // index lines up with boundColliders order, so contact events resolve to the right collider.
        GatherChildColliders(entity, worldScale, parts);

        if (parts.Count == 0) {
            Debugging.LogWarning(
                $"Physics: Rigidbody on '{entity.Name}' has no usable collider; simulating as a 0.1m sphere.");
            parts.Add(new PhysicsShapePart(new SphereShape(0.1f), Vector3.Zero, Quaternion.Identity));
        }

        Collider material = boundColliders.Count > 0 ? boundColliders[0] : null;
        var description = new PhysicsBodyDescription {
            Type = IsKinematic ? PhysicsBodyType.Kinematic : PhysicsBodyType.Dynamic,
            Position = transform.WorldPosition,
            Rotation = transform.WorldRotation,
            Mass = Mass,
            Friction = material?.Friction ?? 0.6f,
            Bounciness = material?.Bounciness ?? 0f,
            FreezeRotation = FreezeRotation,
            // Per-collider triggers handle mixed bodies now; the body-wide flag is set only when
            // EVERY collider is a trigger (a pure trigger volume — slightly cheaper, same result).
            IsTrigger = boundColliders.Count > 0 && boundColliders.TrueForAll(c => c.IsTrigger),
            Layer = entity.Layer,
            Shapes = parts.ToArray(),
        };

        body = Physics.World.AddBody(in description);
        if (body is not null) {
            body.UserData = this;
            RefreshSyncedPose();
        }
    }

    // Adds one collider to the compound at a local pose (relative to the body root). localPosition/
    // localRotation place a CHILD-entity collider; for same-entity colliders both are identity and
    // only the collider's own Center offsets it. Appends to boundColliders so the compound child
    // index (= add order) maps back to the originating collider for contact resolution.
    void TryAddCollider(Collider collider, Vector3 worldScale, Vector3 localPosition,
        Quaternion localRotation, List<PhysicsShapePart> parts) {
        if (collider is null || !collider.IsEnabled)
            return;
        if (!collider.ValidForDynamic) {
            Debugging.LogWarning(
                $"Physics: {collider.GetType().Name} on '{collider.Entity.Name}' is static-only and is ignored by its Rigidbody.");
            return;
        }

        PhysicsShape shape = collider.BuildShape(worldScale);
        if (shape is null)
            return;

        // Drop any standalone static body the collider made before this Rigidbody existed, so it
        // doesn't overlap our dynamic body and eject on the first step.
        collider.ReleaseStaticBodyForRigidbody();

        // The collider's own Center is in its local space; rotate it into the body frame and add the
        // child's relative position. Trigger state is per-collider (P4).
        Vector3 center = localPosition + Vector3.Transform(collider.Center * worldScale, localRotation);
        parts.Add(new PhysicsShapePart(shape, center, localRotation, collider.IsTrigger));
        boundColliders.Add(collider);
    }

    // Recursively gathers colliders from child entities, posed relative to the body root. Stops at any
    // child that has its own Rigidbody (that subtree is a separate body). The relative pose is the
    // child's world transform expressed in the root's frame: inverse(rootWorld) * childWorld.
    void GatherChildColliders(Entity root, Vector3 worldScale, List<PhysicsShapePart> parts) {
        Matrix4 invRoot = transform.WorldMatrix.Inverted();
        GatherFrom(root);

        void GatherFrom(Entity parent) {
            foreach (Entity child in parent.DirectChildren()) {
                if (!child.IsActiveInHierarchy)
                    continue;
                // A child with its own Rigidbody owns its body; don't fold its colliders into ours.
                if (child.GetComponent<Rigidbody>() is not null)
                    continue;

                Matrix4 relative = child.transform.WorldMatrix * invRoot;
                Vector3 localPosition = relative.ExtractTranslation();
                Quaternion localRotation = Quaternion.Normalize(
                    Quaternion.CreateFromRotationMatrix(relative));

                foreach (Behaviour behaviour in child.Behaviours)
                    TryAddCollider(behaviour as Collider, worldScale, localPosition, localRotation, parts);

                GatherFrom(child); // recurse deeper (grandchildren), still stopping at nested bodies
            }
        }
    }

    void DestroyBody() {
        if (body is null)
            return;
        Physics.World?.RemoveBody(body);
        body = null;
        hasSyncedPose = false;
        boundColliders.Clear();
    }

    void RefreshSyncedPose() {
        syncedPosition = transform.WorldPosition;
        syncedRotation = transform.WorldRotation;
        hasSyncedPose = true;
    }

    // A collider on this entity was enabled/disabled/added during play: rebuild the body with
    // the current collider set, preserving motion. No-op when the set didn't actually change
    // (e.g. the redundant notify during Scene.FireBegin ordering).
    internal void NotifyColliderChanged() {
        if (!SceneManager.IsPlaying || body is null)
            return;

        var current = new List<Collider>(capacity: 4);
        foreach (Behaviour behaviour in entity.Behaviours)
            if (behaviour is Collider { IsEnabled: true, ValidForDynamic: true } collider)
                current.Add(collider);
        if (current.SequenceEqual(boundColliders))
            return;

        Vector3 linear = body.LinearVelocity;
        Vector3 angular = body.AngularVelocity;
        DestroyBody();
        CreateBody();
        if (body is null)
            return;
        body.LinearVelocity = linear;
        body.AngularVelocity = angular;
    }

    // ---- Fixed-step hooks (called by Physics.Advance) -------------------------

    internal void PrePhysicsStep(float dt) {
        if (body is null)
            return;

        if (IsKinematic) {
            DriveKinematicTowardTransform(dt);
            return;
        }

        // External transform edits teleport the body (Unity parity): without this, anything
        // written to the transform between steps — gizmo drag, inspector field, a script
        // setting transform.Position — would be silently overwritten by PostPhysicsStep.
        // Runs BEFORE the IsAwake early-out so dragging a sleeping body wakes it.
        if (hasSyncedPose) {
            Vector3 editedPosition = transform.WorldPosition;
            Quaternion editedRotation = transform.WorldRotation;
            if (editedPosition != syncedPosition || editedRotation != syncedRotation) {
                body.Position = editedPosition;
                body.Rotation = editedRotation;
                body.WakeUp();
            }
        }

        if (pendingForce != Vector3.Zero) {
            body.ApplyImpulse(pendingForce * dt);
            pendingForce = Vector3.Zero;
        }
        if (pendingTorque != Vector3.Zero) {
            body.ApplyAngularImpulse(pendingTorque * dt);
            pendingTorque = Vector3.Zero;
        }
        if (pendingForcesAtPoint.Count > 0) {
            foreach ((Vector3 force, Vector3 point) in pendingForcesAtPoint)
                body.ApplyImpulse(force * dt, point); // point impulse → linear + torque about COM
            pendingForcesAtPoint.Clear();
        }

        // Maintenance only below — never wake a sleeping body for it. (Sleeping bodies are
        // also excluded from gravity integration, so there is nothing to cancel.)
        if (!body.IsAwake)
            return;
        if (!UseGravity)
            body.LinearVelocity -= Physics.Gravity * dt;
        if (LinearDamping > 0f)
            body.LinearVelocity *= 1f / (1f + dt * LinearDamping);
        if (AngularDamping > 0f)
            body.AngularVelocity *= 1f / (1f + dt * AngularDamping);
    }

    internal void PostPhysicsStep() {
        if (body is null || IsKinematic)
            return;
        transform.WorldPosition = body.Position;
        transform.WorldRotation = body.Rotation;
        RefreshSyncedPose(); // re-read so the next pre-step's comparison is bitwise-stable
    }

    // Kinematic bodies chase the transform with exact velocities (x + v*dt lands on the
    // target) instead of teleporting, so contacts against dynamic bodies resolve with real
    // relative velocity — a teleported kinematic platform would not push anything.
    void DriveKinematicTowardTransform(float dt) {
        Vector3 targetPosition = transform.WorldPosition;
        Quaternion targetRotation = transform.WorldRotation;

        Vector3 deltaPosition = targetPosition - body.Position;
        Vector3 angularVelocity = AngularVelocityBetween(body.Rotation, targetRotation, dt);

        bool moved = deltaPosition.LengthSquared() > 1e-12f || angularVelocity.LengthSquared() > 1e-12f;
        if (!moved && !body.IsAwake)
            return;

        if (moved)
            body.WakeUp();
        body.LinearVelocity = deltaPosition / dt;
        body.AngularVelocity = angularVelocity;
    }

    static Vector3 AngularVelocityBetween(Quaternion from, Quaternion to, float dt) {
        Quaternion delta = to * Quaternion.Inverse(from);
        if (delta.W < 0f) // shortest arc
            delta = new Quaternion(-delta.X, -delta.Y, -delta.Z, -delta.W);

        // Extract axis/angle from the (shortest-arc) quaternion: angle = 2*acos(W),
        // axis = (X,Y,Z) / sin(angle/2).
        delta = Quaternion.Normalize(delta);
        float w = MathHelper.Clamp(delta.W, -1f, 1f);
        float angle = 2f * MathF.Acos(w);
        float sinHalf = MathF.Sqrt(MathF.Max(0f, 1f - w * w));
        Vector3 axis = sinHalf > 1e-6f
            ? new Vector3(delta.X, delta.Y, delta.Z) / sinHalf
            : Vector3.UnitX;
        if (angle < 1e-6f || float.IsNaN(axis.X))
            return Vector3.Zero;
        return axis * (angle / dt);
    }
}
