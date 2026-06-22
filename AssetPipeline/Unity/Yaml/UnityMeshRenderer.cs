namespace BallisticEngine.AssetPipeline.Unity;

public sealed class UnityMeshRenderer {
    public long FileId;
    public long GameObjectId;
    public readonly List<UnityRef> Materials = new();
    public bool Enabled = true;
}
