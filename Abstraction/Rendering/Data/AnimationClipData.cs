
namespace BallisticEngine;

public readonly struct AnimationClipData {
    public readonly string Name;
    public readonly float DurationTicks;
    public readonly float TicksPerSecond;
    public readonly BoneChannel[] Channels;

    public AnimationClipData(string name, float durationTicks, float ticksPerSecond, BoneChannel[] channels) {
        Name = name ?? "";
        DurationTicks = durationTicks;
        TicksPerSecond = ticksPerSecond > 0f ? ticksPerSecond : 25f;
        Channels = channels ?? System.Array.Empty<BoneChannel>();
    }

    public float DurationSeconds => TicksPerSecond > 0f ? DurationTicks / TicksPerSecond : 0f;
    public bool IsValid => Channels is { Length: > 0 } && DurationTicks > 0f;
}
