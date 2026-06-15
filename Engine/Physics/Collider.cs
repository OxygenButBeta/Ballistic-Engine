
namespace BallisticEngine;

// Base of all collision shapes. A collider on an entity WITH a Rigidbody contributes its shape
// to that body (several colliders form a compound). A collider on an entity WITHOUT one
// becomes a standalone static body — level geometry needs no Rigidbody, exactly like Unity.
//
// Shape dimensions bake the entity's world scale at body-creation time (play start); scale
// changes during play do not resize live bodies.
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

    IPhysicsBody staticBody; // owned only when this entity has no Rigidbody

    public Rigidbody AttachedRigidbody => entity?.GetComponent<Rigidbody>();

    // Shape in body-local space with the world scale baked in. May return null (after logging)
    // when unbuildable, e.g. a MeshCollider with no mesh.
    internal abstract PhysicsShape BuildShape(Vector3 worldScale);

    // Concave shapes (meshes) can't ride on a dynamic body.
    internal virtual bool ValidForDynamic => true;

    // Unity parity: a primitive collider added to an entity that renders a mesh sizes itself
    // to that mesh's local bounds. Each shape overrides this and only fits while it still has
    // its pristine constructor defaults — scene deserialization applies saved members AFTER
    // OnAttach, so saved values always win, and user-edited shapes are never stomped.
    protected internal override void OnAttach() => AutoFitToRenderMesh();

    private protected virtual void AutoFitToRenderMesh() {
    }

    // Local-space AABB of the entity's rendered mesh — the same submesh slice the renderer
    // draws, un-baking the node transform like the renderer does. False when nothing renders.
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
    }

    protected internal override void OnDisabled() {
        DestroyStaticBody();
        AttachedRigidbody?.NotifyColliderChanged();
    }

    protected internal override void OnDetach() => DestroyStaticBody();

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
        if (staticBody is not null)
            staticBody.UserData = this;
    }

    void DestroyStaticBody() {
        if (staticBody is null)
            return;
        Physics.World?.RemoveBody(staticBody);
        staticBody = null;
    }

    // Called by a Rigidbody that is adopting this collider into its compound. If this collider
    // already spun up a standalone static body (its OnEnabled ran while no Rigidbody was present —
    // e.g. the collider was added to the entity before the Rigidbody), that static body must go,
    // or it overlaps the new dynamic body and the two eject each other at spawn. Makes component
    // order on an entity irrelevant.
    internal void ReleaseStaticBodyForRigidbody() => DestroyStaticBody();

    // World-space pose of the shape, for gizmos.
    private protected Vector3 GizmoCenter =>
        transform.WorldPosition + Vector3.Transform(Center * transform.WorldMatrix.ExtractScale(), transform.WorldRotation);
}
