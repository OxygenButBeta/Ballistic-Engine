using System.Numerics;
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
        clientState.Clear();
        predicted.Clear();
        nextPredictKey = 1;
        SendClock.Reset();
        StateSnapshotsSent = 0;
        InputStream.Reset();
        PredictionTicks = 0;
        IsReplaying = false;
        LastServerTick = 0;   // P8a: the client's view of the server tick (drives RenderTick)
        snapshotWriter.Reset();
        sceneStateWriter.Reset();   // P7: the entity-less GameState flush buffer
        connectionTokens.Clear();   // P7: reconnect bookkeeping
        orphans.Clear();
        // PersistentToken is NOT cleared here — a reconnect flow sets it before StartClient, and Stop runs
        // BEFORE StartClient (StartClient calls Stop first); clearing it would erase the reclaim token. The
        // facade's SetReconnectToken sets it after Stop, so the next connect presents it.
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

    // P6 PER-CLIENT replication state (plan §13 late-join): connection -> its delta baseline + pending +
    // send sequence. Created at join (HandleHandshake), dropped at disconnect. SERVER-side only. The
    // FlushStateDown loop diffs each object vs THIS client's baseline (the baseline-swap), so staggered
    // joiners each get exactly the deltas since THEIR last ack (proven in %TEMP%\bal-baseline-test 16/16).
    readonly Dictionary<Connection, ClientReplState> clientState = new();

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
            // here), then send our layout digest + (P7) our PERSISTENT ConnectionToken so the server can
            // reject drift AND reclaim our orphaned pawn on a reconnect. None on a first join (the server
            // mints one we then persist via HandshakeOk).
            ServerConnection = c;
            Transport.Send(c, NetworkWire.Handshake(NetworkWire.LayoutDigest(), PersistentToken), Channel.Reliable);
        }
    }

    // SERVER: on a peer disconnect, KEEP the player's pawn(s) spawned and record a reconnect ORPHAN keyed
    // by the connection's ConnectionToken with a TTL (plan §8.5.5, the approved decision). Ownership ->
    // server (OnOwnershipChanged fires; OnSpawned subscriptions survive — NOT despawn+respawn). A reconnect
    // presenting the same token reclaims the pawn (HandleHandshake); an expired orphan is swept (despawned).
    // The reclaim/TTL ALGORITHM was proven in %TEMP%\bal-reconnect-test before this integration.
    void OnPeerDisconnected(Connection c) {
        clients.Remove(c);
        clientState.Remove(c);   // drop this client's per-client baseline/pending (no leak, no send)

        if (IsServer && connectionTokens.TryGetValue(c, out ConnectionToken token) && token.IsValid)
            RecordReconnectOrphan(c, token);
        connectionTokens.Remove(c);
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
            case NetMessage.Input:       HandleInput(source, ref r); break;
            case NetMessage.Ack:         HandleAck(source, ref r); break;
            case NetMessage.Possess:     HandlePossess(ref r); break;
            case NetMessage.SceneState:  HandleSceneState(source, ref r); break;
            case NetMessage.SceneAck:    HandleSceneAck(source, ref r); break;
        }
    }

    // SERVER (P5b): a client's input batch arrived UP. Enqueue each input on the target object's inbox
    // (consumed one per tick by ApplyServerInput, in seq order). Owner-checked: a client may only send
    // input for an object it owns (the closed trust boundary — a client can't drive someone else's pawn).
    void HandleInput(Connection source, ref BitReader r) {
        int netId = r.ReadInt();
        int count = r.ReadByte();
        NetworkObject obj = objects.Resolve(netId);
        // Validate ownership BEFORE buffering (don't let a non-owner inject input). Still parse the bytes
        // so the reader stays aligned for any following frame (frames are sent one-per-payload here, so a
        // bad one is simply dropped, but we read its inputs out regardless).
        bool accept = obj is not null && obj.IsSpawned && obj.Owner.IsValid && obj.Owner.Equals(source);
        if (accept)
            obj.ServerInputInbox ??= new Queue<NetworkInput>();
        for (int i = 0; i < count; i++) {
            NetworkInput input = NetworkInput.Read(ref r);
            if (accept && input.Seq > obj.LastProcessedSeq)
                obj.ServerInputInbox.Enqueue(input);
        }
        if (!accept)
            Debugging.LogWarning($"Network: input batch for object {netId} from {source} rejected (not the owner).");
    }

    // SERVER: validate the client's layout digest (gate 0c). Match -> accept + run the §6 join flow;
    // mismatch -> explicit error, do NOT spawn (a silent desync is exactly what the guard prevents).
    void HandleHandshake(Connection source, ref BitReader r) {
        int clientDigest = r.ReadInt();
        ConnectionToken presented = NetworkWire.ReadToken(ref r);   // P7: the client's persistent token
        if (clientDigest != NetworkWire.LayoutDigest()) {
            Debugging.LogError(
                $"Network: {source} rejected — [Networked] layout digest mismatch (drifted build). " +
                "A coordinated reload (all peers on the same build) is required (plan §8.6.1).");
            OnLayoutMismatch?.Invoke(source);
            return;
        }

        // P7 RECONNECT (§8.5.5): if the client presented a token that matches a live reconnect orphan,
        // RECLAIM — transfer the orphaned pawn's ownership BACK to this NEW connection (the auto-reclaim
        // default, user-approved), clear the orphan, and DON'T re-spawn (the pawn stayed spawned, so
        // OnSpawned subscriptions are intact). A first join (None / unknown token) mints a fresh token the
        // client persists for a future reconnect. Either way `token` is the identity the server now uses.
        ConnectionToken token = presented;
        bool reclaimed = false;
        if (presented.IsValid && TryReclaimOrphan(source, presented))
            reclaimed = true;
        else if (!presented.IsValid)
            token = MintToken();
        connectionTokens[source] = token;

        Transport.Send(source, NetworkWire.HandshakeOk(source.Id, token), Channel.Reliable);

        // P6: create this client's per-client replication state. A late joiner gets CURRENT state
        // ATOMICALLY — a full Spawn per live object (the baseline delivered at spawn, §8.5) — and the
        // server SEEDS the client's delta baseline to exactly those current values, so its first delta
        // diffs against what it actually holds (not a zero/global baseline that would re-send everything
        // or, worse, skip a change it never saw). This is the §13 "late joiner gets current state
        // atomically at spawn"; the per-client baseline (proven in %TEMP%\bal-baseline-test) makes
        // staggered joins each correct.
        var state = new ClientReplState();
        clientState[source] = state;
        foreach (NetworkObject obj in objects.All()) {
            SendSpawnTo(source, obj);
            SeedClientBaseline(state, obj);
        }
        // P6 possession-replication late-join: replay the current possession links so a joiner sees who
        // controls what (and, if it owns a controller, auto-builds its input). Spawns went first (above) so
        // the controller/pawn mirrors exist when these Reliable Possess messages apply.
        SendPossessionsTo(source);

        // P7 GameState late-join (§13 atomic): send the CURRENT GameState as a full snapshot + seed this
        // client's GameState baseline to those values, so its first scene-state delta diffs against what it
        // holds (it missed nothing). Reliable — the entity-less twin of the per-object spawn baseline above.
        SeedAndSendSceneStateTo(source, state);

        OnPlayerJoined?.Invoke(source);
    }

    // Seed (or re-seed) one client's per-client baseline for an object to its CURRENT [Networked] values —
    // the late-join / new-spawn baseline. Captures the live values into the component's __netBaseline first
    // (so the token reflects the current state, not a stale one), then snapshots the token per component.
    static void SeedClientBaseline(ClientReplState state, NetworkObject obj) {
        if (obj?.Entity is null)
            return;
        foreach (Behaviour b in obj.Entity.Behaviours.ToArray())
            if (b is NetworkBehaviour { HasNetworkedState: true } nb) {
                nb.CaptureNetworkBaseline();                  // __netBaseline := live values
                state.SeedBaseline(obj.NetId, nb.__GetNetBaseline());   // token snapshot of the current state
            }
    }

    // P6 SERVER: a client acked a snapshot frontier — advance THAT client's per-client baseline (promote
    // everything sent at seq <= acked into the acked baseline), so the next delta diffs against what the
    // client now holds. A dropped (unacked) snapshot never advances the baseline, so its change re-sends
    // next flush (latest-wins recovery). Server-only; a client ignores acks (it never sends snapshots).
    void HandleAck(Connection source, ref BitReader r) {
        uint ackedSeq = r.ReadUInt();
        if (clientState.TryGetValue(source, out ClientReplState state))
            state.Ack(ackedSeq);
    }

    // CLIENT: the server accepted us; adopt the connection id it assigned (our LocalConnection) AND (P7)
    // PERSIST the ConnectionToken the server is using for us — so a future reconnect presents it to reclaim
    // our pawn (§8.5.5). On a first join this is the freshly-minted token; on a reconnect it echoes the one
    // we presented. A real client would write this to disk / a session store; here it lives on the manager.
    void HandleHandshakeOk(Connection source, ref BitReader r) {
        int assignedId = r.ReadInt();
        LocalConnection = new Connection(assignedId);
        PersistentToken = NetworkWire.ReadToken(ref r);
    }

    // CLIENT: build a mirror of a server-spawned object (typeId -> factory, no reflection). Owner +
    // authority resolve per the §4d.1 truth-table from THIS machine's view, so the owner sees an
    // AutonomousProxy and a watcher a SimulatedProxy (plan §6).
    void HandleSpawn(ref BitReader r) {
        int netId = r.ReadInt();
        int typeId = r.ReadInt();
        int ownerId = r.ReadInt();
        uint predictKey = r.ReadUInt();   // P5f: non-zero echoes a client's predicted spawn (§8.5.1)
        if (!NetworkReplicationRegistry.TryGet(typeId, out NetworkTypeDescriptor desc) || desc.ComponentType is null) {
            Debugging.LogError($"Network: spawn for unknown typeId {typeId} — no client type registered.");
            return;
        }

        // P5f CONFIRM-LINK: if this authoritative spawn echoes a key we predicted, LINK it to the existing
        // predicted object — adopt the netId, clear the prediction, DO NOT build a duplicate or re-fire
        // OnSpawned (it already fired on the predicted copy). The §8.5.1 reconcile-link.
        if (predictKey != 0 && predicted.TryGetValue(predictKey, out NetworkObject pred) && pred.Entity is not null) {
            predicted.Remove(predictKey);
            pred.PredictKey = 0;
            pred.PredictConfirmDeadline = 0;
            pred.NetId = netId;
            pred.Owner = new Connection(ownerId);
            pred.Authority = ResolveAuthority(Topology, LocalConnection, pred.Owner);
            objects.AddWithId(netId, pred);   // now addressable by the server's netId
            // Apply the authoritative baseline onto the predicted instance + re-baseline. No OnSpawned.
            foreach (Behaviour b in pred.Entity.Behaviours.ToArray())
                if (b is NetworkBehaviour { HasNetworkedState: true } nb && nb.NetworkTypeId == typeId) {
                    nb.DeserializeState(ref r);
                    try { nb.OnStateApplied(); } catch (Exception e) { ScriptGuard.Report(nb, "OnStateApplied", e); }
                    nb.CaptureNetworkBaseline();
                }
            return;
        }

        // Normal (non-predicted) spawn — build a fresh mirror.
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

    // CLIENT: apply a delta-snapshot batch — for each [netId, lastProcessedSeq, typeId, delta] find the
    // mirror, apply the authoritative state, then (P5b) RECONCILE input-authority objects: snap to the
    // server state (just applied), TRIM acked inputs, REPLAY the unacknowledged ones. A SimulatedProxy
    // (no input authority) just takes the state as-is (interpolation is P5c).
    void HandleSnapshot(ref BitReader r) {
        uint snapshotSeq = r.ReadUInt();   // P6: the per-client send frontier — ACK it so the server
                                           // advances OUR baseline (after applying the batch below).
        LastServerTick = r.ReadUInt();     // P8a: the server tick this snapshot reflects — drives RenderTick.
        while (!r.AtEnd && r.BitLength - r.BitPos >= 96) {   // at least netId+seq+typeId remain
            int netId = r.ReadInt();
            uint lastProcessedSeq = r.ReadUInt();   // P5b: the server's ack frontier for this object
            int typeId = r.ReadInt();
            NetworkObject obj = objects.Resolve(netId);
            NetworkBehaviour target = FindNetworkBehaviour(obj, typeId);
            if (target is null) {
                // Unknown object (spawn not yet received / already despawned) — we cannot skip a
                // variable-length field block safely, so stop parsing this batch (snapshots are
                // Unreliable + latest-wins, so a dropped batch self-heals next send).
                break;
            }
            target.DeserializeState(ref r);   // SNAP: apply the authoritative state (step 1 of reconcile)
            try { target.OnStateApplied(); }  // map the just-applied state onto presentation (transform)
            catch (Exception e) { ScriptGuard.Report(target, "OnStateApplied", e); }

            // P5b RECONCILE + P5d SMOOTHING: only the AUTONOMOUS PROXY (the owning client) trims + replays.
            // The server snapshot just overwrote the predicted state with truth; now re-derive the present
            // from the in-flight (unacked) inputs, then ease in any misprediction error.
            if (obj.HasInputAuthority && !obj.HasStateAuthority) {
                obj.LastProcessedSeq = lastProcessedSeq;
                PlayerController pc = FindController(obj);
                if (pc is not null) {
                    // P5d: capture what the render showed BEFORE the correction; run the reconcile under the
                    // IsReplaying flag (so [OnChanged] stays silent during re-derivation, not a real change);
                    // then set the smoother so a misprediction eases in over the next frames, not a pop.
                    Vector3 renderedBefore = obj.Entity?.transform.Position ?? Vector3.Zero;
                    IsReplaying = true;
                    pc.Reconcile(lastProcessedSeq, _ => DriveNetworkTick(obj));
                    IsReplaying = false;
                    if (obj.Entity is not null) {
                        obj.Smoother ??= new PredictionSmoother();
                        obj.Smoother.OnCorrection(renderedBefore, obj.Entity.transform.Position);
                    }
                }
            }
            // P5c INTERPOLATION: a SimulatedProxy (neither authority) does NOT simulate — it BUFFERS the
            // just-applied pose for smooth interpolation. The state-apply above may have moved the
            // transform (if the game maps state->transform) or not; either way we snapshot the transform
            // RESULT, so interpolation is decoupled from how the game writes its pose. PredictTick then
            // renders the lerped pose each tick. Stamp with the proxy's current interp clock.
            else if (obj.IsSimulatedProxy && obj.Entity is not null) {
                obj.Interpolator ??= new SnapshotInterpolator();
                Transform tr = obj.Entity.transform;
                obj.Interpolator.Receive(obj.InterpClock, tr.Position, tr.Rotation);
            }
        }

        // P6: ACK the per-client frontier so the server advances OUR baseline (the next delta diffs against
        // what we now hold). Reliable-ordered — a lost ack just means the server re-sends the unacked delta
        // until an ack lands (latest-wins recovery). A host's local client never reaches here (no wire send
        // to self); only a real remote client acks. Guarded on a valid server connection.
        if (ServerConnection.IsValid)
            Transport.Send(ServerConnection, NetworkWire.Ack(snapshotSeq), Channel.Reliable);
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

    // ReplicateState (P5d, plan §4b/§13 — Fusion's current-vs-replayed distinction): true ONLY during a
    // reconcile's replay sub-ticks. [OnChanged] handlers check this and stay SILENT during replay — the
    // state is being re-derived from buffered inputs, not genuinely changing, so a naive per-set callback
    // would fire spuriously every rollback. The generated [OnChanged] dispatch (and game code) reads this.
    public bool IsReplaying { get; private set; }

    // ---- lag compensation (P8a, plan §9 item 9 / §13) ---------------------------------------------
    // CLIENT side: the latest server tick observed in a snapshot (HandleSnapshot reads it). RenderTick =
    // this − InterpDelayTicks is the PAST server-moment the client's screen shows (it renders proxies
    // InterpDelay back, P5c) — the time a hitscan shot carries UP so the server rewinds to what the shooter
    // saw (§9.9). 0 before the first snapshot.
    public uint LastServerTick { get; private set; }

    // The interpolation delay (ticks) the client renders proxies behind the latest server tick — MUST match
    // the SnapshotInterpolator's InterpDelay (P5c) so RenderTick lines up with what was actually drawn. The
    // shot's render-tick is derived from it, so it's the lag-comp/interp coupling knob.
    public double InterpDelayTicks { get; set; } = SnapshotInterpolator.DefaultInterpDelayTicks;

    // SERVER side: how far back (ticks) a client may ask to rewind. Caps an abusive claim (a cheat asking to
    // rewind 10s to snipe a target that was elsewhere long ago) AND bounds the PoseHistory ring length.
    // ~1s at 60Hz covers any realistic RTT+interp; tune per game.
    public int MaxRewindTicks { get; set; } = 60;

    // The current authoritative server tick (the fixed-step counter). On a client this trails via LastServerTick.
    public uint ServerTick => (uint)SendClock.LocalTick;

    // ---- interest management (P8b, plan §14 item 14) ----------------------------------------------
    // OFF by default — every object is relevant to every client (the per-client flush is byte-identical to
    // pre-P8b). Turn ON to cull replication per connection by area-of-interest (a spatial bubble around each
    // client's owned pawn) — a scale/bandwidth subsystem (NOT hit-detection, which is P8a). The APPROVED
    // decision (§14 item 14 (b)): an AOI transition fires OnInterestLost/Gained — NOT despawn (relevancy !=
    // disconnect; the object stays spawned, subscriptions intact). Proven in %TEMP%\bal-interest-test.
    public bool InterestManagement { get; set; }

    // The default AOI radius for an object whose RelevancyRadius is 0 (the common per-game bubble). Tune per
    // game; an AlwaysRelevant object ignores it entirely.
    public float DefaultRelevancyRadius { get; set; } = 50f;

    // The view position of a connection's AOI = its owned pawn's transform position (a reflection-free walk
    // over spawned objects for one owned by `c` with an entity). Connection.None (a connection with no pawn
    // yet) has no view → everything non-AlwaysRelevant is out of its interest until it owns a pawn. Cheap
    // (a handful of owned objects); runs only on the send boundary when interest management is on.
    bool TryConnectionView(Connection c, out Vector3 view) {
        foreach (NetworkObject obj in objects.All())
            if (obj is { IsSpawned: true } && obj.Entity is not null && obj.Owner.IsValid && obj.Owner.Equals(c)) {
                view = obj.Entity.transform.Position;
                return true;
            }
        view = default;
        return false;
    }

    // Is `obj` relevant to connection `c` right now? AlwaysRelevant bypasses AOI; an object the connection
    // OWNS is always relevant (you always replicate a client its own pawn); otherwise within the (object or
    // default) radius of the connection's view. A connection with no view (no pawn yet) sees only
    // AlwaysRelevant + owned objects.
    bool RelevantTo(Connection c, NetworkObject obj) {
        bool owned = obj.Owner.IsValid && obj.Owner.Equals(c);
        bool hasView = TryConnectionView(c, out Vector3 view);
        float radius = obj.RelevancyRadius > 0f ? obj.RelevancyRadius : DefaultRelevancyRadius;
        return IsRelevantPure(obj.AlwaysRelevant, owned, hasView, view,
            obj.Entity?.transform.Position ?? default, radius);
    }

    // The relevancy DECISION as a pure function (the ResolveAuthority pattern — one place the rule lives,
    // exposed via Network.IsRelevant so a tool/test verifies it without a live registry, and the live path
    // calls the SAME function so it never drifts). AlwaysRelevant OR owned-by-the-viewer => relevant; else,
    // only if the viewer has a pawn (a view) AND the object is within radius of it. A viewer with no pawn
    // sees only AlwaysRelevant + owned objects.
    internal static bool IsRelevantPure(bool alwaysRelevant, bool ownedByViewer, bool hasView,
        Vector3 view, Vector3 objectPos, float radius) {
        if (alwaysRelevant || ownedByViewer)
            return true;
        if (!hasView)
            return false;
        return (objectPos - view).LengthSquared() <= radius * radius;
    }

    // SERVER: recompute every connected client's relevancy set and fire OnInterestLost/Gained on the DIFF —
    // the §14-item-14 transition events (NOT despawn). A newly-relevant object is flagged for a full re-seed
    // on the next flush (its baseline for this client is stale/absent — the late-join machinery). Called on
    // the send boundary, before FlushStateDown, only when interest management is on. Reflection-free.
    void EvaluateInterest() {
        foreach (Connection c in clients) {
            if (!clientState.TryGetValue(c, out ClientReplState state))
                continue;
            // gained: in `objects`, relevant now, not in the previous set.
            foreach (NetworkObject obj in objects.All()) {
                if (obj is not { IsSpawned: true } || obj.Entity is null)
                    continue;
                bool now = RelevantTo(c, obj);
                bool was = state.Relevant.Contains(obj.NetId);
                if (now && !was) {
                    state.Relevant.Add(obj.NetId);
                    state.ReseedOnRegain.Add(obj.NetId);   // stale baseline -> full re-seed next flush
                    DriveInterest(obj, gained: true);
                }
                else if (!now && was) {
                    state.Relevant.Remove(obj.NetId);
                    DriveInterest(obj, gained: false);
                }
            }
        }
    }

    static void DriveInterest(NetworkObject obj, bool gained) {
        foreach (Behaviour b in obj.Entity.Behaviours.ToArray())
            if (b is NetworkBehaviour nb) {
                if (gained) nb.DriveInterestGained();
                else nb.DriveInterestLost();
            }
    }

    // Observability (P8b): is `obj` currently in `c`'s area of interest (the relevancy frontier)? A
    // tool/test reads it. False when interest management is off would be misleading, so it reports the
    // tracked set (which is only populated while management is on).
    public bool IsInInterest(Connection c, NetworkObject obj) =>
        obj is not null && clientState.TryGetValue(c, out ClientReplState s) && s.Relevant.Contains(obj.NetId);

    // The render-tick a LOCAL shot should carry UP (CLIENT): the past server-moment the screen showed. On a
    // host/server this is just the current tick (no interp delay — the host renders the authoritative present).
    public double RenderTick => IsServer ? ServerTick : Math.Max(0, (double)LastServerTick - InterpDelayTicks);

    // SERVER: record every lag-compensated object's CURRENT pose into its PoseHistory for THIS tick, so a
    // later shot can rewind it (favor-the-shooter). Called once per fixed tick from PredictTick (server-only).
    // Only objects that opted in (LagHitboxRadius > 0) are tracked — most objects pay nothing. Reflection-free:
    // a typed walk + a transform read. The ring is sized to MaxRewindTicks (+ a small margin for the
    // fractional render-tick bracket) so it never grows unbounded.
    void RecordLagCompHistory() {
        uint tick = (uint)SendClock.LocalTick;
        foreach (NetworkObject obj in objects.All()) {
            if (obj is not { IsSpawned: true, IsLagCompensated: true } || obj.Entity is null)
                continue;
            obj.LagHistory ??= new PoseHistory(MaxRewindTicks + 4);
            obj.LagHistory.Record(tick, obj.Entity.transform.Position);
        }
    }

    // The server caps how far back a client may rewind (anti-abuse) and never rewinds into the future.
    // Returns the effective render-tick used. Public so a tool/test can predict the clamp.
    public double ClampRenderTick(double renderTick) {
        double now = SendClock.LocalTick;
        double oldest = now - MaxRewindTicks;
        if (renderTick < oldest) return oldest;
        if (renderTick > now) return now;   // clock skew / cheat — never rewind forward
        return renderTick;
    }

    // SERVER: a lag-compensated hitscan raycast (plan §9.9 / §13 P8a — favor-the-shooter). Rewinds every
    // OTHER lag-compensated pawn's hitbox to the pose it interpolates at the (clamped) renderTick, runs a
    // ray-vs-hitbox test against the rewound world, then RESTORES the live poses — so the shot is resolved
    // as the shooter SAW it, not against where the targets have since moved. The shooter's OWN pawn is never
    // rewound. Returns the nearest hit. Call from inside a To.Server shot RPC (where RenderTick rode up).
    //
    // NOTE the rewind is against a dedicated SPHERE hitbox (LagHitboxRadius), NOT the Bepu world — Bepu only
    // syncs body poses at fixed-step boundaries and needs GL, so a transform-rewound Physics.Raycast wouldn't
    // see the moved body and wouldn't run headless. A dedicated hitbox is also how real lag-comp works (it
    // rewinds hitboxes, not the whole physics scene) and keeps this deterministic + headless-verifiable.
    public bool LagCompensatedRaycast(Vector3 origin, Vector3 direction, double renderTick,
        NetworkObject shooter, out LagRaycastHit hit) {
        hit = default;
        if (!IsServer || direction == Vector3.Zero)
            return false;
        Vector3 dir = Vector3.Normalize(direction);
        double effective = ClampRenderTick(renderTick);

        NetworkObject best = null;
        float bestT = float.MaxValue;
        Vector3 bestPoint = default;
        foreach (NetworkObject obj in objects.All()) {
            if (obj is not { IsSpawned: true, IsLagCompensated: true } || obj.Entity is null)
                continue;
            if (ReferenceEquals(obj, shooter))
                continue;   // never rewind/hit the shooter's own pawn

            // REWIND: the hitbox center is the pose this pawn occupied at the render-tick (its history),
            // not the live transform. No mutation of the transform — we test against the sampled point, so
            // there is nothing to restore (the §3 "restore" check is structurally satisfied — the live pose
            // is never touched). The history may be empty (just spawned) → fall back to the live pose.
            Vector3 center = obj.LagHistory is { Count: > 0 } h ? h.SampleAt(effective)
                                                                : obj.Entity.transform.Position;
            if (LagHitbox.RaySphere(origin, dir, center, obj.LagHitboxRadius, out float t) && t < bestT) {
                bestT = t; best = obj; bestPoint = origin + dir * t;
            }
        }
        if (best is null)
            return false;
        hit = new LagRaycastHit(best, bestT, bestPoint);
        return true;
    }

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

        // P8a: record every lag-compensated pawn's pose for THIS tick BEFORE the sim moves them, so a shot
        // arriving later can rewind to where targets were when the shooter saw them (server-only, §9.9).
        if (IsServer)
            RecordLagCompHistory();

        foreach (NetworkObject obj in objects.All()) {
            if (obj?.Entity is null)
                continue;

            if (obj.HasInputAuthority) {
                // OWNER PREDICTION (the AutonomousProxy, or a host's own pawn): capture this tick's input
                // as data, buffer it by seq, predict locally THIS tick (zero round-trip = zero input lag).
                CapturePredictionInputFor(obj, seq);
                DriveNetworkTick(obj);
                // P5d: ease in any pending misprediction correction (a decaying render offset on the
                // transform) so a server correction doesn't pop — bounded per-frame step. No-op when the
                // prediction was correct (offset 0). Only meaningful on a true AutonomousProxy (a host owns
                // truth, so its predictions are authoritative — no correction to smooth).
                if (obj.Smoother is { IsActive: true } && !obj.HasStateAuthority && obj.Entity is not null) {
                    Vector3 off = obj.Smoother.Decay();
                    obj.Entity.transform.Position += off;
                }
                // CLIENT (not the server): send the input UP so the server can authoritatively simulate it.
                if (!IsServer)
                    SendInputUp(obj);
            }
            else if (obj.HasStateAuthority) {
                // SERVER, an object it owns truth for but does NOT input (a remote client's pawn): consume
                // the next received input from the inbox (one per tick — the authoritative cadence), feed
                // it to the controller's CurrentInput, then NetworkTick. Input-starved => re-apply the last
                // (extrapolate) so the sim keeps moving (plan §8.2). Server-owned (no owner) world/AI
                // objects have no inbox — they just tick. This is the authoritative half of the reconcile.
                ApplyServerInput(obj);
                DriveNetworkTick(obj);
            }
            else {
                // SimulatedProxy (neither authority) — INTERPOLATED, never simulated locally (P5c). Advance
                // the proxy's interp clock and render the remote pose InterpDelay ticks in the past, lerping
                // between buffered snapshots — smooth under loss/jitter. The transform write IS the render.
                InterpolateProxy(obj);
            }
        }

        // P5f: roll back any predicted spawn whose confirm window expired (a rejected/mispredicted shot's
        // bullet vanishes cleanly — OnDespawned, no orphan). A confirm (HandleSpawn link) removes it first.
        SweepPredictedRollbacks();

        // P7: sweep any reconnect orphan whose TTL expired (the player did not come back) — despawn its
        // held pawn(s). A reconnect within the window already reclaimed + cleared it (HandleHandshake).
        if (IsServer)
            SweepReconnectOrphans();

        // Asymmetric UP: record EVERY tick's input on the local owner stream (the per-tick contract; the
        // actual per-object wire send happened above). FlushBatch on the boundary keeps the stream bounded.
        InputStream.RecordInput(seq);

        // State DOWN on the send boundary (the divisor cadence) — pack each dirty object's delta snapshot
        // (now carrying lastProcessedSeq for the reconcile) and flush to clients.
        bool sendBoundary = SendClock.Advance();
        if (sendBoundary) {
            if (IsServer) {
                if (InterestManagement)
                    EvaluateInterest();   // P8b: recompute relevancy + fire OnInterestLost/Gained BEFORE the
                                          // flush (so this send respects the just-updated per-client sets)
                FlushStateDown();
            }
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

    // SERVER side (P5b): consume the next buffered input for an object the server owns truth for but a
    // remote client inputs. One per tick (the authoritative cadence); skip already-processed seqs (a
    // re-sent batch can carry old ones). Input-starved => re-apply the last (extrapolate). Sets the
    // possessing controller's CurrentInput so the pawn's NetworkTick reads the authoritative input, and
    // stamps LastProcessedSeq so the snapshot DOWN tells the client what to ack.
    void ApplyServerInput(NetworkObject obj) {
        PlayerController pc = FindController(obj);
        if (pc is null)
            return;   // a server-owned world/AI object with no controller — nothing to apply, just ticks
        Queue<NetworkInput> inbox = obj.ServerInputInbox;
        if (inbox is not null) {
            while (inbox.Count > 0 && inbox.Peek().Seq <= obj.LastProcessedSeq)
                inbox.Dequeue();   // drop already-processed (re-sent under loss)
            if (inbox.Count > 0) {
                NetworkInput input = inbox.Dequeue();
                obj.LastServerInput = input;
                obj.HaveLastServerInput = true;
                obj.LastProcessedSeq = input.Seq;
                pc.SetServerInput(input);
                return;
            }
        }
        // input-starved: extrapolate with the last known input (do NOT advance LastProcessedSeq).
        if (obj.HaveLastServerInput)
            pc.SetServerInput(obj.LastServerInput);
    }

    // Send the owner's buffered input batch UP to the server (P5b). The batch carries EVERY tick since the
    // last send (asymmetric up-rate: per-tick recorded, batched, none dropped). Reliable-ordered so the
    // server is never input-starved. Only on the send boundary (the divisor cadence) — see PredictTick's
    // caller; here we send whatever the controller buffered that the server hasn't acked.
    void SendInputUp(NetworkObject obj) {
        if (!SendClock.IsBoundary || !ServerConnection.IsValid)
            return;
        PlayerController pc = FindController(obj);
        if (pc?.InputBuffer is null)
            return;
        // Send the unacked window (everything past the server's last ack). InputBuffer holds exactly the
        // unacked inputs after each reconcile trims it, so send them all — the server dedups by seq.
        NetworkInput[] batch = pc.InputBuffer.InOrder().ToArray();
        if (batch.Length == 0)
            return;
        Transport.Send(ServerConnection, NetworkWire.Input(obj.NetId, batch), Channel.Reliable);
    }

    // P5c: advance a SimulatedProxy's interp clock and apply the interpolated remote pose to its transform.
    // The proxy renders InterpDelay ticks in the PAST (between two buffered snapshots) — smooth even when
    // snapshots arrive irregularly under loss/jitter. Until the buffer has data the transform is untouched
    // (it sits at its spawn pose). The transform write here is the proxy's entire per-tick work.
    static void InterpolateProxy(NetworkObject obj) {
        if (obj.Entity is null || obj.Interpolator is null)
            return;
        obj.InterpClock += 1.0;   // one interp-clock tick per fixed step (the local render time axis)
        if (obj.Interpolator.TrySample(obj.InterpClock, out var pos, out var rot)) {
            Transform tr = obj.Entity.transform;
            tr.Position = pos;
            tr.Rotation = rot;
        }
        obj.Interpolator.Trim(obj.InterpClock);
    }

    static PlayerController FindController(NetworkObject obj) {
        if (obj?.Entity is null)
            return null;
        foreach (Behaviour b in obj.Entity.Behaviours.ToArray()) {
            if (b is PlayerController pc)
                return pc;
            if (b is Pawn { Controller: { } controller })
                return controller;
        }
        return null;
    }

    static void DriveNetworkTick(NetworkObject obj) {
        if (obj?.Entity is null)
            return;
        foreach (Behaviour b in obj.Entity.Behaviours.ToArray())
            if (b is NetworkBehaviour nb)
                try { nb.NetworkTick(); }
                catch (Exception e) { ScriptGuard.Report(nb, "NetworkTick", e); }
    }

    // P6 PER-CLIENT delta snapshot (plan §13 late-join): build a snapshot batch FOR ONE CLIENT, diffing
    // each object against THAT client's acked baseline (the baseline-swap: __SetNetBaseline the client's
    // saved token -> SerializeState produces the client's delta -> __GetNetBaseline records what the client
    // will hold once it acks, stashed as pending under this send's seq). An object whose live state equals
    // the client's baseline is OMITTED entirely (the strongest 1-bit-unchanged: a quiescent object costs 0).
    //
    // This REPLACED P3's single global SerializeStateSnapshot. The global baseline was correct for ONE
    // observer but wrong for staggered joiners (a delta sent only to client A advanced the shared baseline
    // so client B never learned it). The per-client model gives each client exactly the deltas since ITS
    // last ack — proven necessary (the global model demonstrably fails the staggered case) in
    // %TEMP%\bal-baseline-test (16/16). The frame leads with the per-client send SEQ the client echoes in
    // its Ack; the server advances the client's baseline only on that ack.
    BitWriter snapshotWriter = new();

    // The bytes of the LAST per-client snapshot built (diagnostics/harness). Object count = how many rode.
    public int LastSnapshotObjectCount { get; private set; }

    // Legacy/diagnostic single-baseline snapshot (P2): serialize every state-carrying object's delta vs its
    // OWN __netBaseline, then re-baseline. NOT the per-client send path (that is SerializeStateSnapshotFor)
    // — kept for the P2.5 "the snapshot includes the spawned object" in-engine check and any tool that wants
    // a quick global dump. The real wire path is per-client (FlushStateDown). No per-client seq header.
    public ReadOnlySpan<byte> SerializeStateSnapshot() {
        snapshotWriter.Reset();
        int written = 0;
        foreach (NetworkObject obj in objects.All()) {
            if (obj?.Entity is null)
                continue;
            foreach (Behaviour b in obj.Entity.Behaviours.ToArray()) {
                if (b is NetworkBehaviour { HasNetworkedState: true } nb) {
                    snapshotWriter.WriteInt(obj.NetId);
                    snapshotWriter.WriteUInt(obj.LastProcessedSeq);
                    snapshotWriter.WriteInt(nb.NetworkTypeId);
                    nb.SerializeState(snapshotWriter);
                    nb.CaptureNetworkBaseline();
                    written++;
                }
            }
        }
        LastSnapshotObjectCount = written;
        return snapshotWriter.AsSpan();
    }

    ReadOnlySpan<byte> SerializeStateSnapshotFor(ClientReplState state, uint sendSeq) {
        snapshotWriter.Reset();
        snapshotWriter.WriteUInt(sendSeq);                  // P6: the per-client frontier the client acks
        snapshotWriter.WriteUInt((uint)SendClock.LocalTick);  // P8a: the server tick this snapshot reflects —
                                                            // the client tracks it so RenderTick = serverTick −
                                                            // InterpDelay is the past server-moment its screen
                                                            // shows, which a lag-comp shot carries up (§9.9).
        int written = 0;
        Dictionary<int, object> sentThisSeq = null;
        foreach (NetworkObject obj in objects.All()) {
            if (obj?.Entity is null)
                continue;
            // P8b: when interest management is on, CULL an out-of-interest object entirely (0 bytes for this
            // client — the per-connection AOI filter). Relevant is the just-evaluated set (EvaluateInterest
            // ran before this flush). When management is off, Relevant is unused and every object passes.
            if (InterestManagement && !state.Relevant.Contains(obj.NetId))
                continue;
            // P8b: did this object just RE-ENTER interest? If so, it changed while culled, so this client's
            // baseline is stale — send a FULL snapshot (the late-join re-seed), NOT a delta against the stale
            // baseline (which would silently skip the missed change). Cleared once sent.
            bool reseed = InterestManagement && state.ReseedOnRegain.Remove(obj.NetId);
            foreach (Behaviour b in obj.Entity.Behaviours.ToArray()) {
                if (b is not NetworkBehaviour { HasNetworkedState: true } nb)
                    continue;

                if (reseed) {
                    // Full snapshot on re-gain — every field, ignoring the stale per-client baseline. Then
                    // capture the baseline = live so subsequent deltas diff against what the client now holds.
                    snapshotWriter.WriteInt(obj.NetId);
                    snapshotWriter.WriteUInt(obj.LastProcessedSeq);
                    snapshotWriter.WriteInt(nb.NetworkTypeId);
                    nb.SerializeFullState(snapshotWriter);
                    nb.CaptureNetworkBaseline();
                    (sentThisSeq ??= new())[obj.NetId] = nb.__GetNetBaseline();
                    written++;
                    continue;
                }

                // Swap in THIS client's baseline for the object so SerializeState diffs against it. A client
                // that has never seen this object (a brand-new spawn the seed-on-spawn path didn't precede)
                // has no baseline token -> leave the component's own baseline (the swap is a no-op), which is
                // safe: the seed-on-spawn path normally sets it; absent that the delta is vs whatever the
                // component holds, and the change still rides.
                object clientBaseline = state.Baseline.TryGetValue(obj.NetId, out object t) ? t : null;
                if (clientBaseline is not null)
                    nb.__SetNetBaseline(clientBaseline);

                // Skip an object that is QUIESCENT for this client (live == its acked baseline) — the
                // strongest 1-bit-unchanged form (the whole object costs 0). A reflection-free typed compare
                // the generator emits; no probe/rewind. Then serialize the delta + record the post-send token.
                if (clientBaseline is not null && nb.__NetStateEquals(clientBaseline))
                    continue;

                snapshotWriter.WriteInt(obj.NetId);
                snapshotWriter.WriteUInt(obj.LastProcessedSeq);   // P5b: ack frontier for the reconcile
                snapshotWriter.WriteInt(nb.NetworkTypeId);
                nb.SerializeState(snapshotWriter);                // changemask + changed fields vs client baseline

                // Record what this client WILL hold once it acks this seq (the post-send token). Capture the
                // baseline = live so __GetNetBaseline returns the just-sent values; promoted into the client's
                // acked baseline on its Ack.
                nb.CaptureNetworkBaseline();
                (sentThisSeq ??= new())[obj.NetId] = nb.__GetNetBaseline();
                written++;
            }
        }
        if (sentThisSeq is not null)
            state.Pending[sendSeq] = sentThisSeq;
        LastSnapshotObjectCount = written;
        return snapshotWriter.AsSpan();
    }

    void FlushStateDown() {
        StateSnapshotsSent++;
        if (clients.Count == 0)
            return;   // no remote observers (a loopback host shares the process — nothing to send to self)

        // Build + send a SEPARATE per-client delta frame (each diffed against that client's acked baseline).
        // Unreliable (latest-wins, §12.1) — a dropped frame's change re-sends next flush since the client's
        // baseline only advances on its Ack. A client with nothing dirty for it gets no packet.
        foreach (Connection c in clients) {
            if (!clientState.TryGetValue(c, out ClientReplState state))
                continue;   // not yet handshaken (no baseline) — it gets its full state at join
            uint sendSeq = state.SendSeq + 1;
            ReadOnlySpan<byte> batch = SerializeStateSnapshotFor(state, sendSeq);
            if (LastSnapshotObjectCount == 0)
                continue;   // nothing changed for this client this send — skip the packet entirely
            state.SendSeq = sendSeq;
            byte[] framed = new byte[batch.Length + 1];
            framed[0] = (byte)NetMessage.Snapshot;
            batch.CopyTo(framed.AsSpan(1));
            Transport.Send(c, framed, Channel.Unreliable);
        }

        FlushSceneStateDown();   // P7: the entity-less GameState carve-out, same per-client cadence
    }

    // ---- P7: entity-less GameState replication (the §2/§10 carve-out) ------------------------------
    // GameState is the ONE type that replicates WITHOUT a NetworkObject/netId. The network tick collects
    // every active IReplicated SceneBehaviour, addressed by a small fixed ReplicationId (assigned from the
    // deterministic active order — AssignReplicationIds), and runs the SAME per-client baseline flush the
    // entity path uses (over ClientReplState.SceneBaseline, a SEPARATE id space). Proven entity-less in
    // %TEMP%\bal-scenestate-test (21/21) before this integration; the wire shape is byte-identical to the
    // NetworkBehaviour delta, so the per-client baseline + the layout-digest drift guard carry over.

    // Collect the active IReplicated scene-behaviours of the current scene (today: GameState). Cheap: a
    // single SceneBehaviours walk, no per-tick reflection (the type test is a pattern match). Ordered by
    // a stable key so ReplicationId is deterministic across machines (both ends see the same id->object).
    static readonly List<IReplicated> replScratch = new();
    static List<IReplicated> CollectSceneReplicated() {
        replScratch.Clear();
        Scene scene = SceneManager.GetCurrentScene();
        if (scene is null)
            return replScratch;
        foreach (SceneBehaviour sb in scene.SceneBehaviours)
            if (sb is IReplicated { HasReplicatedState: true } r)
                replScratch.Add(r);
        // Deterministic order: by ReplicationTypeId then type name — stable across machines (a scene has at
        // most a handful of IReplicated, so the sort is negligible and runs only on the send boundary).
        replScratch.Sort((a, b) => {
            int c = a.ReplicationTypeId.CompareTo(b.ReplicationTypeId);
            return c != 0 ? c : string.CompareOrdinal(a.GetType().FullName, b.GetType().FullName);
        });
        return replScratch;
    }

    // Assign each active IReplicated its ReplicationId from the deterministic order (idempotent — re-runs
    // each collection, stable result). GameState gets id 0, a 2nd IReplicated id 1, etc. Both ends run the
    // SAME assignment so a [replicationId][delta] block dispatches to the matching object without a handshake.
    static void AssignReplicationIds(List<IReplicated> list) {
        for (int i = 0; i < list.Count; i++)
            if (list[i] is GameState gs)
                gs.ReplicationId = i;
            else
                SetReplicationId(list[i], i);   // a future non-GameState IReplicated — reflection-free seam
    }

    // A non-GameState IReplicated would expose its own internal setter; today GameState is the only one, so
    // this is a guarded no-op (the id is read-only on the interface). Kept so adding a 2nd IReplicated type
    // is a one-line change here, not a redesign.
    static void SetReplicationId(IReplicated r, int id) { /* GameState is the only impl in P7 */ }

    // Per-client GameState delta flush — the entity-less twin of SerializeStateSnapshotFor. Swap in this
    // client's GameState baseline, skip a quiescent object (0 bytes), else serialize the delta + record the
    // post-send token under this scene-seq's pending. Returns the block count. Frame: [sceneSeq][count] then
    // [replicationId][delta] per dirty object.
    //
    // The frame leads with an explicit block COUNT (not AtEnd-driven parsing) because the bit-packed deltas
    // are NOT byte-aligned — byte-padding after the last block could otherwise read as a phantom id-0 block
    // (and id 0 IS GameState). The count makes the receive loop exact. Two-pass: build blocks into a scratch
    // writer (we don't know the count until we've diffed each object), then frame [seq][count][blocks].
    static readonly BitWriter sceneBlocks = new();
    int SerializeSceneStateFor(ClientReplState state, uint sceneSeq, List<IReplicated> list) {
        sceneBlocks.Reset();
        int written = 0;
        Dictionary<int, object> sentThisSeq = null;
        foreach (IReplicated r in list) {
            object clientBaseline = state.SceneBaseline.TryGetValue(r.ReplicationId, out object t) ? t : null;
            if (clientBaseline is not null)
                r.__SetReplBaseline(clientBaseline);
            if (clientBaseline is not null && r.__ReplStateEquals(clientBaseline))
                continue;   // quiescent for this client (the strongest 1-bit-unchanged: 0 bytes)

            sceneBlocks.WriteInt(r.ReplicationId);
            r.Serialize(sceneBlocks);                   // changemask + changed fields vs the client baseline
            r.CaptureReplBaseline();                    // baseline := live -> __GetReplBaseline = the just-sent values
            (sentThisSeq ??= new())[r.ReplicationId] = r.__GetReplBaseline();
            written++;
        }
        if (sentThisSeq is not null)
            state.ScenePending[sceneSeq] = sentThisSeq;

        sceneStateWriter.Reset();
        sceneStateWriter.WriteUInt(sceneSeq);
        sceneStateWriter.WriteInt(written);             // the explicit block count (exact receive loop)
        ReadOnlySpan<byte> blocks = sceneBlocks.AsSpan();
        // Append the bit-packed block bytes after the byte-aligned header. The header is whole 32-bit words,
        // so the blocks resume byte-aligned (a block's first read is the 32-bit replicationId, then the
        // bit-packed delta — the receiver reads exactly `written` blocks, never a phantom).
        for (int i = 0; i < blocks.Length; i++)
            sceneStateWriter.WriteByte(blocks[i]);
        return written;
    }

    BitWriter sceneStateWriter = new();

    void FlushSceneStateDown() {
        List<IReplicated> list = CollectSceneReplicated();
        if (list.Count == 0 || clients.Count == 0)
            return;
        AssignReplicationIds(list);
        foreach (Connection c in clients) {
            if (!clientState.TryGetValue(c, out ClientReplState state))
                continue;
            uint sceneSeq = state.SceneSendSeq + 1;
            int blocks = SerializeSceneStateFor(state, sceneSeq, list);
            if (blocks == 0)
                continue;   // nothing changed for this client — skip the packet
            state.SceneSendSeq = sceneSeq;
            ReadOnlySpan<byte> batch = sceneStateWriter.AsSpan();
            byte[] framed = new byte[batch.Length + 1];
            framed[0] = (byte)NetMessage.SceneState;
            batch.CopyTo(framed.AsSpan(1));
            Transport.Send(c, framed, Channel.Unreliable);   // latest-wins; a drop re-sends (baseline un-advanced)
        }
    }

    // Seed (or re-seed) one client's GameState baseline to the CURRENT values for every active IReplicated
    // (the late-join atomic snapshot — §13). Captures live into the baseline first so the token reflects
    // current state, then sends a FULL snapshot block per object Reliable (so the joiner holds it before any
    // delta) and records the token. Called from the join flow.
    void SeedAndSendSceneStateTo(Connection client, ClientReplState state) {
        List<IReplicated> list = CollectSceneReplicated();
        if (list.Count == 0)
            return;
        AssignReplicationIds(list);
        var w = new BitWriter();
        w.WriteByte((byte)NetMessage.SceneState);
        w.WriteUInt(0);             // the join snapshot rides seq 0 (the client acks it -> baseline established)
        w.WriteInt(list.Count);     // the explicit block count (every object: a FULL snapshot)
        foreach (IReplicated r in list) {
            w.WriteInt(r.ReplicationId);
            r.SerializeFull(w);
            r.CaptureReplBaseline();
            state.SeedScene(r.ReplicationId, r.__GetReplBaseline());
        }
        Transport.Send(client, w.AsSpan().ToArray(), Channel.Reliable);   // atomic, Reliable (a join baseline)
    }

    // CLIENT: apply a GameState scene-state batch — dispatch each [replicationId][delta] block onto the
    // matching local IReplicated (by ReplicationId, NOT order — both ends assign the same ids), then ACK so
    // the server advances OUR GameState baseline. The frame leads with an explicit block COUNT so parsing is
    // exact (the bit-packed deltas are not byte-aligned; an AtEnd loop could read phantom byte-padding as a
    // bogus id-0 block). An unknown id stops parsing (a block is variable-length, can't skip) — latest-wins
    // self-heals on the next send (the join Reliable snapshot establishes the ids first in practice).
    void HandleSceneState(Connection source, ref BitReader r) {
        uint sceneSeq = r.ReadUInt();
        int count = r.ReadInt();
        List<IReplicated> list = CollectSceneReplicated();
        AssignReplicationIds(list);
        for (int b = 0; b < count; b++) {
            int id = r.ReadInt();
            IReplicated target = null;
            foreach (IReplicated x in list)
                if (x.ReplicationId == id) { target = x; break; }
            if (target is null)
                break;   // unknown id — can't skip a variable-length block; stop (self-heals next send)
            target.Deserialize(ref r);
        }
        if (ServerConnection.IsValid)
            Transport.Send(ServerConnection, NetworkWire.SceneAck(sceneSeq), Channel.Reliable);
    }

    // SERVER: a client acked the GameState frontier — advance THAT client's GameState baseline (its own seq
    // space). Server-only (a client never sends scene-state down).
    void HandleSceneAck(Connection source, ref BitReader r) {
        uint ackedSeq = r.ReadUInt();
        if (clientState.TryGetValue(source, out ClientReplState state))
            state.AckScene(ackedSeq);
    }

    // Send a full Spawn of one object to one client (the join-baseline + each new spawn). Reliable —
    // a missed spawn means the client never builds the mirror. Walks the entity's NetworkBehaviours and
    // sends a Spawn per REGISTERED component (NetworkTypeId != 0). P6: this now includes a no-[Networked]
    // mirror-able type (a partial PlayerController/Pawn) so possession-replication can build the right
    // controller type on the client via typeId->factory — not just state-carrying components (P3's filter).
    // SerializeFullState is a no-op for a no-state type, so the frame just carries an empty baseline.
    // Contract: one registered NetworkBehaviour per NetworkObject (pawn and controller are SEPARATE objects).
    void SendSpawnTo(Connection client, NetworkObject obj, uint predictKey = 0) {
        if (obj?.Entity is null)
            return;
        // P5f: only the OWNING client gets the echoed prediction key (it holds the predicted object to
        // link); other observers get key 0 (a normal spawn — they never predicted it). This prevents a
        // non-owner from spuriously linking an unrelated predicted object to the same key.
        uint keyForClient = predictKey != 0 && obj.Owner.IsValid && obj.Owner.Equals(client) ? predictKey : 0u;
        foreach (Behaviour b in obj.Entity.Behaviours.ToArray())
            if (b is NetworkBehaviour nb && nb.NetworkTypeId != 0)
                Transport.Send(client, NetworkWire.Spawn(obj.NetId, nb.NetworkTypeId, obj.Owner.Id, nb, keyForClient),
                    Channel.Reliable);
    }

    // Broadcast a spawn to every connected client (called from the server spawn path). P6: seed each
    // client's per-client baseline to the new object's current values (the spawn carried a FULL snapshot,
    // so the client now holds those values — the first delta must diff against them, not a default/global
    // baseline). Mirrors the join path (HandleHandshake seeds existing objects; this seeds a NEW one for
    // already-connected clients).
    void BroadcastSpawn(NetworkObject obj, uint predictKey = 0) {
        foreach (Connection c in clients) {
            SendSpawnTo(c, obj, predictKey);
            if (clientState.TryGetValue(c, out ClientReplState state))
                SeedClientBaseline(state, obj);
        }
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

    // ---- predicted spawn (P5f, §8.5.1) ------------------------------------------------------------
    // The CLIENT-side predicted spawn table: prediction key -> the local predicted object awaiting an
    // authoritative confirm. HandleSpawn links/clears; the rollback sweep destroys expired ones.
    readonly Dictionary<uint, NetworkObject> predicted = new();
    uint nextPredictKey = 1;
    const int PredictRollbackWindowTicks = 30;   // ~500ms at 60Hz — confirm within this, else roll back

    // Spawn a PREDICTED object on the CLIENT, INSTANTLY, with no server baseline (§8.5.1 — the one place
    // "OnSpawned == baseline delivered" does not hold). Mints a prediction key, marks the object predicted,
    // drives OnSpawned ONCE locally, and registers it by key. The triggering RPC (e.g. a To.Server Fire)
    // must carry the returned key UP so the server can echo it on the authoritative spawn → the client
    // LINKS rather than duplicating (HandleSpawn). If no confirm arrives within the rollback window the
    // object is destroyed (a mispredicted/rejected shot). Returns (object, key).
    //
    // Server/host: a predicted spawn is just a real spawn (it already IS the authority) — so on a host this
    // delegates to Spawn and returns key 0 (no prediction needed; nothing to reconcile).
    public (NetworkObject obj, uint key) PredictSpawn(Entity entity, Connection owner = default) {
        if (entity is null)
            throw new ArgumentNullException(nameof(entity));
        if (IsServer) {
            // The authority does not predict — it spawns for real and there is no key to reconcile.
            return (Spawn(entity, owner), 0u);
        }
        NetworkObject netObj = entity.GetComponent<NetworkObject>() ?? entity.AddComponent<NetworkObject>();
        uint key = nextPredictKey++;
        netObj.PredictKey = key;
        netObj.PredictConfirmDeadline = SendClock.LocalTick + PredictRollbackWindowTicks;
        netObj.Owner = LocalConnection;                       // the predicting client owns its prediction
        netObj.Authority = ResolveAuthority(Topology, LocalConnection, LocalConnection);
        netObj.IsSpawned = true;                              // locally live (no netId until confirmed)
        predicted[key] = netObj;
        DriveNetSpawnStrand(netObj.Entity);                   // OnSpawned fires ONCE on the predicted copy
        return (netObj, key);
    }

    // Roll back any predicted spawn whose confirm window expired (the server never echoed its key — the
    // shot was rejected / mispredicted). Destroys the predicted object cleanly (OnDespawned). Called once
    // per fixed tick from PredictTick. No orphan, no duplicate.
    void SweepPredictedRollbacks() {
        if (predicted.Count == 0)
            return;
        List<uint> expired = null;
        foreach (var kv in predicted)
            if (SendClock.LocalTick >= kv.Value.PredictConfirmDeadline) {
                (expired ??= new()).Add(kv.Key);
            }
        if (expired is null)
            return;
        foreach (uint key in expired) {
            NetworkObject obj = predicted[key];
            predicted.Remove(key);
            foreach (Behaviour b in obj.Entity.Behaviours.ToArray())
                if (b is NetworkBehaviour nb)
                    nb.DriveNetDespawn();                     // OnDespawned on rollback
            obj.IsSpawned = false;
            obj.PredictKey = 0;
            SceneManager.GetCurrentScene()?.DestroyEntity(obj.Entity);
        }
    }

    public int PendingPredictedSpawns => predicted.Count;

    // SERVER (P5f): spawn the authoritative object in response to a client's PREDICTED spawn, ECHOING the
    // prediction key the triggering RPC carried up. The owning client matches the key and LINKS this spawn
    // to its predicted object (no duplicate). Called from inside a To.Server RPC impl where RpcCaller is
    // the firing client (the natural owner). key 0 would be a normal spawn — use Spawn for that.
    public NetworkObject SpawnPredicted(Entity entity, uint predictKey, Connection owner = default) {
        if (entity is null)
            throw new ArgumentNullException(nameof(entity));
        if (!IsServer) {
            Debugging.LogWarning("SpawnPredicted is server-only — the client predicts via Network.PredictSpawn.");
            return null;
        }
        NetworkObject netObj = entity.GetComponent<NetworkObject>() ?? entity.AddComponent<NetworkObject>();
        return SpawnObject(netObj, owner.IsValid ? owner : Connection.None, predictKey);
    }

    // Spawn an already-constructed NetworkObject (used by GameMode possession of a scene-placed pawn).
    internal NetworkObject SpawnObject(NetworkObject netObj, Connection owner, uint predictKey = 0) {
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
        // The Spawn carries a FULL snapshot + the echoed predictKey (P5f, 0 = normal); capture the baseline
        // right after so the next delta-snapshot diffs against it. A loopback host has no remote clients,
        // so the broadcast is a no-op there (SP path).
        BroadcastSpawn(netObj, predictKey);
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
        foreach (ClientReplState state in clientState.Values)
            state.Forget(netId);   // P6: drop the despawned object from every client's baseline/pending
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

    // ---- P7: ConnectionToken reconnect window (plan §8.5.5 / §9.8) --------------------------------
    // The disconnect/reconnect edge-case class (§9 item 8): identity = a PERSISTENT ConnectionToken, NOT
    // the transport id. On disconnect the player's pawn STAYS spawned (ownership -> server, OnOwnershipChanged
    // fires, OnSpawned subscriptions survive); a reconnect presenting the same token within the TTL reclaims
    // it (ownership -> the new connection). Auto-reclaim is the framework DEFAULT (user-approved). Expired
    // orphans are swept (despawned — the player truly left). Proven in %TEMP%\bal-reconnect-test (26/26).

    // SERVER: connection -> the ConnectionToken it presented at handshake (its persistent identity). Dropped
    // on disconnect (after recording the orphan). The source of truth for "which token owns this connection".
    readonly Dictionary<Connection, ConnectionToken> connectionTokens = new();

    // SERVER: token -> a reclaimable orphan (a spawned-but-server-owned pawn awaiting reclaim, + its TTL
    // deadline tick). A reconnect with the matching token transfers ownership back; the sweep despawns
    // expired ones. Keyed by token so a NEW transport id reclaims the SAME pawn (the §9.8 point).
    sealed class ReconnectOrphan { public List<NetworkObject> Pawns; public long Expiry; }
    readonly Dictionary<ConnectionToken, ReconnectOrphan> orphans = new();

    // CLIENT: the persistent token to present at the next connect (None = first join, the server mints one
    // we then persist from HandshakeOk). Set externally before a RECONNECT (Network.SetReconnectToken) so
    // the reconnecting client reclaims its pawn. A real client persists this to disk between sessions.
    public ConnectionToken PersistentToken { get; set; } = ConnectionToken.None;

    // The reconnect-window TTL in fixed ticks (~30s at 60Hz). A disconnected player has this long to come
    // back and reclaim its pawn before the orphan is swept. Tunable per game (a lobby may want longer).
    public long ReconnectTtlTicks { get; set; } = 60 * 30;

    // SERVER-side mint counter for first-join tokens (distinct per process via the seed). Monotonic.
    ulong mintCounter;
    ConnectionToken MintToken() => ConnectionToken.Mint(MintSeed, mintCounter++);
    // A per-process seed so two servers never mint colliding tokens. Derived from the topology + a fixed
    // salt (Date.Now is banned in harness contexts; this is deterministic-enough for collision avoidance —
    // tokens only need to be distinct WITHIN one server's lifetime, which the counter guarantees).
    ulong MintSeed => 0xC2B2AE3D27D4EB4FUL ^ (ulong)(LocalConnection.Id + 1);

    // Raised when a reconnect reclaimed an orphaned pawn (diagnostics / the §6 rejoin hook). prev arg = the
    // reclaiming connection. A GameMode override can re-bind HUD / announce the rejoin here.
    public Action<Connection> OnPlayerReconnected { get; set; }

    // SERVER: record the disconnecting connection's owned pawns as a reclaimable orphan. Ownership -> server
    // (NOT despawn) so OnOwnershipChanged fires and the §8.5 subscriptions survive. A connection with no
    // owned pawn records an empty orphan (still reclaimable so a rejoin is recognized; harmless).
    void RecordReconnectOrphan(Connection c, ConnectionToken token) {
        var pawns = new List<NetworkObject>();
        foreach (NetworkObject obj in objects.All())
            if (obj is { IsSpawned: true } && obj.Owner.IsValid && obj.Owner.Equals(c)) {
                TransferOwnership(obj, Connection.None);   // ownership -> server (OnOwnershipChanged fires)
                pawns.Add(obj);
            }
        orphans[token] = new ReconnectOrphan { Pawns = pawns, Expiry = SendClock.LocalTick + ReconnectTtlTicks };
    }

    // SERVER: a reconnect presented `token`. If a live orphan matches, transfer every orphaned pawn's
    // ownership BACK to the new connection (the SAME objects — no respawn) and clear the orphan. Returns
    // true on a reclaim. OnOwnershipChanged fires (server -> newConn) on each reclaimed pawn.
    bool TryReclaimOrphan(Connection newConn, ConnectionToken token) {
        if (!orphans.TryGetValue(token, out ReconnectOrphan o))
            return false;
        orphans.Remove(token);
        foreach (NetworkObject pawn in o.Pawns)
            if (pawn is { IsSpawned: true })
                TransferOwnership(pawn, newConn);          // ownership -> the reconnecting player (auto-reclaim)
        OnPlayerReconnected?.Invoke(newConn);
        return true;
    }

    // Sweep expired reconnect orphans (the player did NOT come back in time) — despawn each orphaned pawn
    // (graceful teardown, §8.5.3: OnDespawned fires). Called once per fixed tick from PredictTick. A
    // deadline compare per orphan (a handful at most) — no per-tick cost when none exist.
    void SweepReconnectOrphans() {
        if (orphans.Count == 0)
            return;
        List<ConnectionToken> expired = null;
        foreach (var kv in orphans)
            if (SendClock.LocalTick >= kv.Value.Expiry)
                (expired ??= new()).Add(kv.Key);
        if (expired is null)
            return;
        foreach (ConnectionToken t in expired) {
            foreach (NetworkObject pawn in orphans[t].Pawns)
                if (pawn is { IsSpawned: true })
                    Despawn(pawn);                          // the player truly left -> graceful teardown
            orphans.Remove(t);
        }
    }

    public int ReconnectOrphanCount => orphans.Count;
    public bool HasReconnectOrphan(ConnectionToken token) => orphans.ContainsKey(token);

    static void FireOwnershipChanged(NetworkObject netObj, Connection prev, Connection next) {
        foreach (Behaviour b in netObj.Entity.Behaviours.ToArray())
            if (b is NetworkBehaviour nb)
                nb.DriveOwnershipChanged(prev, next);
    }

    // ---- possession-replication (P6, plan §6/§4e) -------------------------------------------------
    // Called by PlayerController.Possess on the SERVER (both authoritative ends of the possession pair are
    // spawned NetworkObjects). The server already wired the link locally; this REPLICATES it so the owning
    // client AUTO-builds its controller's input pipeline (no hand-wiring — the P5b/c/d/f harness scope
    // boundary closed) and every observer links Pawn.Controller consistently. Idempotent / no-op off-server.
    internal void OnServerPossess(PlayerController controller, Pawn pawn) {
        if (!IsServer)
            return;
        NetworkObject cObj = controller?.NetworkObject;
        NetworkObject pObj = pawn?.NetworkObject;
        if (cObj is not { IsSpawned: true } || pObj is not { IsSpawned: true })
            return;   // both must be spawned to address them by netId — Phase-1/spawn order guarantees this
        BroadcastPossess(cObj.NetId, pObj.NetId);
    }

    internal void OnServerUnpossess(PlayerController controller) {
        if (!IsServer)
            return;
        NetworkObject cObj = controller?.NetworkObject;
        if (cObj is { IsSpawned: true })
            BroadcastPossess(cObj.NetId, 0);   // pawnNetId 0 = unpossess
    }

    void BroadcastPossess(int controllerNetId, int pawnNetId) {
        byte[] msg = NetworkWire.Possess(controllerNetId, pawnNetId);
        foreach (Connection c in clients)
            Transport.Send(c, msg, Channel.Reliable);
    }

    // Replay the CURRENT possession links to one joining client (late-join, plan §5/§6): walk spawned
    // PlayerControllers that possess a pawn and send each as a Possess message, so the joiner links them
    // (and, if it owns one, auto-builds input). Called from the join flow after spawns are sent.
    void SendPossessionsTo(Connection client) {
        foreach (NetworkObject obj in objects.All()) {
            if (obj?.Entity is null)
                continue;
            foreach (Behaviour b in obj.Entity.Behaviours.ToArray())
                if (b is PlayerController { Pawn: { } pawn } pc && pc.NetworkObject is { IsSpawned: true } cObj
                    && pawn.NetworkObject is { IsSpawned: true } pObj)
                    Transport.Send(client, NetworkWire.Possess(cObj.NetId, pObj.NetId), Channel.Reliable);
        }
    }

    // CLIENT: apply a replicated possession. Resolve the controller + pawn mirrors and link them locally via
    // the SAME PlayerController.Possess the server ran — so on the OWNING client Possess's owner-gated
    // TrySetupInput builds the InputComponent (via the controller's CreateInputComponent seam) AND the
    // prediction InputBuffer, with zero hand-wiring. pawnNetId 0 = unpossess. A controller/pawn whose spawn
    // hasn't arrived yet is skipped (Reliable spawns precede this Reliable message in practice; a benign
    // miss self-heals if the server re-sends on the next relevant event).
    void HandlePossess(ref BitReader r) {
        int controllerNetId = r.ReadInt();
        int pawnNetId = r.ReadInt();
        NetworkObject cObj = objects.Resolve(controllerNetId);
        PlayerController pc = FindPlayerController(cObj);
        if (pc is null) {
            Debugging.LogWarning($"Network: Possess for unknown controller netId {controllerNetId} — dropped.");
            return;
        }
        if (pawnNetId == 0) {
            pc.Unpossess();
            return;
        }
        NetworkObject pObj = objects.Resolve(pawnNetId);
        Pawn pawn = FindPawn(pObj);
        if (pawn is null) {
            Debugging.LogWarning($"Network: Possess for unknown pawn netId {pawnNetId} — dropped.");
            return;
        }
        pc.Possess(pawn);   // owner-gated TrySetupInput fires inside Possess on the input authority (§7)
    }

    static PlayerController FindPlayerController(NetworkObject obj) {
        if (obj?.Entity is null)
            return null;
        foreach (Behaviour b in obj.Entity.Behaviours.ToArray())
            if (b is PlayerController pc)
                return pc;
        return null;
    }

    static Pawn FindPawn(NetworkObject obj) {
        if (obj?.Entity is null)
            return null;
        foreach (Behaviour b in obj.Entity.Behaviours.ToArray())
            if (b is Pawn p)
                return p;
        return null;
    }

    internal NetworkObject Resolve(int netId) => objects.Resolve(netId);
    public int SpawnedCount => objects.Count;

    // ---- P7: local-player resolution (the HUD binding seam, plan §2/§5 Phase 2) -------------------
    // The PlayerController this machine OWNS (input authority) — the "local player". On a host its own
    // pawn's controller; on a client the controller it was possessed into. Null on a dedicated server (no
    // local player) or before possession. A reflection-free walk over spawned objects (a handful of
    // controllers; runs at HUD.Init / on demand, never per-frame). HUD.Init binds to this.
    public PlayerController LocalPlayerController() {
        foreach (NetworkObject obj in objects.All()) {
            if (obj?.Entity is null || !obj.IsOwner)
                continue;
            foreach (Behaviour b in obj.Entity.Behaviours.ToArray())
                if (b is PlayerController pc)
                    return pc;
        }
        return null;
    }

    // The PlayerState for the local player — the one on the local controller's entity, or (if the player
    // info lives on a dedicated entity) the PlayerState owned by the local connection. Null when none.
    public PlayerState LocalPlayerState() {
        // Prefer the PlayerState sibling of the local controller (the common layout).
        PlayerController pc = LocalPlayerController();
        if (pc?.Entity is not null && pc.Entity.GetComponent<PlayerState>() is { } sibling)
            return sibling;
        // Else any PlayerState owned by the local connection (a dedicated player-info entity).
        foreach (NetworkObject obj in objects.All()) {
            if (obj?.Entity is null || !obj.IsOwner)
                continue;
            foreach (Behaviour b in obj.Entity.Behaviours.ToArray())
                if (b is PlayerState ps)
                    return ps;
        }
        return null;
    }
}
