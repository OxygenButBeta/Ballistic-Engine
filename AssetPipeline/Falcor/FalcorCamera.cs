using System.Globalization;
using System.Text.RegularExpressions;

namespace BallisticEngine.AssetPipeline;

public sealed class FalcorCamera {
    public Vector3 Position = new(0, 1, -5);
    public Vector3 Target = Vector3.Zero;
    public float FovYDegrees = 45f;
}
