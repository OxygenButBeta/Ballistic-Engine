namespace BallisticEngine;

public abstract class Renderer : Behaviour, IStaticMeshRenderer {
    public abstract Mesh SharedMesh { get; set; }
    public abstract Material SharedMaterial { get; set; }
    public Transform Transform => transform;
    public bool RenderedThisFrame { get; set; }

    // Materials resolved from the mesh's baked submesh refs (the .mat assets the model importer
    // generated). Rebuilt lazily whenever the mesh instance changes; entries can be null.
    Mesh autoMaterialMesh;
    Material[] autoMaterials;
    bool hasAnyAutoMaterial;

    public bool IsRenderable {
        get {
            if (SharedMesh is null)
                return false;
            if (SharedMaterial is not null)
                return true;
            EnsureAutoMaterials();
            return hasAnyAutoMaterial;
        }
    }

    // The material a given submesh renders with. Single-submesh meshes honor an explicitly
    // assigned SharedMaterial first; multi-submesh meshes use their baked refs and fall back
    // to SharedMaterial for slots without one. May return null (submesh is skipped).
    public Material MaterialFor(int submeshIndex) {
        EnsureAutoMaterials();

        Material auto = autoMaterials is not null && (uint)submeshIndex < (uint)autoMaterials.Length
            ? autoMaterials[submeshIndex]
            : null;

        if (autoMaterials is null || autoMaterials.Length <= 1)
            return SharedMaterial ?? auto;
        return auto ?? SharedMaterial;
    }

    void EnsureAutoMaterials() {
        Mesh mesh = SharedMesh;
        if (ReferenceEquals(mesh, autoMaterialMesh))
            return;

        autoMaterialMesh = mesh;
        autoMaterials = null;
        hasAnyAutoMaterial = false;

        if (mesh?.SubMeshes is not { Length: > 0 } subMeshes)
            return;

        autoMaterials = new Material[subMeshes.Length];
        for (var i = 0; i < subMeshes.Length; i++) {
            if (string.IsNullOrEmpty(subMeshes[i].MaterialRef))
                continue;
            autoMaterials[i] = AssetDatabase.LoadRef<Material>(subMeshes[i].MaterialRef);
            hasAnyAutoMaterial |= autoMaterials[i] is not null;
        }
    }

    public void Activate() {
        MaterialFor(0)?.Activate();
        SharedMesh.Activate();
    }

    public void Deactivate() {
        MaterialFor(0)?.Deactivate();
        SharedMesh.Deactivate();
    }
}
