
namespace BallisticEngine;

public abstract class Renderer : Behaviour, IStaticMeshRenderer {
    public abstract Mesh SharedMesh { get; set; }
    public abstract Material SharedMaterial { get; set; }

    public virtual int SubMeshIndex { get; set; } = -1;

    public virtual float LodBias { get; set; } = 1f;

    public Transform Transform => transform;
    public bool RenderedThisFrame { get; set; }

    public virtual bool IsSkinned => false;
    public virtual Matrix4[] SkinningMatrices => null;

    Material materialInstance;

    public Material Material {
        get {
            if (materialInstance is not null)
                return materialInstance;

            Material source = SharedMaterial ?? MaterialFor(0);
            if (source is null)
                return null;

            materialInstance = source.Clone();
            SharedMaterial = materialInstance;
            return materialInstance;
        }
        set {
            materialInstance = value;
            SharedMaterial = value;
        }
    }

    Mesh autoMaterialMesh;
    Material[] autoMaterials;
    bool hasAnyAutoMaterial;

    Material[] materialOverrides;

    protected Material[] MaterialOverrides {
        get => materialOverrides;
        set => materialOverrides = value;
    }

    Material OverrideFor(int submeshIndex) =>
        materialOverrides is not null && (uint)submeshIndex < (uint)materialOverrides.Length
            ? materialOverrides[submeshIndex]
            : null;

    public void SetMaterialOverride(int submeshIndex, Material material) {
        if (submeshIndex < 0)
            return;
        int meshCount = SharedMesh?.SubMeshes?.Length ?? 0;
        int needed = Math.Max(meshCount, submeshIndex + 1);
        if (materialOverrides is null || materialOverrides.Length < needed) {
            var grown = new Material[needed];
            if (materialOverrides is not null)
                Array.Copy(materialOverrides, grown, materialOverrides.Length);
            materialOverrides = grown;
        }
        materialOverrides[submeshIndex] = material;
    }

    public Material GetMaterialOverride(int submeshIndex) => OverrideFor(submeshIndex);

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

    public static bool ShowMissingMaterial = true;

    public Material MaterialFor(int submeshIndex) {
        EnsureAutoMaterials();

        Material over = OverrideFor(submeshIndex);
        if (over is not null)
            return over;

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
