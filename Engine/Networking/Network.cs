using BallisticEngine.Networking;
using BallisticEngine.Loopback;

namespace BallisticEngine;

// Static facade over the NetworkManager (plan §4d / §8.1) — the same shape as Physics.Raycast /
// Input.IsKeyDown. Game code talks to this; the manager instance is injected by EngineBootstrap.
//
// The ONLY bare server/client booleans live here and are about the PROCESS topology (§3 Grade-3):
// Network.IsServer/IsClient/IsHost ≠ an object's IsSpawned (object readiness). Keeping them on the
// facade (not on objects) is the FishNet IsServerStarted/IsServerInitialized split, designed in.
public static class Network {
    // Injected by EngineBootstrap, like Physics.World. Null only before bootstrap; the facade
    // degrades to Offline so a host that never wired networking behaves exactly as today.
    public static NetworkManager Manager { get; set; }

    public static bool IsServer  => Manager?.IsServer ?? false;
    public static bool IsClient  => Manager?.IsClient ?? false;
    public static bool IsHost    => Manager?.IsHost ?? false;
    public static bool IsOffline => Manager?.IsOffline ?? true;

    public static Connection LocalConnection => Manager?.LocalConnection ?? Connection.None;

    // ---- bring-up (server-authoritative lifecycle) ------------------------------------------------
    // Single-player / listen-server over loopback by default (D5): SP = host, same code path as MP.
    public static void StartHost(ITransport transport = null) =>
        Require().StartHost(transport ?? new LoopbackTransport());

    public static void StartServer(ITransport transport) => Require().StartServer(transport);
    public static void StartClient(ITransport transport) => Require().StartClient(transport);
    public static void Stop() => Manager?.Stop();

    // ---- spawn (server-authoritative; owner defaults to the server — closed trust boundary) -------
    public static NetworkObject Spawn(Entity entity, Connection owner = default) =>
        Require().Spawn(entity, owner);

    public static void Despawn(NetworkObject netObj) => Manager?.Despawn(netObj);

    // Resolve a netId to its object (internal — game code never sees a raw netId, §3). The generational
    // NetworkRef<T> handle that nulls on despawn is built on this in P1.
    internal static NetworkObject Resolve(int netId) => Manager?.Resolve(netId);

    static NetworkManager Require() =>
        Manager ?? throw new InvalidOperationException(
            "Network has no NetworkManager. EngineBootstrap injects one; call this only after bootstrap.");
}
