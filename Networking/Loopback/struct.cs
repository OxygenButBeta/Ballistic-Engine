using BallisticEngine.Networking;

namespace BallisticEngine.Loopback;

public readonly record struct SimSettings(int LatencyTicks, int JitterTicks, double LossFraction) {
    public static readonly SimSettings Perfect = new(0, 0, 0.0);
    public static SimSettings Lan => new(1, 0, 0.0);
    public static SimSettings Broadband => new(6, 2, 0.02);
    public static SimSettings Poor => new(12, 4, 0.05);
}
