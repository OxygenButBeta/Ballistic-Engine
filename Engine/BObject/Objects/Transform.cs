using BallisticEngine;

public class Transform : Component {
    Vector3 position = Vector3.Zero;
    Quaternion rotation = Quaternion.Identity;
    Vector3 scale = Vector3.One;

    Vector3 eulerAngles = Vector3.Zero;
    bool eulerDirty;

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
        get {
            if (eulerDirty) {
                eulerAngles = RadiansToDegrees(rotation.ToEulerAngles());
                eulerDirty = false;
            }
            return eulerAngles;
        }
        set {
            eulerAngles = value;
            eulerDirty = false;
            rotation = BQuaternion.FromEulerAngles(DegreesToRadians(value));
            localVersion++;
        }
    }

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

    Matrix4 publishedWorld;
    bool hasPublished;

    public void PublishWorldForRender() {
        publishedWorld = WorldMatrix;
        hasPublished = true;
    }

    public Matrix4 RenderMatrix => hasPublished ? publishedWorld : WorldMatrix;

    public Vector3 RenderWorldPosition => RenderMatrix.ExtractTranslation();
    public Quaternion RenderWorldRotation => RenderMatrix.ExtractRotation();

    public Transform? Parent { get; private set; }

    public Entity Entity => entity;

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
        cachedWorldLocalVersion = -1;
        cachedParentWorldVersion = -1;
    }

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