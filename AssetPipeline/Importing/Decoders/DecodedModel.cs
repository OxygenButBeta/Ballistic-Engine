using Assimp;

namespace BallisticEngine.AssetPipeline;

public sealed class DecodedModel {
    public MeshData Mesh;
    public DecodedMaterial[] SubMeshMaterials;
}
