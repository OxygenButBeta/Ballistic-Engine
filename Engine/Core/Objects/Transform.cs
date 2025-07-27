using BallisticEngine;
using OpenTK.Mathematics;

public class Transform : Component {
    public Vector3 Position { get; set; } = Vector3.Zero;
    public Quaternion Rotation { get; set; } = Quaternion.Identity;
    public Vector3 Scale { get; set; } = Vector3.One;

    public Vector3 EulerAngles {
        get => RadiansToDegrees(Rotation.ToEulerAngles());
        set => Rotation = Quaternion.FromEulerAngles(DegreesToRadians(value));
    }

    public Matrix4 WorldMatrix =>
        Parent == null ? LocalMatrix : Parent.WorldMatrix * LocalMatrix;

    public Matrix4 LocalMatrix =>
        Matrix4.CreateScale(Scale) *
        Matrix4.CreateFromQuaternion(Rotation) *
        Matrix4.CreateTranslation(Position);

    public Transform? Parent { get; private set; }

    public void SetParent(Transform? parent) {
        Parent = parent;
    }

    public Vector3 Forward => Rotation * Vector3.UnitZ;
    public Vector3 Up => Rotation * Vector3.UnitY;
    public Vector3 Right => Rotation * Vector3.UnitX;


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