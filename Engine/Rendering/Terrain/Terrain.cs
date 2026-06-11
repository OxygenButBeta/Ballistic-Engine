namespace BallisticEngine;

// Heightmap terrain (Unity-style). Generates a grid mesh from a .terrain asset's height field and
// drives a sibling StaticMeshRenderer with it — so the EXISTING renderer draws it with no special
// path, and an (optional) sibling MeshCollider gives it static collision for free (the collider
// falls back to the renderer's mesh).
//
// The mesh is rebuilt whenever the assigned asset changes OR its height field is sculpted (tracked
// by TerrainAsset.Revision). Because scene deserialization fires OnAttach BEFORE it applies the
// serialized asset ref (and OnBegin/Tick are play-only), the inputs are properties whose setters
// trigger a lazy EnsureBuilt() once the component is attached — so terrain appears in edit mode,
// the paused player, and play alike. OnBegin/Tick/gizmo draw also call EnsureBuilt as a safety net,
// and the sculpt tools call the public Rebuild() after a stroke.
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

    // The generated mesh + the renderer it feeds. Runtime-only: the mesh is rebuilt from the asset,
    // never serialized (the renderer it lives on is created by us, not authored in the scene).
    [NotSerialized]
    public Mesh GeneratedMesh { get; private set; }

    // Cached build signature: the asset instance we built from + the revision at build time. A change
    // in either (different asset assigned, or the same asset sculpted) triggers a rebuild.
    TerrainAsset builtAsset;
    int builtRevision = -1;
    Material builtMaterial;

    StaticMeshRenderer managedRenderer;
    MeshCollider managedCollider;

    // Build once attached, in case the asset ref was assigned before the component joined an entity
    // (programmatic creation: new Terrain { Terrain3D = ... } then AddComponent). Deserialization
    // instead sets the property AFTER OnAttach, where the setter drives the build.
    protected internal override void OnAttach() => EnsureBuilt();

    protected internal override void OnBegin() => EnsureBuilt();

    protected internal override void Tick(in float delta) => EnsureBuilt();

    // Editor path: OnDrawGizmos runs every scene frame for active components, so terrain that's only
    // viewed in the editor (no play) still builds and reacts to sculpt edits. Draws nothing itself.
    public override void OnDrawGizmos(IGizmos gizmos) => EnsureBuilt();

    protected internal override void OnDetach() {
        // Tear down the renderer/collider we created so the entity doesn't keep drawing a stale mesh.
        if (managedCollider is not null && !managedCollider.IsDetached)
            entity.RemoveComponent(managedCollider);
        if (managedRenderer is not null && !managedRenderer.IsDetached)
            entity.RemoveComponent(managedRenderer);
        managedRenderer = null;
        managedCollider = null;
        builtAsset = null;
        builtRevision = -1;
    }

    // Rebuilds the mesh if the asset/material/revision changed since the last build. Cheap no-op when
    // nothing changed. Safe to call every frame.
    public void EnsureBuilt() {
        // Setters can fire before the component is attached (programmatic construction); building
        // needs the entity (to add the sibling renderer/collider). OnAttach re-runs this once ready.
        if (entity is null)
            return;

        TerrainAsset asset = terrain3D;
        if (asset is null) {
            // No asset: drop any mesh we were showing so the viewport doesn't keep a stale terrain.
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

    // Forces an immediate rebuild from the current asset (the sculpt tools call this after a stroke).
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
        // A generated mesh has no baked material refs, so an unassigned material would make the
        // renderer non-renderable — fall back to the engine default (grey lit), like primitives.
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

        // Reuse an existing renderer on the entity if one's already there (e.g. after a scene reload),
        // otherwise add our own. The terrain owns whichever it ends up driving.
        managedRenderer = entity.GetComponent<StaticMeshRenderer>() ?? entity.AddComponent<StaticMeshRenderer>();
    }

    void EnsureCollider() {
        if (managedCollider is not null && !managedCollider.IsDetached)
            return;

        // A MeshCollider with no SharedMesh falls back to the entity's StaticMeshRenderer mesh — which
        // is exactly our generated terrain mesh — so we don't need to push the mesh into it explicitly.
        managedCollider = entity.GetComponent<MeshCollider>() ?? entity.AddComponent<MeshCollider>();
    }
}
