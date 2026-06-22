
namespace BallisticEngine;

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

    internal IPhysicsBody InternalBody => body;

    readonly List<Collider> boundColliders = new(capacity: 4);
    Vector3 pendingForce;
    Vector3 pendingTorque;

    readonly List<(Vector3 Force, Vector3 Point)> pendingForcesAtPoint = new(capacity: 4);

    Vector3 syncedPosition;
    Quaternion syncedRotation;
    bool hasSyncedPose;

    internal Collider PrimaryCollider => boundColliders.Count > 0 ? boundColliders[0] : null;

    internal Collider ColliderForChild(int childIndex) =>
        childIndex >= 0 && childIndex < boundColliders.Count ? boundColliders[childIndex] : PrimaryCollider;

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

    public void AddForce(Vector3 force) => pendingForce += force;
    public void AddTorque(Vector3 torque) => pendingTorque += torque;

    public void AddForceAtPosition(Vector3 force, Vector3 worldPoint) =>
        pendingForcesAtPoint.Add((force, worldPoint));

    public void AddImpulse(Vector3 impulse) => body?.ApplyImpulse(impulse);
    public void AddImpulseAtPosition(Vector3 impulse, Vector3 worldPoint) =>
        body?.ApplyImpulse(impulse, worldPoint);
    public void AddAngularImpulse(Vector3 impulse) => body?.ApplyAngularImpulse(impulse);

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

        foreach (Behaviour behaviour in entity.Behaviours)
            TryAddCollider(behaviour as Collider, worldScale, Vector3.Zero, Quaternion.Identity, parts);

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

        collider.ReleaseStaticBodyForRigidbody();

        Vector3 center = localPosition + Vector3.Transform(collider.Center * worldScale, localRotation);
        parts.Add(new PhysicsShapePart(shape, center, localRotation, collider.IsTrigger));
        boundColliders.Add(collider);
    }

    void GatherChildColliders(Entity root, Vector3 worldScale, List<PhysicsShapePart> parts) {
        Matrix4 invRoot = transform.WorldMatrix.Inverted();
        GatherFrom(root);

        void GatherFrom(Entity parent) {
            foreach (Entity child in parent.DirectChildren()) {
                if (!child.IsActiveInHierarchy)
                    continue;
                if (child.GetComponent<Rigidbody>() is not null)
                    continue;

                Matrix4 relative = child.transform.WorldMatrix * invRoot;
                Vector3 localPosition = relative.ExtractTranslation();
                Quaternion localRotation = Quaternion.Normalize(
                    Quaternion.CreateFromRotationMatrix(relative));

                foreach (Behaviour behaviour in child.Behaviours)
                    TryAddCollider(behaviour as Collider, worldScale, localPosition, localRotation, parts);

                GatherFrom(child);
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

    internal void PrePhysicsStep(float dt) {
        if (body is null)
            return;

        if (IsKinematic) {
            DriveKinematicTowardTransform(dt);
            return;
        }

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
                body.ApplyImpulse(force * dt, point);
            pendingForcesAtPoint.Clear();
        }

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
        RefreshSyncedPose();
    }

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
        if (delta.W < 0f) delta = new Quaternion(-delta.X, -delta.Y, -delta.Z, -delta.W);

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
