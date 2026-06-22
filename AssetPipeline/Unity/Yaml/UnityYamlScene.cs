namespace BallisticEngine.AssetPipeline.Unity;

public sealed class UnityYamlScene {
    public readonly Dictionary<long, UnityGameObject> GameObjects = new();
    public readonly Dictionary<long, UnityTransform> Transforms = new();
    public readonly Dictionary<long, UnityMeshFilter> MeshFilters = new();
    public readonly Dictionary<long, UnityMeshRenderer> MeshRenderers = new();

    public readonly Dictionary<long, UnityPrefabInstance> PrefabInstances = new();

    public long PrefabRootGameObjectId;
}
