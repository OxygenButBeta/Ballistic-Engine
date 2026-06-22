namespace BallisticEngine.AssetPipeline.Unity;

public sealed class UnityTransform {
    public long FileId;
    public long GameObjectId;
    public long FatherId;
    public Vector3 LocalPosition;
    public Quaternion LocalRotation = Quaternion.Identity;
    public Vector3 LocalScale = Vector3.One;
    public readonly List<long> ChildIds = new();
}
