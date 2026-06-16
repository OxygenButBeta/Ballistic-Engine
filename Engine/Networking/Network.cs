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

    // ---- ownership transfer (server-only, replicated; plan §4d) -----------------------------------
    // Move input authority to a new connection (pick-up, vehicle-enter, detachable turret). Server-only
    // — a client cannot grant itself ownership. Fires OnOwnershipChanged on the affected objects.
    public static void TransferOwnership(NetworkObject netObj, Connection newOwner) =>
        Manager?.TransferOwnership(netObj, newOwner);

    // Drop ownership back to the server (Connection.None).
    public static void RemoveOwnership(NetworkObject netObj) => Manager?.RemoveOwnership(netObj);

    // ---- RPC dispatch (plan §4b, P4) — called by the GENERATED partial-void stub, not by hand ------
    // The generated method body packs its args into a BitWriter then calls this; the manager routes per the
    // declared To.X target (To.Server up / To.Owner+To.All down) and runs the dev method on the right
    // machine, owner-checked by default for To.Server (the closed trust boundary). NO RPC return (L1):
    // request→response is RPC-up + [Networked] state-down + [OnChanged]. Game code never calls this
    // directly — it calls the typed stub (`weapon.Fire(dir)`) the generator emits.
    public static void SendRpc(NetworkBehaviour self, int methodId, RpcTarget target, bool reliable,
        ReadOnlySpan<byte> args) =>
        Manager?.SendRpc(self, methodId, target, reliable, args);

    // The §4d.1 truth-table as a pure function — predict the authority a machine with the given
    // (topology, localConnection) holds over an object with the given owner. Exposed because it's the
    // canonical role definition: tooling/agents can predict "who runs this code" for any peer without a
    // live object, and it's the testable form of L3. The live path (spawn / TransferOwnership) calls the
    // same function, so this never drifts from real role resolution.
    public static NetworkAuthority ResolveRole(
        NetworkTopology topology, Connection localConnection, Connection owner) =>
        NetworkManager.ResolveAuthority(topology, localConnection, owner);

    // Resolve a netId to its object (internal — game code never sees a raw netId, §3). The generational
    // NetworkRef<T> handle (§8.4) that nulls on despawn is built on this.
    internal static NetworkObject Resolve(int netId) => Manager?.Resolve(netId);

    static NetworkManager Require() =>
        Manager ?? throw new InvalidOperationException(
            "Network has no NetworkManager. EngineBootstrap injects one; call this only after bootstrap.");
}
