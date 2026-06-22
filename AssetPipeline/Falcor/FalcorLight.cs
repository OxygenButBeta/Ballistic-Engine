using System.Globalization;
using System.Text.RegularExpressions;

namespace BallisticEngine.AssetPipeline;

public sealed class FalcorLight {
    public Vector3 Direction = new(0, -1, 0);
    public Vector3 Color = Vector3.One;
    public float Intensity = 1f;
}
