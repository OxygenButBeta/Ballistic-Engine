using Assimp;

namespace BallisticEngine.AssetPipeline;

public sealed class DecodedMaterial {
    public string Name;
    public readonly Dictionary<TextureType, string> TexturePaths = new();

    public Vector4? BaseColor;
    public float? Metallic;
    public float? Roughness;
    public Vector3? EmissiveColor;
    public float? Opacity;
}
