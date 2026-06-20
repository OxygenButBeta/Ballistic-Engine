
namespace BallisticEngine;

public abstract class Renderer : Behaviour, IStaticMeshRenderer {
    public abstract Mesh SharedMesh { get; set; }
    public abstract Material SharedMaterial { get; set; }

    // -1 = whole mesh. Concrete renderers override to serialize it (members declared on this
    // base class are excluded from serialization by ComponentReflection).
    public virtual int SubMeshIndex { get; set; } = -1;

    // Geometric-LOD screen-size bias (Unity's per-renderer LODBias). >1 keeps higher detail at a given distance,
    // <1 drops detail sooner. Only consulted when the mesh has an imported LOD chain AND LodSettings is active;
    // 1.0 default → no effect unless LOD is on, so byte-identical to a no-LOD build.
    public virtual float LodBias { get; set; } = 1f;

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

    // Per-submesh material OVERRIDES (Unity's renderer.sharedMaterials). Index = submesh index; a null
    // entry means "no override — fall back to the baked ref / SharedMaterial" (the original behaviour),
    // so an all-null/absent array is byte-identical to before. A non-null entry wins over the baked ref
    // in MaterialFor, letting the inspector reassign any submesh slot of a multi-material mesh (the
    // editor's per-slot material list writes here). Stored on the base; concrete renderers expose it as a
    // SERIALIZED member (SharedMaterials) — members declared on this base class are excluded from
    // serialization (ComponentReflection), exactly like SubMeshIndex.
    Material[] materialOverrides;

    // The override array, lazily sized to the mesh's submesh count (so the inspector always has a slot per
    // submesh). Returns the live array; callers may read/write entries. Null mesh → empty.
    protected Material[] MaterialOverrides {
        get => materialOverrides;
        set => materialOverrides = value;
    }

    // Returns the override for a submesh, or null if none / out of range.
    Material OverrideFor(int submeshIndex) =>
        materialOverrides is not null && (uint)submeshIndex < (uint)materialOverrides.Length
            ? materialOverrides[submeshIndex]
            : null;

    // Sets (or clears, value=null) the per-submesh material override, growing the array to fit the mesh's
    // submesh count so every slot is addressable. Used by the inspector's per-slot material list. The array
    // is sized to the live mesh's submesh count (clamped to at least submeshIndex+1) so a saved override
    // survives a later mesh swap that adds submeshes. Setting all entries back to null is harmless (the
    // resolver falls through to the baked ref); a fully-null array still serializes but reads as "no override".
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

    // The current override for a submesh (null = none), for the inspector to show the live slot value.
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

    // The material a given submesh renders with. Single-submesh meshes honor an explicitly
    // assigned SharedMaterial first; multi-submesh meshes use their baked refs and fall back
    // to SharedMaterial for slots without one. When NOTHING resolves, substitutes the magenta/black
    // MissingMaterial so the gap is visible (Unity's missing-material pink) instead of silently
    // skipping the submesh — set ShowMissingMaterial = false to opt out (e.g. intentional holes).
    public static bool ShowMissingMaterial = true;

    public Material MaterialFor(int submeshIndex) {
        EnsureAutoMaterials();

        // A per-submesh override (inspector-assigned) wins over everything — Unity's sharedMaterials[i].
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
