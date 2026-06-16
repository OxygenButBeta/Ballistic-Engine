
namespace BallisticEngine;

public abstract class Renderer : Behaviour, IStaticMeshRenderer {
    public abstract Mesh SharedMesh { get; set; }
    public abstract Material SharedMaterial { get; set; }

    // -1 = whole mesh. Concrete renderers override to serialize it (members declared on this
    // base class are excluded from serialization by ComponentReflection).
    public virtual int SubMeshIndex { get; set; } = -1;
    public Transform Transform => transform;
    public bool RenderedThisFrame { get; set; }

    // Skinning hooks (IStaticMeshRenderer). Static renderers are never skinned; SkinnedMeshRenderer
    // overrides both so the draw path uploads its per-bone matrices to the bone SSBO.
    public virtual bool IsSkinned => false;
    public virtual Matrix4[] SkinningMatrices => null;

    Material materialInstance;

    // Unity's renderer.material: returns a per-renderer CLONE of the shared material that you can
    // mutate (material.MetallicFactor = ..., material.BaseColorFactor = ...) without affecting the
    // .mat asset or other renderers using it. The clone is created on first access and reused; it
    // replaces SharedMaterial for this renderer so rendering picks it up. Because the instance isn't
    // an asset, it serializes as null — a runtime-only override, exactly like Unity's instanced mats.
    //
    // Use SharedMaterial instead when you WANT to edit the asset (affecting every user of it).
    public Material Material {
        get {
            if (materialInstance is not null)
                return materialInstance;

            Material source = SharedMaterial ?? MaterialFor(0);
            if (source is null)
                return null; // nothing to instance yet (no mesh/material assigned)

            materialInstance = source.Clone();
            SharedMaterial = materialInstance; // route this renderer's draws through the instance
            return materialInstance;
        }
        set {
            materialInstance = value;
            SharedMaterial = value;
        }
    }

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
    // to SharedMaterial for slots without one. When NOTHING resolves, substitutes the magenta/black
    // MissingMaterial so the gap is visible (Unity's missing-material pink) instead of silently
    // skipping the submesh — set ShowMissingMaterial = false to opt out (e.g. intentional holes).
    public static bool ShowMissingMaterial = true;

    public Material MaterialFor(int submeshIndex) {
        EnsureAutoMaterials();

        Material auto = autoMaterials is not null && (uint)submeshIndex < (uint)autoMaterials.Length
            ? autoMaterials[submeshIndex]
            : null;

        Material resolved = (autoMaterials is null || autoMaterials.Length <= 1)
            ? SharedMaterial ?? auto
            : auto ?? SharedMaterial;

        return resolved ?? (ShowMissingMaterial ? MissingMaterial.Get() : null);
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
