namespace BallisticEngine;

[Component("Terrain", "Rendering")]
public class Terrain : Behaviour {
    TerrainAsset terrain3D;
    Material material;
    bool generateCollider = true;

    [Tooltip("The .terrain asset holding the editable height field. Create one via the asset browser (New Terrain).")]
    public TerrainAsset Terrain3D {
        get => terrain3D;
        set { terrain3D = value; EnsureBuilt(); }
    }

    [Tooltip("Material the terrain renders with. Leave empty to use the engine's default lit material.")]
    public Material Material {
        get => material;
        set { material = value; EnsureBuilt(); }
    }

    [Tooltip("Generate a static MeshCollider from the terrain mesh so objects collide with it.")]
    public bool GenerateCollider {
        get => generateCollider;
        set { generateCollider = value; EnsureBuilt(); }
    }

    [NotSerialized]
    public Mesh GeneratedMesh { get; private set; }

    TerrainAsset builtAsset;
    int builtRevision = -1;
    Material builtMaterial;

    StaticMeshRenderer managedRenderer;
    MeshCollider managedCollider;

    protected internal override void OnAttach() => EnsureBuilt();

    protected internal override void OnBegin() => EnsureBuilt();

    protected internal override void Tick(in float delta) => EnsureBuilt();

    public override void OnDrawGizmos(IGizmos gizmos) => EnsureBuilt();

    protected internal override void OnDetach() {
        if (managedCollider is not null && !managedCollider.IsDetached)
            entity.RemoveComponent(managedCollider);
        if (managedRenderer is not null && !managedRenderer.IsDetached)
            entity.RemoveComponent(managedRenderer);
        managedRenderer = null;
        managedCollider = null;
        builtAsset = null;
        builtRevision = -1;
    }

    public void EnsureBuilt() {
        if (entity is null)
            return;

        TerrainAsset asset = terrain3D;
        if (asset is null) {
            if (managedRenderer is not null)
                managedRenderer.SharedMesh = null;
            builtAsset = null;
            builtRevision = -1;
            return;
        }

        bool unchanged = ReferenceEquals(asset, builtAsset)
                         && asset.Revision == builtRevision
                         && ReferenceEquals(material, builtMaterial);
        if (unchanged && GeneratedMesh is not null)
            return;

        Rebuild();
    }

    public void Rebuild() {
        if (entity is null)
            return;

        TerrainAsset asset = terrain3D;
        if (asset is null)
            return;

        TerrainData data = asset.ToData();
        if (!data.IsValid) {
            Debugging.LogWarning($"Terrain on '{entity.Name}': asset '{asset.Name}' has an invalid height field; not built.");
            return;
        }

        GeneratedMesh = Mesh.Create(TerrainMeshBuilder.Build(in data));

        EnsureRenderer();
        managedRenderer.SharedMesh = GeneratedMesh;
        managedRenderer.SharedMaterial = material ?? TerrainDefaultMaterial.Get();

        if (generateCollider)
            EnsureCollider();
        else if (managedCollider is not null && !managedCollider.IsDetached) {
            entity.RemoveComponent(managedCollider);
            managedCollider = null;
        }

        builtAsset = asset;
        builtRevision = asset.Revision;
        builtMaterial = material;
    }

    void EnsureRenderer() {
        if (managedRenderer is not null && !managedRenderer.IsDetached)
            return;

        managedRenderer = entity.GetComponent<StaticMeshRenderer>() ?? entity.AddComponent<StaticMeshRenderer>();
    }

    void EnsureCollider() {
        if (managedCollider is not null && !managedCollider.IsDetached)
            return;

        managedCollider = entity.GetComponent<MeshCollider>() ?? entity.AddComponent<MeshCollider>();
    }
}
