using BallisticEngine;
using OpenTK.Mathematics;

public interface IStaticMeshRenderer : IDrawable {
    Mesh SharedMesh { get; }
    Material SharedMaterial { get; }

    // -1 draws all of SharedMesh's submeshes (the default). >= 0 draws only that submesh —
    // model instantiation gives each child entity the shared mesh plus its own submesh index,
    // so per-object entities cost no geometry duplication.
    int SubMeshIndex { get; }

    // The material a given submesh of SharedMesh renders with — the mesh's baked material for
    // that range, or SharedMaterial as fallback. Null means the submesh is skipped.
    Material MaterialFor(int submeshIndex);

    // False until a mesh and at least one resolvable material are assigned; the renderer skips
    // such targets.
    bool IsRenderable { get; }

    // Entity active && component enabled — disabling either hides the mesh.
    bool IsActive { get; }

    // Skinning: a skinned renderer returns its per-bone skinning matrices (mesh-bind -> animated
    // world, in mesh-local space) for THIS frame; the draw path uploads them to the bone SSBO before
    // drawing. Static renderers return null and take the normal path. SkinningMatrices.Length ==
    // SharedMesh.BoneCount when non-null.
    bool IsSkinned => false;
    Matrix4[] SkinningMatrices => null;

    public void Activate();
    public void Deactivate();
}
