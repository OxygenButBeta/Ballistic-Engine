using BallisticEngine;

public class Transform : Component {
    Vector3 position = Vector3.Zero;
    Quaternion rotation = Quaternion.Identity;
    Vector3 scale = Vector3.One;

    // Authored Euler angles (degrees), stored ALONGSIDE the quaternion so EulerAngles is a stable
    // edit field (Unity's eulerAngles). The quaternion→euler conversion is multi-valued: reading
    // EulerAngles straight off `rotation` and writing it back (e.g. a per-frame `t.EulerAngles =
    // t.EulerAngles`) is NOT idempotent — the result drifts every frame and gimbal-flips near ±90°.
    // So we keep the last authored euler and only re-derive it from the quaternion when something
    // ELSE set the rotation (eulerDirty). EulerAngles round-trips exactly; render math still reads
    // the quaternion, so behaviour is unchanged for everything but the euler edit path.
    Vector3 eulerAngles = Vector3.Zero;
    bool eulerDirty;

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
        // A quaternion write from anywhere but the euler setter invalidates the cached euler, so the
        // next EulerAngles read re-derives it from the new rotation (the only place the multi-valued
        // conversion is allowed to happen — once, on demand, not every frame).
        set { rotation = value; eulerDirty = true; localVersion++; }
    }

    public Vector3 Scale {
        get => scale;
        set { scale = value; localVersion++; }
    }

    public Vector3 Forward => Vector3.Transform(Vector3.UnitZ, Rotation);
    public Vector3 Up => Vector3.Transform(Vector3.UnitY, Rotation);
    public Vector3 Right => Vector3.Transform(Vector3.UnitX, Rotation);
    public Vector3 EulerAngles {
        // Return the authored euler verbatim (idempotent round-trip); only re-derive from the
        // quaternion when something else set the rotation since the last euler write.
        get {
            if (eulerDirty) {
                eulerAngles = RadiansToDegrees(rotation.ToEulerAngles());
                eulerDirty = false;
            }
            return eulerAngles;
        }
        // Store the authored angles AND the equivalent quaternion. Bypass the Rotation setter so it
        // doesn't flag eulerDirty — we want this exact euler preserved, not re-derived back out.
        set {
            eulerAngles = value;
            eulerDirty = false;
            rotation = BQuaternion.FromEulerAngles(DegreesToRadians(value));
            localVersion++;
        }
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

    // ---- DECOUPLED-RENDER-THREAD support (BALLISTIC_DX12_RENDER_THREAD) ----
    // The render thread must not call WorldMatrix: that getter LAZILY recomputes (writes cachedWorld, bumps
    // worldVersion, walks the parent chain) and races the game thread's setters → torn matrices. Instead the
    // game thread calls PublishWorldForRender() at the END of its Update (after all Tick/physics motion settled,
    // when WorldMatrix is final), snapshotting the world matrix into `publishedWorld`. The render thread reads
    // RenderMatrix, which returns that frozen copy — no recompute, no shared-state write, no race.
    //
    // When the render thread is OFF (the default), RenderMatrix falls straight through to WorldMatrix, so the
    // single-threaded path is byte-identical and pays nothing (no publish step runs).
    Matrix4 publishedWorld;
    bool hasPublished;

    // Game thread, end of frame: freeze this transform's current world matrix for the render thread to read.
    public void PublishWorldForRender() {
        publishedWorld = WorldMatrix;   // computed on the GAME thread (safe to touch the lazy cache here)
        hasPublished = true;
    }

    // Render thread reads THIS, never WorldMatrix. Falls back to the live matrix until the first publish (the
    // first frame) and whenever the render thread is disabled.
    public Matrix4 RenderMatrix => hasPublished ? publishedWorld : WorldMatrix;

    // Render-thread-safe world position/rotation, derived from the FROZEN matrix (the camera view matrix reads
    // these on the render thread). Identical to WorldPosition/WorldRotation when nothing was published (OFF path).
    public Vector3 RenderWorldPosition => RenderMatrix.ExtractTranslation();
    public Quaternion RenderWorldRotation => RenderMatrix.ExtractRotation();

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
            : Vector3.Transform(value, Parent.WorldMatrix.Inverted());
    }

    public Quaternion WorldRotation {
        get => Parent is null ? Rotation : Parent.WorldRotation * Rotation;
        set => Rotation = Parent is null ? value : Quaternion.Inverse(Parent.WorldRotation) * value;
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

        Matrix4 local = parent is null ? world : world * parent.WorldMatrix.Inverted();
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