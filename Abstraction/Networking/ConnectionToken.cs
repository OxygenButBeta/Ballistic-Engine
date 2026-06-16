namespace BallisticEngine.Networking;

// The PERSISTENT player identity (plan §9.8 / §8.5.5) — NOT the transport Connection.Id. A reconnect gets
// a NEW transport id (a new socket/peer), but presents the SAME ConnectionToken at handshake, so the server
// can reclaim the player's orphaned pawn by token (transfer ownership BACK) within the reconnect TTL.
//
// This is the §9 item-8 fix: identity = a stable token, not the transport id, so disconnect/reconnect does
// not orphan the pawn permanently or hand it to the wrong player. The token is a 128-bit value the CLIENT
// persists (a lobby ticket / a saved file / a login session) and sends in its first handshake. P7 mints one
// per client at connect when none is presented (the common first-join case); a real reconnect presents the
// one it kept. Two distinct players never collide (128 bits, minted from a counter + the connection seed).
//
// BCL-only (Abstraction layer) — a plain value type, wire-packed as two ulongs. The reclaim/TTL ALGORITHM
// was proven in %TEMP%\bal-reconnect-test (26/26) before the engine integration.
public readonly record struct ConnectionToken(ulong Hi, ulong Lo) {
    public static readonly ConnectionToken None = default;

    public bool IsValid => Hi != 0 || Lo != 0;

    // Mint a fresh token from a monotonic counter + a per-process seed so distinct clients never collide
    // (the server mints one for a first-join client that presents None). Deterministic Math.Random is
    // banned in some harness contexts, so this is a counter-based mint seeded by the caller's process salt.
    public static ConnectionToken Mint(ulong seed, ulong counter) =>
        new(seed ^ 0x9E3779B97F4A7C15UL * (counter + 1), 0xD1B54A32D192ED03UL * (counter + 1) ^ ~seed);

    public override string ToString() => IsValid ? $"token({Hi:x8}:{Lo:x8})" : "token(none)";
}
