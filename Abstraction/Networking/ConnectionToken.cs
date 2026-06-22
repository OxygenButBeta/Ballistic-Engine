namespace BallisticEngine.Networking;

public readonly record struct ConnectionToken(ulong Hi, ulong Lo) {
    public static readonly ConnectionToken None = default;

    public bool IsValid => Hi != 0 || Lo != 0;

    public static ConnectionToken Mint(ulong seed, ulong counter) =>
        new(seed ^ 0x9E3779B97F4A7C15UL * (counter + 1), 0xD1B54A32D192ED03UL * (counter + 1) ^ ~seed);

    public override string ToString() => IsValid ? $"token({Hi:x8}:{Lo:x8})" : "token(none)";
}
