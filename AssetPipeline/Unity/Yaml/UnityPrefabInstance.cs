namespace BallisticEngine.AssetPipeline.Unity;

public sealed class UnityPrefabInstance {
    public long FileId;
    public string SourcePrefabGuid;
    public long TransformParentId;
    public string Name;
    public bool Active = true;

    public Vector3 LocalPosition;
    public Quaternion LocalRotation = Quaternion.Identity;
    public Vector3 LocalScale = Vector3.One;
}
