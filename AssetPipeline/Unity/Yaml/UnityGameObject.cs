namespace BallisticEngine.AssetPipeline.Unity;

public sealed class UnityGameObject {
    public long FileId;
    public string Name = "GameObject";
    public bool Active = true;
    public readonly List<long> ComponentIds = new();
}
