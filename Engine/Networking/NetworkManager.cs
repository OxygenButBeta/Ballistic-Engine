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

    // CLIENT-side: the connection handle that addresses the SERVER (the server's peer id from this
    // client's view, captured on connect). A To.Server RPC sends here. On a host/server this is unused
    // (the server runs To.Server RPCs locally). Connection.None until connected.
    public Connection ServerConnection { get; private set; } = Connection.None;

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
        ServerConnection = Connection.None;
        objects.Clear();
        SendClock.Reset();
        StateSnapshotsSent = 0;
        InputStream.Reset();
        PredictionTicks = 0;
        snapshotWriter.Reset();
    }

    void WireTransport() {
        Transport.OnConnected = OnPeerConnected;
        Transport.OnDisconnected = OnPeerDisconnected;
        Transport.OnReceived = OnPayload;
    }

    // ---- connection registry (P3) -----------------------------------------------------------------
    // The server's list of connected clients (who to send snapshots/spawns to). On a host the local
    // client is NOT here (it shares the process — no wire send to self). A pure client has only the
    // server, addressed implicitly by Send to its single peer.
    readonly List<Connection> clients = new();
    public IReadOnlyList<Connection> Clients => clients;

    // The client-side MIRROR table (P3): netId -> the locally-built proxy object, so an incoming Snapshot
    // can find the object to apply state to, and a Despawn can tear it down. Distinct from `objects`
    // (which on the server is the authoritative set; on a pure client it IS the mirror set — a client
    // registers each mirror into `objects` too so NetworkRef resolves locally).
    // P3 over the wire fires OnPlayerJoined for a real connecting client (the §6 join flow = Phase 1).
    public Action<Connection> OnPlayerJoined { get; set; }

    // Raised when the handshake layout-digest mismatches (gate 0c) — a peer on a drifted build. The host
    // surfaces this as an explicit error instead of a silent desync (§8.6.1).
    public Action<Connection> OnLayoutMismatch { get; set; }

    void OnPeerConnected(Connection c) {
        if (IsServer) {
            // A client connected. Register it; the §6 join flow (GameMode.OnPlayerJoined = Phase 1 for a
            // late joiner) runs once the handshake validates — we wait for the client's Handshake message
            // so a drifted peer never reaches spawn. (The client sends Handshake immediately on connect.)
            if (!clients.Contains(c))
                clients.Add(c);
        }
        else {
            // Client connected to the server — remember the server's peer handle (a To.Server RPC sends
            // here), then send our layout digest so the server can reject drift.
            ServerConnection = c;
            Transport.Send(c, NetworkWire.Handshake(NetworkWire.LayoutDigest()), Channel.Reliable);
        }
    }

    void OnPeerDisconnected(Connection c) {
        clients.Remove(c);
        // P7 (ConnectionToken reconnect) keeps the pawn alive on a TTL; P3 just drops the registration.
    }

    void OnPayload(Connection source, ReadOnlySpan<byte> payload, Channel channel) {
        byte tag = NetworkWire.ReadTag(payload);
        var r = new BitReader(payload);
        r.ReadByte();   // consume the tag
        switch ((NetMessage)tag) {
            case NetMessage.Handshake:   HandleHandshake(source, ref r); break;
            case NetMessage.HandshakeOk: HandleHandshakeOk(source, ref r); break;
            case NetMessage.Spawn:       HandleSpawn(ref r); break;
            case NetMessage.Despawn:     HandleDespawn(ref r); break;
            case NetMessage.Snapshot:    HandleSnapshot(ref r); break;
            case NetMessage.Rpc:         HandleRpc(source, ref r); break;
        }
    }

    // SERVER: validate the client's layout digest (gate 0c). Match -> accept + run the §6 join flow;
    // mismatch -> explicit error, do NOT spawn (a silent desync is exactly what the guard prevents).
    void HandleHandshake(Connection source, ref BitReader r) {
        int clientDigest = r.ReadInt();
        if (clientDigest != NetworkWire.LayoutDigest()) {
            Debugging.LogError(
                $"Network: {source} rejected — [Networked] layout digest mismatch (drifted build). " +
                "A coordinated reload (all peers on the same build) is required (plan §8.6.1).");
            OnLayoutMismatch?.Invoke(source);
            return;
        }
        Transport.Send(source, NetworkWire.HandshakeOk(source.Id), Channel.Reliable);
        // The client passed the drift check — replicate every already-spawned object to it (late-join
        // baseline, the P6 path in miniature: a full Spawn per live object), then fire the join flow.
        foreach (NetworkObject obj in objects.All())
            SendSpawnTo(source, obj);
        OnPlayerJoined?.Invoke(source);
    }

    // CLIENT: the server accepted us; adopt the connection id it assigned (our LocalConnection).
    void HandleHandshakeOk(Connection source, ref BitReader r) {
        int assignedId = r.ReadInt();
        LocalConnection = new Connection(assignedId);
    }

    // CLIENT: build a mirror of a server-spawned object (typeId -> factory, no reflection). Owner +
    // authority resolve per the §4d.1 truth-table from THIS machine's view, so the owner sees an
    // AutonomousProxy and a watcher a SimulatedProxy (plan §6).
    void HandleSpawn(ref BitReader r) {
        int netId = r.ReadInt();
        int typeId = r.ReadInt();
        int ownerId = r.ReadInt();
        if (!NetworkReplicationRegistry.TryGet(typeId, out NetworkTypeDescriptor desc) || desc.ComponentType is null) {
            Debugging.LogError($"Network: spawn for unknown typeId {typeId} — no client type registered.");
            return;
        }
        Entity entity = Entity.Instantiate($"Net#{netId}");
        var mirror = (NetworkBehaviour)entity.AddComponent(desc.ComponentType);
        NetworkObject netObj = entity.GetComponent<NetworkObject>() ?? entity.AddComponent<NetworkObject>();

        var owner = new Connection(ownerId);
        netObj.Owner = owner;
        netObj.Authority = ResolveAuthority(Topology, LocalConnection, owner);
        netObj.IsSpawned = true;
        netObj.NetId = netId;
        objects.AddWithId(netId, netObj);   // register under the SERVER's netId so snapshots address it

        // Apply the full baseline that rode with the spawn, then drive the net strand (OnSpawned, §8.5).
        mirror.DeserializeState(ref r);
        mirror.CaptureNetworkBaseline();
        DriveNetSpawnStrand(netObj.Entity);
    }

    void HandleDespawn(ref BitReader r) {
        int netId = r.ReadInt();
        NetworkObject obj = objects.Resolve(netId);
        if (obj is null)
            return;
        foreach (Behaviour b in obj.Entity.Behaviours.ToArray())
            if (b is NetworkBehaviour nb)
                nb.DriveNetDespawn();
        objects.Remove(netId);
        obj.IsSpawned = false;
        SceneManager.GetCurrentScene()?.DestroyEntity(obj.Entity);
    }

    // CLIENT: apply a delta-snapshot batch — for each [netId, typeId, delta] find the mirror and apply.
    void HandleSnapshot(ref BitReader r) {
        while (!r.AtEnd && r.BitLength - r.BitPos >= 64) {   // at least netId+typeId remain
            int netId = r.ReadInt();
            int typeId = r.ReadInt();
            NetworkObject obj = objects.Resolve(netId);
            NetworkBehaviour target = FindNetworkBehaviour(obj, typeId);
            if (target is null) {
                // Unknown object (spawn not yet received / already despawned) — we cannot skip a
                // variable-length field block safely, so stop parsing this batch (snapshots are
                // Unreliable + latest-wins, so a dropped batch self-heals next send).
                break;
            }
            target.DeserializeState(ref r);
        }
    }

    static NetworkBehaviour FindNetworkBehaviour(NetworkObject obj, int typeId) {
        if (obj?.Entity is null)
            return null;
        foreach (Behaviour b in obj.Entity.Behaviours.ToArray())
            if (b is NetworkBehaviour nb && nb.HasNetworkedState && nb.NetworkTypeId == typeId)
                return nb;
        return null;
    }

    // ---- RPC send/receive (plan §4b / §9.5, P4) ---------------------------------------------------
    // The OUTGOING side — called by the generated partial-void stub body. It pre-packed the args into a
    // BitWriter; we decide FROM THIS MACHINE whether to execute the dev method locally and/or send a frame,
    // per the declared To.X target. The routing is the byte-for-byte logic proven in %TEMP%\bal-rpc-test.
    // No RPC return (L1): the stub is `void`; a request→response is RPC-up + [Networked] state-down.
    public void SendRpc(NetworkBehaviour self, int methodId, RpcTarget target, bool reliable,
        ReadOnlySpan<byte> args) {
        NetworkObject obj = self?.NetworkObject;
        if (obj is null || !obj.IsSpawned) {
            Debugging.LogWarning("Network.SendRpc on an unspawned object — ignored (RPCs are valid only while spawned).");
            return;
        }
        Channel channel = reliable ? Channel.Reliable : Channel.Unreliable;
        switch (target) {
            case RpcTarget.Server:
                // client → server. If WE are the server (host/dedicated), execute locally attributing the
                // local connection (the owner-check passes for a host's own object); else send UP.
                if (IsServer)
                    InvokeRpcLocally(self, methodId, target, args, caller: LocalConnection);
                else
                    Transport.Send(ServerConnection, NetworkWire.Rpc(obj.NetId, self.NetworkTypeId, methodId, args), channel);
                break;

            case RpcTarget.Owner:
                // server → the owning client only. A CLIENT calling this is misuse (only the server emits
                // owner/all RPCs) — drop + log, never send (closed trust boundary).
                if (!IsServer) { WarnClientCalledServerRpc(target); return; }
                if (obj.Owner.IsValid && obj.Owner.Equals(LocalConnection))
                    InvokeRpcLocally(self, methodId, target, args, caller: LocalConnection);  // host owns it
                else if (obj.Owner.IsValid)
                    Transport.Send(obj.Owner, NetworkWire.Rpc(obj.NetId, self.NetworkTypeId, methodId, args), channel);
                break;

            case RpcTarget.All:
                // server → every observing client AND run locally (the server is an observer too). A CLIENT
                // calling this is misuse — drop + log.
                if (!IsServer) { WarnClientCalledServerRpc(target); return; }
                InvokeRpcLocally(self, methodId, target, args, caller: LocalConnection);       // local run
                byte[] frame = NetworkWire.Rpc(obj.NetId, self.NetworkTypeId, methodId, args);
                foreach (Connection c in clients)
                    Transport.Send(c, frame, channel);
                break;
        }
    }

    // Execute an RPC on THIS machine (a host running its own To.Server/Owner/All, or the server's To.All
    // local run). Re-reads the freshly-packed args through the SAME invoker the wire arrival uses, so local
    // and remote execution are byte-identical. For a To.Server local run we still apply the owner-check so
    // the trust boundary is identical to the wire path (a host's local call passes: caller == owner).
    void InvokeRpcLocally(NetworkBehaviour self, int methodId, RpcTarget target, ReadOnlySpan<byte> args,
        Connection caller) {
        if (!NetworkReplicationRegistry.TryGetRpc(self.NetworkTypeId, methodId, out NetworkRpcEntry entry)) {
            Debugging.LogError($"Network: local RPC dispatch for unknown methodId {methodId} on {self.GetType().Name}.");
            return;
        }
        if (target == RpcTarget.Server && !OwnerCheckPasses(self.NetworkObject, caller)) {
            Debugging.LogWarning($"Network: To.Server RPC rejected — caller {caller} does not own the object.");
            return;
        }
        var r = new BitReader(args);
        self.RpcCaller = caller;
        try { entry.Invoke(self, ref r, caller); }
        catch (Exception e) { ScriptGuard.Report(self, "Rpc", e); }
        finally { self.RpcCaller = Connection.None; }
    }

    // The INCOMING side — a framed RPC arrived from `source`. Resolve the object + component mirror, look
    // the method up in the registry (reflection-free), enforce the owner-check for a client→server RPC,
    // then deserialize args + invoke. The methodId's DECLARED target drives the owner-check decision.
    void HandleRpc(Connection source, ref BitReader r) {
        int netId = r.ReadInt();
        int typeId = r.ReadInt();
        int methodId = r.ReadInt();
        NetworkObject obj = objects.Resolve(netId);
        NetworkBehaviour target = FindNetworkBehaviourForRpc(obj, typeId);
        if (target is null) {
            // No mirror (spawn not yet received / already despawned) — drop. RPCs ride Reliable so a true
            // ordering gap is rare; a despawn race is a benign drop (the object is gone).
            Debugging.LogWarning($"Network: RPC for unknown object netId {netId} typeId {typeId} — dropped.");
            return;
        }
        if (!NetworkReplicationRegistry.TryGetRpc(typeId, methodId, out NetworkRpcEntry entry)) {
            Debugging.LogWarning($"Network: RPC for unknown methodId {methodId} on typeId {typeId} — dropped.");
            return;
        }
        // The closed trust boundary (§4b/§9.5): a client→server RPC executes ONLY if `source` owns the
        // object. The server NEVER runs a client's To.Server RPC for an object it does not own.
        if (IsServer && entry.Target == RpcTarget.Server && !OwnerCheckPasses(obj, source)) {
            Debugging.LogWarning(
                $"Network: To.Server RPC '{methodId}' from {source} REJECTED — not the object's owner (cheat guard).");
            return;
        }
        target.RpcCaller = source;
        try { entry.Invoke(target, ref r, source); }
        catch (Exception e) { ScriptGuard.Report(target, "Rpc", e); }
        finally { target.RpcCaller = Connection.None; }
    }

    // An RPC may target a NetworkBehaviour with NO [Networked] state (RPC-only component), so this does NOT
    // gate on HasNetworkedState (unlike the snapshot lookup) — it matches purely on the wire typeId.
    static NetworkBehaviour FindNetworkBehaviourForRpc(NetworkObject obj, int typeId) {
        if (obj?.Entity is null)
            return null;
        foreach (Behaviour b in obj.Entity.Behaviours.ToArray())
            if (b is NetworkBehaviour nb && nb.NetworkTypeId == typeId)
                return nb;
        return null;
    }

    static bool OwnerCheckPasses(NetworkObject obj, Connection caller) =>
        obj is not null && obj.Owner.IsValid && obj.Owner.Equals(caller);

    static void WarnClientCalledServerRpc(RpcTarget target) =>
        Debugging.LogWarning(
            $"Network: a client called a To.{target} RPC — only the server sends Owner/All RPCs. Dropped (plan §4b).");

    // ---- the tick seam (plan §8.2) ----------------------------------------------------------------
    // The ASYMMETRIC send-rate clock (§8.2 / §14 item 3): simulate at the fixed 60 Hz step but flush
    // state DOWN only every Divisor-th tick (20 Hz). Input UP is per-tick (P5) — the up-stream lives on
    // the predicting client; P2 lays the down path so P5 can't inherit a conflated rate. Default 20 Hz.
    public SendRateClock SendClock { get; private set; } = new();

    // Count of state-DOWN snapshot flushes — the send cadence the harness asserts (proves the divisor).
    public int StateSnapshotsSent { get; private set; }

    // The per-tick UP stream — every fixed tick's input batched at the send cadence, never starved
    // (plan §8.2 / §14 item 3). P5a fills it from the owner's CapturePredictionInput; the actual wire
    // up-send is P5b's reconcile concern (the server needs every tick). Exposed counters let the harness
    // assert the asymmetric rate holds (per-tick record / batched flush).
    public InputUpStream InputStream { get; private set; } = new();

    // The monotonic LocalTick the prediction loop has advanced — the seq stamped on captured input and
    // the replay index (plan §8.2). Equals SendClock.LocalTick (one counter, no second clock).
    public uint LocalTick => (uint)SendClock.LocalTick;

    // The number of prediction ticks driven this session (the fixed-tick count the harness asserts is
    // bound to the 60 Hz accumulator, NOT the render frame rate — the L2 fix).
    public int PredictionTicks { get; private set; }

    // ---- the transport pump (once per FRAME) ------------------------------------------------------
    // Drains incoming / flushes outgoing on the socket — the IterateIncoming/IterateOutgoing brackets
    // (plan §8.2). Render-frame cadence is correct here (it is I/O, not simulation). The per-TICK
    // simulation work is PredictTick, bound to the fixed step.
    public void PollTransport() => Transport?.Poll();

    // ---- the prediction tick (once per FIXED STEP, plan §8.2) -------------------------------------
    // Bound to the existing 60 Hz accumulator via SceneManager's FixedTickScenes (L2 — no second clock).
    // The canonical bracket runs here (input capture → NetworkTick → down-flush), BEFORE the physics
    // step that follows in Physics.Advance. P5a:
    //   - the input authority (owner) CAPTURES input as data, buffers it by seq, predicts its own
    //     objects locally THIS tick (zero round-trip = zero input lag);
    //   - the server drives NetworkTick on the objects it has state authority over (authoritative sim);
    //   - the asymmetric down-state flush lands on the send boundary.
    // No server correction yet (P5b). Reflection-free: virtual NetworkTick calls + a cached owner walk.
    public void PredictTick(float step) {
        if (IsOffline)
            return;

        uint seq = (uint)SendClock.LocalTick;   // this tick's seq (the replay index, == LocalTick)
        PredictionTicks++;

        // OWNER PREDICTION: each input-authority object captures its tick input + predicts locally. We
        // drive it through the possessing PlayerController (which owns the InputComponent + buffer), then
        // run the object's NetworkTick so the predicted pawn integrates THIS tick's input immediately.
        foreach (NetworkObject obj in objects.All()) {
            if (obj?.Entity is null || !obj.HasInputAuthority)
                continue;
            CapturePredictionInputFor(obj, seq);
        }

        // NETWORK TICK dispatch (§4c — the single simulation step). On the state authority (server/host)
        // every spawned object ticks (authoritative). For an input-authority object on a pure client
        // (no state authority), the owner still ticks it locally — that IS the prediction. A proxy with
        // neither authority does NOT tick (it is interpolated, P5c). Deduped so a host (both authorities)
        // ticks each object exactly once.
        foreach (NetworkObject obj in objects.All()) {
            if (obj?.Entity is null)
                continue;
            bool shouldTick = obj.HasStateAuthority || obj.HasInputAuthority;
            if (shouldTick)
                DriveNetworkTick(obj);
        }

        // Asymmetric UP: record EVERY tick's input (never gated by the divisor — the load-bearing rule),
        // flush the batch on the send boundary. The wire up-send is P5b; P5a proves the rate is honored.
        InputStream.RecordInput(seq);

        // State DOWN on the send boundary (the divisor cadence) — pack each dirty object's delta snapshot
        // and flush to clients. Input UP flushes on the SAME boundary but carries all buffered ticks.
        bool sendBoundary = SendClock.Advance();
        if (sendBoundary) {
            if (IsServer)
                FlushStateDown();
            InputStream.FlushBatch();
        }
    }

    // Drive the prediction input capture for one input-authority object via its possessing controller.
    // A PlayerController owns the InputComponent + InputBuffer; a possessed Pawn defers to its Controller.
    // The captured input also feeds the UP stream's per-tick record (above). No reflection — a typed walk.
    void CapturePredictionInputFor(NetworkObject obj, uint seq) {
        foreach (Behaviour b in obj.Entity.Behaviours.ToArray()) {
            if (b is PlayerController pc) {
                pc.CapturePredictionInput(seq);
                return;
            }
            // A possessed Pawn routes through its Controller (the input owner) — capture once there.
            if (b is Pawn { Controller: { } controller })
                controller.CapturePredictionInput(seq);
        }
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
    // captured baseline, §11) into one payload, then re-baseline. P3 frames this behind a Snapshot tag and
    // sends it Unreliable to every client. CaptureNetworkBaseline runs AFTER the write so the next send
    // diffs against what we just sent.
    //
    // P3 SCOPE BOUNDARY (the ONE global-baseline limitation, by design): the baseline is per-OBJECT and
    // GLOBAL, not per-client. A late joiner gets a FULL spawn snapshot (SendSpawnTo) at join, then rides
    // the shared delta stream — correct for a single observer (proven in %TEMP%\bal-net-twoproc). With
    // MULTIPLE clients joining at different times, a per-CLIENT ack baseline is needed so each gets exactly
    // the deltas since ITS last ack — that is explicitly P6 (late-join baseline, §13). P3 demonstrates the
    // wire path; P6 makes the baseline per-client. Documented so a future session doesn't mistake the
    // global baseline for a bug.
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
        StateSnapshotsSent++;
        if (clients.Count == 0)
            return;   // no remote observers (a loopback host shares the process — nothing to send to self)

        // Frame the delta batch behind a Snapshot tag and send it Unreliable (latest-wins, §12.1) to every
        // observing client. SerializeStateSnapshot re-baselines as it writes, so the NEXT flush deltas
        // against what we just sent (the last-ack model; per-client ack tracking is P6).
        ReadOnlySpan<byte> batch = SerializeStateSnapshot();
        if (LastSnapshotObjectCount == 0)
            return;   // nothing dirty this send — skip the packet entirely
        byte[] framed = new byte[batch.Length + 1];
        framed[0] = (byte)NetMessage.Snapshot;
        batch.CopyTo(framed.AsSpan(1));
        foreach (Connection c in clients)
            Transport.Send(c, framed, Channel.Unreliable);
    }

    // Send a full Spawn of one object to one client (the join-baseline + each new spawn). Reliable —
    // a missed spawn means the client never builds the mirror. Walks the entity's NetworkBehaviours and
    // sends a Spawn per state-carrying component (P3: one component per object is the common case).
    void SendSpawnTo(Connection client, NetworkObject obj) {
        if (obj?.Entity is null)
            return;
        foreach (Behaviour b in obj.Entity.Behaviours.ToArray())
            if (b is NetworkBehaviour nb && nb.HasNetworkedState)
                Transport.Send(client, NetworkWire.Spawn(obj.NetId, nb.NetworkTypeId, obj.Owner.Id, nb),
                    Channel.Reliable);
    }

    // Broadcast a spawn to every connected client (called from the server spawn path).
    void BroadcastSpawn(NetworkObject obj) {
        foreach (Connection c in clients)
            SendSpawnTo(c, obj);
    }

    // Broadcast a despawn (Reliable) so every client tears its mirror down.
    void BroadcastDespawn(int netId) {
        if (clients.Count == 0)
            return;
        byte[] msg = NetworkWire.Despawn(netId);
        foreach (Connection c in clients)
            Transport.Send(c, msg, Channel.Reliable);
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

        // Replicate the spawn to every connected client (P3): a full Spawn so each builds the mirror
        // (owner->AutonomousProxy, others->SimulatedProxy per the §4d.1 table, resolved on each machine).
        // The Spawn carries a FULL snapshot; capture the baseline right after so the next delta-snapshot
        // diffs against it. A loopback host has no remote clients, so this is a no-op there (SP path).
        BroadcastSpawn(netObj);
        foreach (Behaviour b in netObj.Entity.Behaviours.ToArray())
            if (b is NetworkBehaviour { HasNetworkedState: true } nb)
                nb.CaptureNetworkBaseline();
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
        int netId = netObj.NetId;
        BroadcastDespawn(netId);   // tell clients FIRST (while NetId is still valid) to tear the mirror down
        foreach (Behaviour b in netObj.Entity.Behaviours.ToArray())
            if (b is NetworkBehaviour nb)
                nb.DriveNetDespawn();
        objects.Remove(netId);   // bumps the slot generation -> stale NetworkRefs null out
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
