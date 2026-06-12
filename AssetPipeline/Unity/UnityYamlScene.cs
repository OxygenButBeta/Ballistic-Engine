namespace BallisticEngine.AssetPipeline.Unity;

// Parsed shape of a Unity .unity scene or .prefab — only the objects we can map to Ballistic
// entities (GameObject + Transform + MeshFilter + MeshRenderer). Everything else (lights, cameras,
// scripts, terrain, ...) is left for the converter to optionally handle or ignore.
//
// Unity references everything by a numeric fileID local to the document; cross-FILE references are
// {fileID, guid} pairs (guid = the target asset's .meta GUID). We keep those raw so the converter
// resolves them against the project's GUID table.
public sealed class UnityYamlScene {
    public readonly Dictionary<long, UnityGameObject> GameObjects = new();
    public readonly Dictionary<long, UnityTransform> Transforms = new();
    public readonly Dictionary<long, UnityMeshFilter> MeshFilters = new();
    public readonly Dictionary<long, UnityMeshRenderer> MeshRenderers = new();
    // Nested-prefab instances (a scene placing prefabs, or a prefab referencing other prefabs). These
    // carry the real set-dressing: a source-prefab guid + per-instance transform/name overrides.
    public readonly Dictionary<long, UnityPrefabInstance> PrefabInstances = new();

    // A .prefab's root GameObject (the one whose transform has no parent). Null for scenes.
    public long PrefabRootGameObjectId;
}

public sealed class UnityGameObject {
    public long FileId;
    public string Name = "GameObject";
    public bool Active = true;
    public readonly List<long> ComponentIds = new(); // fileIDs of attached components (Transform, etc.)
}

public sealed class UnityTransform {
    public long FileId;
    public long GameObjectId;
    public long FatherId;               // parent transform fileID, 0 = root
    public OpenTK.Mathematics.Vector3 LocalPosition;
    public OpenTK.Mathematics.Quaternion LocalRotation = OpenTK.Mathematics.Quaternion.Identity;
    public OpenTK.Mathematics.Vector3 LocalScale = OpenTK.Mathematics.Vector3.One;
    public readonly List<long> ChildIds = new();
}

public sealed class UnityMeshFilter {
    public long FileId;
    public long GameObjectId;
    public UnityRef Mesh;               // {fileID, guid} of the mesh asset (an .fbx/.obj/etc.)
}

public sealed class UnityMeshRenderer {
    public long FileId;
    public long GameObjectId;
    public readonly List<UnityRef> Materials = new(); // ordered, one per submesh
    public bool Enabled = true;
}

// A nested-prefab instance (Unity class 1001). The real props in a dressed scene are these: a
// reference to a source .prefab plus a flat list of property-path overrides (name, the instance's
// local position/rotation/scale, and which transform it parents under). We decode the handful of
// overrides we need; the rest (per-material tweaks etc.) are ignored.
public sealed class UnityPrefabInstance {
    public long FileId;
    public string SourcePrefabGuid;     // m_SourcePrefab guid -> the prefab asset to instantiate
    public long TransformParentId;      // m_TransformParent fileID (a Transform in THIS file), 0 = root
    public string Name;                 // name override, if any
    public bool Active = true;

    public OpenTK.Mathematics.Vector3 LocalPosition;
    public OpenTK.Mathematics.Quaternion LocalRotation = OpenTK.Mathematics.Quaternion.Identity;
    public OpenTK.Mathematics.Vector3 LocalScale = OpenTK.Mathematics.Vector3.One;
}

// A Unity reference: a local fileID plus, for cross-file refs, the target asset's GUID.
public readonly struct UnityRef(long fileId, string guid) {
    public readonly long FileId = fileId;
    public readonly string Guid = guid;     // null for same-file refs

    public bool IsExternal => !string.IsNullOrEmpty(Guid);
    public bool IsNull => FileId == 0 && string.IsNullOrEmpty(Guid);
}
