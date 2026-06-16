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

    // netId -> object, a GENERATIONAL slot array (§8.4) so NetworkRef.Value resolves in O(1) with a
    // generation check — the mechanism that makes "null on despawn" real. Internal (no public netId, §3).
    readonly NetworkObjectRegistry objects = new();

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
        SendClock.Reset();
        StateSnapshotsSent = 0;
        snapshotWriter.Reset();
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
    // The ASYMMETRIC send-rate clock (§8.2 / §14 item 3): simulate at the fixed 60 Hz step but flush
    // state DOWN only every Divisor-th tick (20 Hz). Input UP is per-tick (P5) — the up-stream lives on
    // the predicting client; P2 lays the down path so P5 can't inherit a conflated rate. Default 20 Hz.
    public SendRateClock SendClock { get; private set; } = new();

    // Count of state-DOWN snapshot flushes — the send cadence the harness asserts (proves the divisor).
    public int StateSnapshotsSent { get; private set; }

    // Drains incoming, runs NetworkTick on the state authority, and flushes a state snapshot DOWN on the
    // send boundary (the divisor cadence). Called once per fixed step by SceneManager. P0 only pumped the
    // transport; P2 adds the NetworkTick dispatch + the asymmetric down-state flush.
    public void Tick() {
        Transport?.Poll();
        if (IsOffline)
            return;

        // The single simulation step (§4c) — only on the state authority (the server/host). A proxy does
        // not mutate state (P5 adds owner prediction). Reflection-free: a virtual call per spawned object.
        if (IsServer)
            foreach (NetworkObject obj in objects.All())
                DriveNetworkTick(obj);

        // State DOWN on the send boundary (the divisor cadence) — pack each dirty object's delta snapshot.
        // P2 builds + measures the snapshot over loopback; the wire send is P3. The asymmetry (down here,
        // input up per-tick on the client) is correct FROM THE START (the §14-item-3 functional guard).
        bool sendBoundary = SendClock.Advance();
        if (sendBoundary && IsServer)
            FlushStateDown();
    }

    static void DriveNetworkTick(NetworkObject obj) {
        if (obj?.Entity is null)
            return;
        foreach (Behaviour b in obj.Entity.Behaviours.ToArray())
            if (b is NetworkBehaviour nb)
                try { nb.NetworkTick(); }
                catch (Exception e) { ScriptGuard.Report(nb, "NetworkTick", e); }
    }

    // Serialize every spawned, state-carrying object's DELTA snapshot (changemask + changed fields vs its
    // captured baseline, §11) into one payload, then re-baseline. P2 over loopback: build + measure + (in
    // a host) apply locally is a no-op since server==client; the real cross-peer apply is P3. Returns the
    // packed payload so the harness can assert size/cadence. CaptureNetworkBaseline runs AFTER the write so
    // the next tick diffs against what we just sent (the last-ack model; true ack tracking is P3/P6).
    BitWriter snapshotWriter = new();

    public ReadOnlySpan<byte> SerializeStateSnapshot() {
        snapshotWriter.Reset();
        int written = 0;
        foreach (NetworkObject obj in objects.All()) {
            if (obj?.Entity is null)
                continue;
            foreach (Behaviour b in obj.Entity.Behaviours.ToArray()) {
                if (b is NetworkBehaviour nb && nb.HasNetworkedState) {
                    snapshotWriter.WriteInt(obj.NetId);           // which object (P3 maps this on the wire)
                    snapshotWriter.WriteInt(nb.NetworkTypeId);    // which component type
                    nb.SerializeState(snapshotWriter);            // changemask + changed fields
                    nb.CaptureNetworkBaseline();                  // re-baseline for the next delta
                    written++;
                }
            }
        }
        LastSnapshotObjectCount = written;
        return snapshotWriter.AsSpan();
    }

    public int LastSnapshotObjectCount { get; private set; }

    void FlushStateDown() {
        ReadOnlySpan<byte> snapshot = SerializeStateSnapshot();
        StateSnapshotsSent++;
        // P3: Transport.Send(client, snapshot, Channel.Unreliable) to each observer. P2 (loopback host):
        // server and client are the same process, so there is nothing to send to — the snapshot is built
        // and measured (proving the cadence + the format), and the down-apply is exercised in the harness.
        _ = snapshot;
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

        netObj.Owner = owner;
        netObj.Authority = ResolveAuthority(Topology, LocalConnection, owner);
        netObj.IsSpawned = true;
        netObj.NetId = objects.Add(netObj);   // packed (slot, generation) — the NetworkRef handle key

        // Drive the net strand on EVERY NetworkBehaviour of the entity, in declaration order, before
        // any Unity strand. The caller (phase runner / Network.Spawn runtime path) handles the Unity
        // strand afterward with the suppression dance.
        DriveNetSpawnStrand(netObj.Entity);
        return netObj;
    }

    // The §4d.1 truth-table, as a PURE function of (this machine's topology, this machine's connection,
    // the object's owner). One place authority is decided, for every machine — so when P3 brings real
    // remote peers, each computes its OWN role by calling this with its own (topology, localConn). The
    // two orthogonal axes (L3), never collapsed:
    //   State authority — the SERVER (and a host, which IS a server) owns the truth. A pure client never
    //                     has it. This is why IsProxy is false on a host for EVERYTHING (the host-corner).
    //   Input authority — the machine whose local connection == the object's owner drives its input.
    //                     On a dedicated server an object owned by a remote client gives Input to that
    //                     client's machine, NOT the server. A server-owned (None) object gives Input to
    //                     nobody. A host owning an object locally gets BOTH (its own pawn).
    internal static NetworkAuthority ResolveAuthority(
        NetworkTopology topology, Connection localConnection, Connection owner) {
        NetworkAuthority a = NetworkAuthority.None;

        // State: the server/host owns truth for everything it tracks.
        if (topology is NetworkTopology.Server or NetworkTopology.Host)
            a |= NetworkAuthority.State;

        // Input: this machine is the owning connection. Connection.None (server-owned) is owned by no
        // machine, so no Input authority anywhere. A host's local connection is Connection.Local.
        if (owner.IsValid && owner.Equals(localConnection))
            a |= NetworkAuthority.Input;

        return a;
    }

    static void DriveNetSpawnStrand(Entity entity) {
        foreach (Behaviour b in entity.Behaviours.ToArray())
            if (b is NetworkBehaviour nb)
                nb.DriveNetSpawn();
    }

    // Despawn (server-authoritative). Fires OnDespawned on every NetworkBehaviour, then frees the slot —
    // bumping its generation so every NetworkRef to this identity now reads null (§8.4).
    public void Despawn(NetworkObject netObj) {
        if (netObj is null || !netObj.IsSpawned)
            return;
        foreach (Behaviour b in netObj.Entity.Behaviours.ToArray())
            if (b is NetworkBehaviour nb)
                nb.DriveNetDespawn();
        objects.Remove(netObj.NetId);   // bumps the slot generation -> stale NetworkRefs null out
        netObj.IsSpawned = false;
        netObj.NetId = 0;
        netObj.Authority = NetworkAuthority.None;
        netObj.Owner = Connection.None;
    }

    // ---- ownership transfer (server-only, replicated; plan §4d) -----------------------------------
    // Move INPUT authority to a new connection at runtime — pick-up items, vehicle-enter, detachable
    // turrets. Server-only to call (a client can't grant itself ownership — the closed trust boundary);
    // the change replicates and fires OnOwnershipChanged on affected peers. P1: the local re-resolve +
    // callback; the replication of the change rides the wire in P3.
    public void TransferOwnership(NetworkObject netObj, Connection newOwner) {
        if (netObj is null || !netObj.IsSpawned)
            return;
        if (!IsServer) {
            Debugging.LogWarning("TransferOwnership is server-only — a client cannot grant itself ownership.");
            return;
        }
        Connection prev = netObj.Owner;
        if (prev.Equals(newOwner))
            return;
        netObj.Owner = newOwner;
        netObj.Authority = ResolveAuthority(Topology, LocalConnection, newOwner);
        FireOwnershipChanged(netObj, prev, newOwner);
    }

    // Drop ownership back to the server (Connection.None) — the orphan/return case.
    public void RemoveOwnership(NetworkObject netObj) => TransferOwnership(netObj, Connection.None);

    static void FireOwnershipChanged(NetworkObject netObj, Connection prev, Connection next) {
        foreach (Behaviour b in netObj.Entity.Behaviours.ToArray())
            if (b is NetworkBehaviour nb)
                nb.DriveOwnershipChanged(prev, next);
    }

    internal NetworkObject Resolve(int netId) => objects.Resolve(netId);
    public int SpawnedCount => objects.Count;
}
