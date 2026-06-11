using BallisticEngine;
using OpenTK.Mathematics;

public class Transform : Component {
    Vector3 position = Vector3.Zero;
    Quaternion rotation = Quaternion.Identity;
    Vector3 scale = Vector3.One;

    // Matrix caching: the renderer reads WorldMatrix several times per frame per renderer
    // (cull AABB, prepass, shadow casters, main pass) — recomputing 3 matrix products plus the
    // whole parent chain each read was pure waste for static entities. `localVersion` bumps on
    // every setter; `worldVersion` bumps when the cached world matrix actually recomputes, so
    // children key their cache on (own local version, parent's world version) and any change
    // anywhere up the chain invalidates lazily on next read.
    int localVersion = 1;
    int worldVersion;
    int cachedLocalVersion = -1;
    int cachedWorldLocalVersion = -1;
    int cachedParentWorldVersion = -1;
    Matrix4 cachedLocal;
    Matrix4 cachedWorld;

    public Vector3 Position {
        get => position;
        set { position = value; localVersion++; }
    }

    public Quaternion Rotation {
        get => rotation;
        set { rotation = value; localVersion++; }
    }

    public Vector3 Scale {
        get => scale;
        set { scale = value; localVersion++; }
    }

    public Vector3 Forward => Rotation * Vector3.UnitZ;
    public Vector3 Up => Rotation * Vector3.UnitY;
    public Vector3 Right => Rotation * Vector3.UnitX;
    public Vector3 EulerAngles {
        get => RadiansToDegrees(Rotation.ToEulerAngles());
        set => Rotation = Quaternion.FromEulerAngles(DegreesToRadians(value));
    }

    // Row-vector (OpenTK) convention: points are transformed as v * M, so composition is
    // left-to-right (child-local FIRST, then up through the parents). Hence LocalMatrix * Parent,
    // NOT Parent * Local — the latter (column-major order) is what broke child follow/rotate/scale.
    public Matrix4 WorldMatrix {
        get {
            if (Parent == null) {
                if (cachedWorldLocalVersion != localVersion) {
                    cachedWorld = LocalMatrix;
                    cachedWorldLocalVersion = localVersion;
                    worldVersion++;
                }
                return cachedWorld;
            }

            // Read the parent FIRST: it recomputes (and bumps its worldVersion) if stale.
            Matrix4 parentWorld = Parent.WorldMatrix;
            if (cachedWorldLocalVersion != localVersion ||
                cachedParentWorldVersion != Parent.worldVersion) {
                cachedWorld = LocalMatrix * parentWorld;
                cachedWorldLocalVersion = localVersion;
                cachedParentWorldVersion = Parent.worldVersion;
                worldVersion++;
            }
            return cachedWorld;
        }
    }

    public Matrix4 LocalMatrix {
        get {
            if (cachedLocalVersion != localVersion) {
                cachedLocal = Matrix4.CreateScale(scale) *
                              Matrix4.CreateFromQuaternion(rotation) *
                              Matrix4.CreateTranslation(position);
                cachedLocalVersion = localVersion;
            }
            return cachedLocal;
        }
    }

    public Transform? Parent { get; private set; }

    // The entity this transform belongs to. Lets hierarchy walks (e.g. Entity.IsActiveInHierarchy)
    // hop from a parent Transform back to its Entity, since Children are derived from Parent links.
    public Entity Entity => entity;

    // World-space accessors: read/write the transform in world space, converting through the parent
    // chain. The editor gizmo edits in world space, so parented objects move/rotate/scale relative to
    // their parent correctly (setting local Position by a world delta would be wrong under a parent).
    public Vector3 WorldPosition {
        get => WorldMatrix.ExtractTranslation();
        set => Position = Parent is null
            ? value
            : Vector3.TransformPosition(value, Matrix4.Invert(Parent.WorldMatrix));
    }

    public Quaternion WorldRotation {
        get => Parent is null ? Rotation : Parent.WorldRotation * Rotation;
        set => Rotation = Parent is null ? value : Quaternion.Invert(Parent.WorldRotation) * value;
    }

    public void SetParent(Transform? parent) {
        Parent = parent;
        cachedWorldLocalVersion = -1; // new chain: recompute world on next read
        cachedParentWorldVersion = -1;
    }

    // Reparents while keeping the same world transform: recomputes local Position/Rotation/Scale so
    // that Parent.WorldMatrix * newLocal reproduces the current WorldMatrix (no visual jump). Used by
    // the editor's drag-to-parent. Pass null to unparent to world space.
    public void SetParentKeepingWorld(Transform? parent) {
        Matrix4 world = WorldMatrix;
        Parent = parent;
        cachedWorldLocalVersion = -1;
        cachedParentWorldVersion = -1;

        Matrix4 local = parent is null ? world : world * Matrix4.Invert(parent.WorldMatrix);
        Scale = local.ExtractScale();
        Rotation = local.ExtractRotation();
        Position = local.ExtractTranslation();
    }

    // True if `potentialAncestor` is this transform or one of its parents (cycle guard for reparenting).
    public bool IsDescendantOf(Transform potentialAncestor) {
        for (Transform? t = this; t is not null; t = t.Parent)
            if (ReferenceEquals(t, potentialAncestor))
                return true;
        return false;
    }

    static Vector3 RadiansToDegrees(Vector3 radians) =>
        new(
            MathHelper.RadiansToDegrees(radians.X),
            MathHelper.RadiansToDegrees(radians.Y),
            MathHelper.RadiansToDegrees(radians.Z));

    static Vector3 DegreesToRadians(Vector3 degrees) =>
        new(
            MathHelper.DegreesToRadians(degrees.X),
            MathHelper.DegreesToRadians(degrees.Y),
            MathHelper.DegreesToRadians(degrees.Z));
}