using System.Globalization;

namespace BallisticEngine.AssetPipeline.Unity;

public sealed class UnityMaterialData {
    public string DiffuseGuid;
    public string NormalGuid;
    public string MaskGuid;
    public string OcclusionGuid;
    public bool MaskIsPacked;

    public float[] BaseColor;
    public float? Metallic;
    public float? Smoothness;
    public bool AlphaCutout;

    public bool HasAnyTexture => DiffuseGuid is not null || NormalGuid is not null
                                                         || MaskGuid is not null || OcclusionGuid is not null;
}
