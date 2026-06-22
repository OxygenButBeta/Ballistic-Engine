
namespace BallisticEngine;

public abstract class Collider : Behaviour {
    [Tooltip("Local offset of the shape from the entity origin.")]
    public Vector3 Center { get; set; } = Vector3.Zero;

    [Header("Material")]
    [Range(0f, 2f)]
    public float Friction { get; set; } = 0.6f;

    [Tooltip("0 = no bounce, 1 = very bouncy. Approximate — contact springs, not true restitution.")]
    [Range(0f, 1f)]
    public float Bounciness { get; set; }

    [Tooltip("Trigger colliders detect overlaps (OnTrigger* callbacks) but don't physically collide. Applied when the body is created (play start).")]
    public bool IsTrigger { get; set; }

    IPhysicsBody staticBody;

    Vector3 syncedPosition;
    Quaternion syncedRotation;
    bool hasSyncedPose;

    public Rigidbody AttachedRigidbody => entity?.GetComponent<Rigidbody>();

    internal abstract PhysicsShape BuildShape(Vector3 worldScale);

    internal virtual bool ValidForDynamic => true;

    protected internal override void OnAttach() => AutoFitToRenderMesh();

    private protected virtual void AutoFitToRenderMesh() {
    }

    private protected bool TryGetRenderMeshBounds(out Vector3 boundsMin, out Vector3 boundsMax) {
        boundsMin = default;
        boundsMax = default;
        if (entity?.GetComponent<StaticMeshRenderer>() is not { } renderer ||
            renderer.SharedMesh is not { } mesh || mesh.Vertices.Length == 0)
            return false;

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        int subMeshIndex = renderer.SubMeshIndex;
        if (subMeshIndex >= 0 && subMeshIndex < mesh.SubMeshes.Length) {
            SubMeshData subMesh = mesh.SubMeshes[subMeshIndex];
            if (subMesh.IndexCount == 0)
                return false;
            Matrix4 inverseNode = mesh.InverseNodeTransforms[subMeshIndex];
            for (int i = 0; i < subMesh.IndexCount; i++) {
                Vector3 v = Vector3.Transform(
                    mesh.Vertices[mesh.Indices[subMesh.IndexStart + i]], inverseNode);
                min = Vector3.Min(min, v);
                max = Vector3.Max(max, v);
            }
        }
        else {
            foreach (Vector3 v in mesh.Vertices) {
                min = Vector3.Min(min, v);
                max = Vector3.Max(max, v);
            }
        }

        boundsMin = min;
        boundsMax = max;
        return true;
    }

    protected internal override void OnEnabled() {
        if (!SceneManager.IsPlaying)
            return;

        Rigidbody rigidbody = AttachedRigidbody;
        if (rigidbody is null)
            CreateStaticBody();
        else
            rigidbody.NotifyColliderChanged();

        if (!RuntimeSet<Collider>.Contains(this))
            RuntimeSet<Collider>.Add(this);
    }

    protected internal override void OnDisabled() {
        RuntimeSet<Collider>.Remove(this);
        DestroyStaticBody();
        AttachedRigidbody?.NotifyColliderChanged();
    }

    protected internal override void OnDetach() {
        RuntimeSet<Collider>.Remove(this);
        DestroyStaticBody();
    }

    internal void SyncStaticBodyToTransform() {
        if (staticBody is null)
            return;
        Vector3 position = transform.WorldPosition;
        Quaternion rotation = transform.WorldRotation;
        if (hasSyncedPose && position == syncedPosition && rotation == syncedRotation)
            return;
        staticBody.Position = position;
        staticBody.Rotation = rotation;
        syncedPosition = position;
        syncedRotation = rotation;
        hasSyncedPose = true;
    }

    void CreateStaticBody() {
        if (staticBody is not null || Physics.World is null)
            return;

        Vector3 worldScale = transform.WorldMatrix.ExtractScale();
        PhysicsShape shape = BuildShape(worldScale);
        if (shape is null)
            return;

        var description = new PhysicsBodyDescription {
            Type = PhysicsBodyType.Static,
            Position = transform.WorldPosition,
            Rotation = transform.WorldRotation,
            Friction = Friction,
            Bounciness = Bounciness,
            IsTrigger = IsTrigger,
            Layer = entity.Layer,
            Shapes = [new PhysicsShapePart(shape, Center * worldScale, Quaternion.Identity)],
        };

        staticBody = Physics.World.AddBody(in description);
        if (staticBody is not null) {
            staticBody.UserData = this;
            syncedPosition = transform.WorldPosition;
            syncedRotation = transform.WorldRotation;
            hasSyncedPose = true;
        }
    }

    void DestroyStaticBody() {
        if (staticBody is null)
            return;
        Physics.World?.RemoveBody(staticBody);
        staticBody = null;
    }

    internal void ReleaseStaticBodyForRigidbody() => DestroyStaticBody();

    private protected Vector3 GizmoCenter =>
        transform.WorldPosition + Vector3.Transform(Center * transform.WorldMatrix.ExtractScale(), transform.WorldRotation);
}
