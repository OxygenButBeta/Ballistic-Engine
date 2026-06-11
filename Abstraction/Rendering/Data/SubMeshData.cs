namespace BallisticEngine;

// A contiguous index-buffer range of a mesh that renders with one material.
// MaterialRef is an "Assets/..." reference baked by the model importer (the .mat it generated
// for the source material), or null when the source had no material — the renderer then falls
// back to its explicitly assigned SharedMaterial.
public readonly struct SubMeshData {
    public readonly string Name;        // source material name (debugging, editor display)
    public readonly int IndexStart;     // offset into the mesh's index buffer
    public readonly int IndexCount;
    public readonly string MaterialRef;

    public SubMeshData(string name, int indexStart, int indexCount, string materialRef) {
        Name = name;
        IndexStart = indexStart;
        IndexCount = indexCount;
        MaterialRef = materialRef;
    }

    public SubMeshData WithMaterialRef(string materialRef) =>
        new(Name, IndexStart, IndexCount, materialRef);
}
