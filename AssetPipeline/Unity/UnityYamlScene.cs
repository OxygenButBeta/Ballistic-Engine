namespace BallisticEngine.AssetPipeline.Unity;

public sealed class UnityYamlScene {
    public readonly Dictionary<long, UnityGameObject> GameObjects = new();
    public readonly Dictionary<long, UnityTransform> Transforms = new();
    public readonly Dictionary<long, UnityMeshFilter> MeshFilters = new();
    public readonly Dictionary<long, UnityMeshRenderer> MeshRenderers = new();

    public readonly Dictionary<long, UnityPrefabInstance> PrefabInstances = new();

    public long PrefabRootGameObjectId;
}

public sealed class UnityGameObject {
    public long FileId;
    public string Name = "GameObject";
    public bool Active = true;
    public readonly List<long> ComponentIds = new();
}

public sealed class UnityTransform {
    public long FileId;
    public long GameObjectId;
    public long FatherId;
    public Vector3 LocalPosition;
    public Quaternion LocalRotation = Quaternion.Identity;
    public Vector3 LocalScale = Vector3.One;
    public readonly List<long> ChildIds = new();
}

public sealed class UnityMeshFilter {
    public long FileId;
    public long GameObjectId;
    public UnityRef Mesh;
}

public sealed class UnityMeshRenderer {
    public long FileId;
    public long GameObjectId;
    public readonly List<UnityRef> Materials = new();
    public bool Enabled = true;
}

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

public readonly struct UnityRef(long fileId, string guid) {
    public readonly long FileId = fileId;
    public readonly string Guid = guid;

    public bool IsExternal => !string.IsNullOrEmpty(Guid);
    public bool IsNull => FileId == 0 && string.IsNullOrEmpty(Guid);
}
