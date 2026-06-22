namespace BallisticEngine.AssetPipeline.Unity;

public readonly struct UnityRef(long fileId, string guid) {
    public readonly long FileId = fileId;
    public readonly string Guid = guid;

    public bool IsExternal => !string.IsNullOrEmpty(Guid);
    public bool IsNull => FileId == 0 && string.IsNullOrEmpty(Guid);
}
