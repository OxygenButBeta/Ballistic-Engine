using BallisticEngine.Networking;

namespace BallisticEngine;

// The orchestrator (plan §8.1) — a plain engine object, instantiated once at bootstrap alongside
// Physics.World / Input.Provider, reachable through the static Network facade. NOT a NetworkBehaviour
// and never on an entity (FishNet's hard rule — avoids recursive identity tangles).
//
// P0 owns: the transport seam, the topology, the netId -> NetworkObject registry, and the
// server-authoritative spawn/despawn path (loopback only — no socket until P3). ServerManager/
// ClientManager/observer/prediction are later phases; the facade shape is final so they slot under it.
public sealed class NetworkManager {
    public ITransport Transport { get; private set; }
    public NetworkTopology Topology { get; private set; } = NetworkTopology.Offline;

    // netId -> object. Internal: the §3 no-public-netId rule. Slot 0 unused (0 = "unspawned").
    readonly Dictionary<int, NetworkObject> objects = new();
    int nextNetId = 1;

    // The local connection's identity. In a loopback host this is Connection.Local; a remote client
    // gets its id from the transport handshake (P3).
    public Connection LocalConnection { get; private set; } = Connection.None;

    public bool IsServer => Topology is NetworkTopology.Server or NetworkTopology.Host;
    public bool IsClient => Topology is NetworkTopology.Client or NetworkTopology.Host;
    public bool IsHost   => Topology is NetworkTopology.Host;
    public bool IsOffline => Topology is NetworkTopology.Offline;

    // ---- bring-up ---------------------------------------------------------------------------------
    // Single-player / listen-server: server + local client in one process over the given transport
    // (loopback by default — D5). The same code path multiplayer uses; only the transport differs.
    public void StartHost(ITransport transport) {
        Stop();
        Transport = transport ?? throw new ArgumentNullException(nameof(transport));
        WireTransport();
        Topology = NetworkTopology.Host;
        LocalConnection = Connection.Local;
        Transport.StartServer();
        Transport.Connect();   // the in-process client half
    }

    public void StartServer(ITransport transport) {
        Stop();
        Transport = transport ?? throw new ArgumentNullException(nameof(transport));
        WireTransport();
        Topology = NetworkTopology.Server;
        LocalConnection = Connection.None;   // a dedicated server has no local player
        Transport.StartServer();
    }

    public void StartClient(ITransport transport) {
        Stop();
        Transport = transport ?? throw new ArgumentNullException(nameof(transport));
        WireTransport();
        Topology = NetworkTopology.Client;
        Transport.Connect();
    }

    // Return to Offline (no socket). Despawns nothing by itself — StopPlay tears the scene down.
    public void Stop() {
        Transport?.Stop();
        Transport = null;
        Topology = NetworkTopology.Offline;
        LocalConnection = Connection.None;
        objects.Clear();
        nextNetId = 1;
    }

    void WireTransport() {
        Transport.OnConnected = OnPeerConnected;
        Transport.OnDisconnected = OnPeerDisconnected;
        Transport.OnReceived = OnPayload;
    }

    // P0 stubs — the loopback path needs no per-event logic yet (no remote peers, no wire state).
    // P3 fills these (a real client connecting triggers GameMode.OnPlayerJoined, etc.).
    void OnPeerConnected(Connection c) { }
    void OnPeerDisconnected(Connection c) { }
    void OnPayload(Connection source, ReadOnlySpan<byte> payload, Channel channel) { }

    // ---- the tick seam (plan §8.2) ----------------------------------------------------------------
    // Drains incoming, (later) runs NetworkTick + reconcile, sends outgoing. P0: just pumps the
    // transport so the loopback pair stays live. Called once per fixed step by SceneManager.
    public void Tick() {
        Transport?.Poll();
    }

    // ---- server-authoritative spawn (plan §6 / §8.5) ----------------------------------------------
    // Spawn a networked entity: assign a netId, set owner + authority, drive the NET strand in order
    // (OnSpawned -> OnStartX) BEFORE the Unity strand. Server-only conceptually; in loopback the host
    // is the server. `owner` defaults to the server (Connection.None) — the closed trust boundary
    // (§4d): a world/AI object is server-owned; a player pawn passes the owning connection.
    //
    // The lifecycle ordering (§5): we suppress Entity.Attach's eager FireEnable (SuppressPlayLifecycle,
    // the existing deserialize mechanism) while building the object, drive the net strand, then let the
    // Unity strand fire once. Returns the spawned NetworkObject (its entity is `.Entity`).
    public NetworkObject Spawn(Entity entity, Connection owner = default) {
        if (entity is null)
            throw new ArgumentNullException(nameof(entity));
        if (!IsServer)
            Debugging.LogWarning("Network.Spawn called without server authority — ignored on a client (server spawns).");

        NetworkObject netObj = entity.GetComponent<NetworkObject>() ?? entity.AddComponent<NetworkObject>();
        return SpawnObject(netObj, owner.IsValid ? owner : Connection.None);
    }

    // Spawn an already-constructed NetworkObject (used by GameMode possession of a scene-placed pawn).
    internal NetworkObject SpawnObject(NetworkObject netObj, Connection owner) {
        if (netObj.IsSpawned)
            return netObj;

        netObj.NetId = nextNetId++;
        netObj.Owner = owner;
        netObj.Authority = ResolveAuthority(owner);
        netObj.IsSpawned = true;
        objects[netObj.NetId] = netObj;

        // Drive the net strand on EVERY NetworkBehaviour of the entity, in declaration order, before
        // any Unity strand. The caller (phase runner / Network.Spawn runtime path) handles the Unity
        // strand afterward with the suppression dance.
        DriveNetSpawnStrand(netObj.Entity);
        return netObj;
    }

    // P0 authority resolution (loopback-correct; P1 promotes to the per-machine §4d.1 truth-table):
    // the server/host always has State authority over what it spawns; the owning connection has Input
    // authority. On a host, an object owned by the local connection gets Both.
    NetworkAuthority ResolveAuthority(Connection owner) {
        NetworkAuthority a = NetworkAuthority.None;
        if (IsServer)
            a |= NetworkAuthority.State;
        bool ownedLocally = owner.IsValid && owner.Equals(LocalConnection);
        // A dedicated-server object with a remote owner has Input authority on the OWNER's machine,
        // not here; in loopback the owner IS the local connection, so Input lands locally. (P1 makes
        // this per-machine; P0 is single-process so "here" == "the owner's machine" when owned locally.)
        if (ownedLocally || (IsHost && owner.Equals(Connection.Local)))
            a |= NetworkAuthority.Input;
        return a;
    }

    static void DriveNetSpawnStrand(Entity entity) {
        foreach (Behaviour b in entity.Behaviours.ToArray())
            if (b is NetworkBehaviour nb)
                nb.DriveNetSpawn();
    }

    // Despawn (server-authoritative). Fires OnDespawned on every NetworkBehaviour, then frees the slot.
    public void Despawn(NetworkObject netObj) {
        if (netObj is null || !netObj.IsSpawned)
            return;
        foreach (Behaviour b in netObj.Entity.Behaviours.ToArray())
            if (b is NetworkBehaviour nb)
                nb.DriveNetDespawn();
        objects.Remove(netObj.NetId);
        netObj.IsSpawned = false;
        netObj.NetId = 0;
        netObj.Authority = NetworkAuthority.None;
    }

    internal NetworkObject Resolve(int netId) => objects.GetValueOrDefault(netId);
    public int SpawnedCount => objects.Count;
}
