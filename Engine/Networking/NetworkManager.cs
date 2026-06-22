using BallisticEngine.Networking;

namespace BallisticEngine;

public sealed class NetworkManager {
    public ITransport Transport { get; private set; }
    public NetworkTopology Topology { get; private set; } = NetworkTopology.Offline;

    readonly NetworkObjectRegistry objects = new();

    public Connection LocalConnection { get; private set; } = Connection.None;

    public Connection ServerConnection { get; private set; } = Connection.None;

    public bool IsServer => Topology is NetworkTopology.Server or NetworkTopology.Host;
    public bool IsClient => Topology is NetworkTopology.Client or NetworkTopology.Host;
    public bool IsHost   => Topology is NetworkTopology.Host;
    public bool IsOffline => Topology is NetworkTopology.Offline;

    public void StartHost(ITransport transport) {
        Stop();
        Transport = transport ?? throw new ArgumentNullException(nameof(transport));
        WireTransport();
        Topology = NetworkTopology.Host;
        LocalConnection = Connection.Local;
        Transport.StartServer();
        Transport.Connect();
    }

    public void StartServer(ITransport transport) {
        Stop();
        Transport = transport ?? throw new ArgumentNullException(nameof(transport));
        WireTransport();
        Topology = NetworkTopology.Server;
        LocalConnection = Connection.None;
        Transport.StartServer();
    }

    public void StartClient(ITransport transport) {
        Stop();
        Transport = transport ?? throw new ArgumentNullException(nameof(transport));
        WireTransport();
        Topology = NetworkTopology.Client;
        Transport.Connect();
    }

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
        LastServerTick = 0;
        snapshotWriter.Reset();
        sceneStateWriter.Reset();
        connectionTokens.Clear();
        orphans.Clear();
    }

    void WireTransport() {
        Transport.OnConnected = OnPeerConnected;
        Transport.OnDisconnected = OnPeerDisconnected;
        Transport.OnReceived = OnPayload;
    }

    readonly List<Connection> clients = new();
    public IReadOnlyList<Connection> Clients => clients;

    readonly HashSet<string> warnedMultiNet = new();

    readonly Dictionary<Connection, ClientReplState> clientState = new();

    public Action<Connection> OnPlayerJoined { get; set; }

    public Action<Connection> OnLayoutMismatch { get; set; }

    void OnPeerConnected(Connection c) {
        if (IsServer) {
            if (!clients.Contains(c))
                clients.Add(c);
        }
        else {
            ServerConnection = c;
            Transport.Send(c, NetworkWire.Handshake(NetworkWire.LayoutDigest(), PersistentToken), Channel.Reliable);
        }
    }

    void OnPeerDisconnected(Connection c) {
        clients.Remove(c);
        clientState.Remove(c);

        if (IsServer && connectionTokens.TryGetValue(c, out ConnectionToken token) && token.IsValid)
            RecordReconnectOrphan(c, token);
        connectionTokens.Remove(c);
    }

    void OnPayload(Connection source, ReadOnlySpan<byte> payload, Channel channel) {
        byte tag = NetworkWire.ReadTag(payload);
        var r = new BitReader(payload);
        r.ReadByte();
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

    void HandleInput(Connection source, ref BitReader r) {
        int netId = r.ReadInt();
        int count = r.ReadByte();
        NetworkObject obj = objects.Resolve(netId);
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

    void HandleHandshake(Connection source, ref BitReader r) {
        int clientDigest = r.ReadInt();
        ConnectionToken presented = NetworkWire.ReadToken(ref r);
        if (clientDigest != NetworkWire.LayoutDigest()) {
            Debugging.LogError(
                $"Network: {source} rejected — [Networked] layout digest mismatch (drifted build). " +
                "A coordinated reload (all peers on the same build) is required (plan §8.6.1).");
            OnLayoutMismatch?.Invoke(source);
            return;
        }

        ConnectionToken token = presented;
        bool reclaimed = false;
        if (presented.IsValid && TryReclaimOrphan(source, presented))
            reclaimed = true;
        else if (!presented.IsValid)
            token = MintToken();
        connectionTokens[source] = token;

        Transport.Send(source, NetworkWire.HandshakeOk(source.Id, token), Channel.Reliable);

        var state = new ClientReplState();
        clientState[source] = state;
        foreach (NetworkObject obj in objects.All()) {
            SendSpawnTo(source, obj);
            SeedClientBaseline(state, obj);
        }

        SendPossessionsTo(source);

        SeedAndSendSceneStateTo(source, state);

        OnPlayerJoined?.Invoke(source);
    }

    static void SeedClientBaseline(ClientReplState state, NetworkObject obj) {
        if (obj?.Entity is null)
            return;
        foreach (Behaviour b in obj.Entity.Behaviours.ToArray())
            if (b is NetworkBehaviour { HasNetworkedState: true } nb) {
                nb.CaptureNetworkBaseline();
                state.SeedBaseline(obj.NetId, nb.__GetNetBaseline());
            }
    }

    void HandleAck(Connection source, ref BitReader r) {
        uint ackedSeq = r.ReadUInt();
        if (clientState.TryGetValue(source, out ClientReplState state))
            state.Ack(ackedSeq);
    }

    void HandleHandshakeOk(Connection source, ref BitReader r) {
        int assignedId = r.ReadInt();
        LocalConnection = new Connection(assignedId);
        PersistentToken = NetworkWire.ReadToken(ref r);
    }

    void HandleSpawn(ref BitReader r) {
        int netId = r.ReadInt();
        int typeId = r.ReadInt();
        int ownerId = r.ReadInt();
        uint predictKey = r.ReadUInt();
        if (!NetworkReplicationRegistry.TryGet(typeId, out NetworkTypeDescriptor desc) || desc.ComponentType is null) {
            Debugging.LogError($"Network: spawn for unknown typeId {typeId} — no client type registered.");
            return;
        }

        if (predictKey != 0 && predicted.TryGetValue(predictKey, out NetworkObject pred) && pred.Entity is not null) {
            predicted.Remove(predictKey);
            pred.PredictKey = 0;
            pred.PredictConfirmDeadline = 0;
            pred.NetId = netId;
            pred.Owner = new Connection(ownerId);
            pred.Authority = ResolveAuthority(Topology, LocalConnection, pred.Owner);
            objects.AddWithId(netId, pred);
            foreach (Behaviour b in pred.Entity.Behaviours.ToArray())
                if (b is NetworkBehaviour { HasNetworkedState: true } nb && nb.NetworkTypeId == typeId) {
                    nb.DeserializeState(ref r);
                    try { nb.OnStateApplied(); } catch (Exception e) { ScriptGuard.Report(nb, "OnStateApplied", e); }
                    nb.CaptureNetworkBaseline();
                }
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
        objects.AddWithId(netId, netObj);

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

    void HandleSnapshot(ref BitReader r) {
        uint snapshotSeq = r.ReadUInt();
        LastServerTick = r.ReadUInt();
        while (!r.AtEnd && r.BitLength - r.BitPos >= 96) {
            int netId = r.ReadInt();
            uint lastProcessedSeq = r.ReadUInt();
            int typeId = r.ReadInt();
            NetworkObject obj = objects.Resolve(netId);
            NetworkBehaviour target = FindNetworkBehaviour(obj, typeId);
            if (target is null) {
                break;
            }
            target.DeserializeState(ref r);
            try { target.OnStateApplied(); }
            catch (Exception e) { ScriptGuard.Report(target, "OnStateApplied", e); }

            if (obj.HasInputAuthority && !obj.HasStateAuthority) {
                obj.LastProcessedSeq = lastProcessedSeq;
                PlayerController pc = FindController(obj);
                if (pc is not null) {
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
            else if (obj.IsSimulatedProxy && obj.Entity is not null) {
                obj.Interpolator ??= new SnapshotInterpolator();
                Transform tr = obj.Entity.transform;
                obj.Interpolator.Receive(obj.InterpClock, tr.Position, tr.Rotation);
            }
        }

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
                if (IsServer)
                    InvokeRpcLocally(self, methodId, target, args, caller: LocalConnection);
                else
                    Transport.Send(ServerConnection, NetworkWire.Rpc(obj.NetId, self.NetworkTypeId, methodId, args), channel);
                break;

            case RpcTarget.Owner:
                if (!IsServer) { WarnClientCalledServerRpc(target); return; }
                if (obj.Owner.IsValid && obj.Owner.Equals(LocalConnection))
                    InvokeRpcLocally(self, methodId, target, args, caller: LocalConnection);
                else if (obj.Owner.IsValid)
                    Transport.Send(obj.Owner, NetworkWire.Rpc(obj.NetId, self.NetworkTypeId, methodId, args), channel);
                break;

            case RpcTarget.All:
                if (!IsServer) { WarnClientCalledServerRpc(target); return; }
                InvokeRpcLocally(self, methodId, target, args, caller: LocalConnection);
                byte[] frame = NetworkWire.Rpc(obj.NetId, self.NetworkTypeId, methodId, args);
                foreach (Connection c in clients)
                    Transport.Send(c, frame, channel);
                break;
        }
    }

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

    void HandleRpc(Connection source, ref BitReader r) {
        int netId = r.ReadInt();
        int typeId = r.ReadInt();
        int methodId = r.ReadInt();
        NetworkObject obj = objects.Resolve(netId);
        NetworkBehaviour target = FindNetworkBehaviourForRpc(obj, typeId);
        if (target is null) {
            Debugging.LogWarning($"Network: RPC for unknown object netId {netId} typeId {typeId} — dropped.");
            return;
        }
        if (!NetworkReplicationRegistry.TryGetRpc(typeId, methodId, out NetworkRpcEntry entry)) {
            Debugging.LogWarning($"Network: RPC for unknown methodId {methodId} on typeId {typeId} — dropped.");
            return;
        }

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

    public SendRateClock SendClock { get; private set; } = new();

    public int StateSnapshotsSent { get; private set; }

    public InputUpStream InputStream { get; private set; } = new();

    public uint LocalTick => (uint)SendClock.LocalTick;

    public int PredictionTicks { get; private set; }

    public bool IsReplaying { get; private set; }

    public uint LastServerTick { get; private set; }

    public double InterpDelayTicks { get; set; } = SnapshotInterpolator.DefaultInterpDelayTicks;

    public int MaxRewindTicks { get; set; } = 60;

    public uint ServerTick => (uint)SendClock.LocalTick;

    public bool InterestManagement { get; set; }

    public float DefaultRelevancyRadius { get; set; } = 50f;

    bool TryConnectionView(Connection c, out Vector3 view) {
        foreach (NetworkObject obj in objects.All())
            if (obj is { IsSpawned: true } && obj.Entity is not null && obj.Owner.IsValid && obj.Owner.Equals(c)) {
                view = obj.Entity.transform.Position;
                return true;
            }
        view = default;
        return false;
    }

    bool RelevantTo(Connection c, NetworkObject obj) {
        bool owned = obj.Owner.IsValid && obj.Owner.Equals(c);
        bool hasView = TryConnectionView(c, out Vector3 view);
        float radius = obj.RelevancyRadius > 0f ? obj.RelevancyRadius : DefaultRelevancyRadius;
        return IsRelevantPure(obj.AlwaysRelevant, owned, hasView, view,
            obj.Entity?.transform.Position ?? default, radius);
    }

    internal static bool IsRelevantPure(bool alwaysRelevant, bool ownedByViewer, bool hasView,
        Vector3 view, Vector3 objectPos, float radius) {
        if (alwaysRelevant || ownedByViewer)
            return true;
        if (!hasView)
            return false;
        return (objectPos - view).LengthSquared() <= radius * radius;
    }

    void EvaluateInterest() {
        foreach (Connection c in clients) {
            if (!clientState.TryGetValue(c, out ClientReplState state))
                continue;
            foreach (NetworkObject obj in objects.All()) {
                if (obj is not { IsSpawned: true } || obj.Entity is null)
                    continue;
                bool now = RelevantTo(c, obj);
                bool was = state.Relevant.Contains(obj.NetId);
                if (now && !was) {
                    state.Relevant.Add(obj.NetId);
                    state.ReseedOnRegain.Add(obj.NetId);
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

    public bool IsInInterest(Connection c, NetworkObject obj) =>
        obj is not null && clientState.TryGetValue(c, out ClientReplState s) && s.Relevant.Contains(obj.NetId);

    public double RenderTick => IsServer ? ServerTick : Math.Max(0, (double)LastServerTick - InterpDelayTicks);

    void RecordLagCompHistory() {
        uint tick = (uint)SendClock.LocalTick;
        foreach (NetworkObject obj in objects.All()) {
            if (obj is not { IsSpawned: true, IsLagCompensated: true } || obj.Entity is null)
                continue;
            obj.LagHistory ??= new PoseHistory(MaxRewindTicks + 4);
            obj.LagHistory.Record(tick, obj.Entity.transform.Position);
        }
    }

    public double ClampRenderTick(double renderTick) {
        double now = SendClock.LocalTick;
        double oldest = now - MaxRewindTicks;
        if (renderTick < oldest) return oldest;
        if (renderTick > now) return now;
        return renderTick;
    }

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
                continue;

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

    public void PollTransport() => Transport?.Poll();

    public void PredictTick(float step) {
        if (IsOffline)
            return;

        uint seq = (uint)SendClock.LocalTick;
        PredictionTicks++;

        if (IsServer)
            RecordLagCompHistory();

        foreach (NetworkObject obj in objects.All()) {
            if (obj?.Entity is null)
                continue;

            if (obj.HasInputAuthority) {
                CapturePredictionInputFor(obj, seq);
                DriveNetworkTick(obj);
                if (obj.Smoother is { IsActive: true } && !obj.HasStateAuthority && obj.Entity is not null) {
                    Vector3 off = obj.Smoother.Decay();
                    obj.Entity.transform.Position += off;
                }

                if (!IsServer)
                    SendInputUp(obj);
            }
            else if (obj.HasStateAuthority) {
                ApplyServerInput(obj);
                DriveNetworkTick(obj);
            }
            else {
                InterpolateProxy(obj);
            }
        }

        SweepPredictedRollbacks();

        if (IsServer)
            SweepReconnectOrphans();

        InputStream.RecordInput(seq);

        bool sendBoundary = SendClock.Advance();
        if (sendBoundary) {
            if (IsServer) {
                if (InterestManagement)
                    EvaluateInterest();
                FlushStateDown();
            }
            InputStream.FlushBatch();
        }
    }

    void CapturePredictionInputFor(NetworkObject obj, uint seq) {
        foreach (Behaviour b in obj.Entity.Behaviours.ToArray()) {
            if (b is PlayerController pc) {
                pc.CapturePredictionInput(seq);
                return;
            }

            if (b is Pawn { Controller: { } controller })
                controller.CapturePredictionInput(seq);
        }
    }

    void ApplyServerInput(NetworkObject obj) {
        PlayerController pc = FindController(obj);
        if (pc is null)
            return;
        Queue<NetworkInput> inbox = obj.ServerInputInbox;
        if (inbox is not null) {
            while (inbox.Count > 0 && inbox.Peek().Seq <= obj.LastProcessedSeq)
                inbox.Dequeue();
            if (inbox.Count > 0) {
                NetworkInput input = inbox.Dequeue();
                obj.LastServerInput = input;
                obj.HaveLastServerInput = true;
                obj.LastProcessedSeq = input.Seq;
                pc.SetServerInput(input);
                return;
            }
        }

        if (obj.HaveLastServerInput)
            pc.SetServerInput(obj.LastServerInput);
    }

    void SendInputUp(NetworkObject obj) {
        if (!SendClock.IsBoundary || !ServerConnection.IsValid)
            return;
        PlayerController pc = FindController(obj);
        if (pc?.InputBuffer is null)
            return;
        NetworkInput[] batch = pc.InputBuffer.InOrder().ToArray();
        if (batch.Length == 0)
            return;
        Transport.Send(ServerConnection, NetworkWire.Input(obj.NetId, batch), Channel.Reliable);
    }

    static void InterpolateProxy(NetworkObject obj) {
        if (obj.Entity is null || obj.Interpolator is null)
            return;
        obj.InterpClock += 1.0;
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

    BitWriter snapshotWriter = new();

    public int LastSnapshotObjectCount { get; private set; }

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
        snapshotWriter.WriteUInt(sendSeq);
        snapshotWriter.WriteUInt((uint)SendClock.LocalTick);
        int written = 0;
        Dictionary<int, object> sentThisSeq = null;
        foreach (NetworkObject obj in objects.All()) {
            if (obj?.Entity is null)
                continue;
            if (InterestManagement && !state.Relevant.Contains(obj.NetId))
                continue;
            bool reseed = InterestManagement && state.ReseedOnRegain.Remove(obj.NetId);
            foreach (Behaviour b in obj.Entity.Behaviours.ToArray()) {
                if (b is not NetworkBehaviour { HasNetworkedState: true } nb)
                    continue;

                if (reseed) {
                    snapshotWriter.WriteInt(obj.NetId);
                    snapshotWriter.WriteUInt(obj.LastProcessedSeq);
                    snapshotWriter.WriteInt(nb.NetworkTypeId);
                    nb.SerializeFullState(snapshotWriter);
                    nb.CaptureNetworkBaseline();
                    (sentThisSeq ??= new())[obj.NetId] = nb.__GetNetBaseline();
                    written++;
                    continue;
                }

                object clientBaseline = state.Baseline.TryGetValue(obj.NetId, out object t) ? t : null;
                if (clientBaseline is not null)
                    nb.__SetNetBaseline(clientBaseline);

                if (clientBaseline is not null && nb.__NetStateEquals(clientBaseline))
                    continue;

                snapshotWriter.WriteInt(obj.NetId);
                snapshotWriter.WriteUInt(obj.LastProcessedSeq);
                snapshotWriter.WriteInt(nb.NetworkTypeId);
                nb.SerializeState(snapshotWriter);

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
            return;

        foreach (Connection c in clients) {
            if (!clientState.TryGetValue(c, out ClientReplState state))
                continue;
            uint sendSeq = state.SendSeq + 1;
            ReadOnlySpan<byte> batch = SerializeStateSnapshotFor(state, sendSeq);
            if (LastSnapshotObjectCount == 0)
                continue;
            state.SendSeq = sendSeq;
            byte[] framed = new byte[batch.Length + 1];
            framed[0] = (byte)NetMessage.Snapshot;
            batch.CopyTo(framed.AsSpan(1));
            Transport.Send(c, framed, Channel.Unreliable);
        }

        FlushSceneStateDown();
    }

    static readonly List<IReplicated> replScratch = new();
    static List<IReplicated> CollectSceneReplicated() {
        replScratch.Clear();
        Scene scene = SceneManager.GetCurrentScene();
        if (scene is null)
            return replScratch;
        foreach (SceneBehaviour sb in scene.SceneBehaviours)
            if (sb is IReplicated { HasReplicatedState: true } r)
                replScratch.Add(r);
        replScratch.Sort((a, b) => {
            int c = a.ReplicationTypeId.CompareTo(b.ReplicationTypeId);
            return c != 0 ? c : string.CompareOrdinal(a.GetType().FullName, b.GetType().FullName);
        });
        return replScratch;
    }

    static void AssignReplicationIds(List<IReplicated> list) {
        for (int i = 0; i < list.Count; i++)
            if (list[i] is GameState gs)
                gs.ReplicationId = i;
            else
                SetReplicationId(list[i], i);
    }

    static void SetReplicationId(IReplicated r, int id) {
    }

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
                continue;

            sceneBlocks.WriteInt(r.ReplicationId);
            r.Serialize(sceneBlocks);
            r.CaptureReplBaseline();
            (sentThisSeq ??= new())[r.ReplicationId] = r.__GetReplBaseline();
            written++;
        }
        if (sentThisSeq is not null)
            state.ScenePending[sceneSeq] = sentThisSeq;

        sceneStateWriter.Reset();
        sceneStateWriter.WriteUInt(sceneSeq);
        sceneStateWriter.WriteInt(written);
        ReadOnlySpan<byte> blocks = sceneBlocks.AsSpan();
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
                continue;
            state.SceneSendSeq = sceneSeq;
            ReadOnlySpan<byte> batch = sceneStateWriter.AsSpan();
            byte[] framed = new byte[batch.Length + 1];
            framed[0] = (byte)NetMessage.SceneState;
            batch.CopyTo(framed.AsSpan(1));
            Transport.Send(c, framed, Channel.Unreliable);
        }
    }

    void SeedAndSendSceneStateTo(Connection client, ClientReplState state) {
        List<IReplicated> list = CollectSceneReplicated();
        if (list.Count == 0)
            return;
        AssignReplicationIds(list);
        var w = new BitWriter();
        w.WriteByte((byte)NetMessage.SceneState);
        w.WriteUInt(0);
        w.WriteInt(list.Count);
        foreach (IReplicated r in list) {
            w.WriteInt(r.ReplicationId);
            r.SerializeFull(w);
            r.CaptureReplBaseline();
            state.SeedScene(r.ReplicationId, r.__GetReplBaseline());
        }
        Transport.Send(client, w.AsSpan().ToArray(), Channel.Reliable);
    }

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
                break;
            target.Deserialize(ref r);
        }
        if (ServerConnection.IsValid)
            Transport.Send(ServerConnection, NetworkWire.SceneAck(sceneSeq), Channel.Reliable);
    }

    void HandleSceneAck(Connection source, ref BitReader r) {
        uint ackedSeq = r.ReadUInt();
        if (clientState.TryGetValue(source, out ClientReplState state))
            state.AckScene(ackedSeq);
    }

    void SendSpawnTo(Connection client, NetworkObject obj, uint predictKey = 0) {
        if (obj?.Entity is null)
            return;
        uint keyForClient = predictKey != 0 && obj.Owner.IsValid && obj.Owner.Equals(client) ? predictKey : 0u;
        foreach (Behaviour b in obj.Entity.Behaviours.ToArray())
            if (b is NetworkBehaviour nb && nb.NetworkTypeId != 0)
                Transport.Send(client, NetworkWire.Spawn(obj.NetId, nb.NetworkTypeId, obj.Owner.Id, nb, keyForClient),
                    Channel.Reliable);
    }

    void BroadcastSpawn(NetworkObject obj, uint predictKey = 0) {
        foreach (Connection c in clients) {
            SendSpawnTo(c, obj, predictKey);
            if (clientState.TryGetValue(c, out ClientReplState state))
                SeedClientBaseline(state, obj);
        }
    }

    void BroadcastDespawn(int netId) {
        if (clients.Count == 0)
            return;
        byte[] msg = NetworkWire.Despawn(netId);
        foreach (Connection c in clients)
            Transport.Send(c, msg, Channel.Reliable);
    }

    public NetworkObject Spawn(Entity entity, Connection owner = default) {
        if (entity is null)
            throw new ArgumentNullException(nameof(entity));
        if (!IsServer)
            Debugging.LogWarning("Network.Spawn called without server authority — ignored on a client (server spawns).");

        NetworkObject netObj = entity.GetComponent<NetworkObject>() ?? entity.AddComponent<NetworkObject>();
        return SpawnObject(netObj, owner.IsValid ? owner : Connection.None);
    }

    readonly Dictionary<uint, NetworkObject> predicted = new();
    uint nextPredictKey = 1;
    const int PredictRollbackWindowTicks = 30;

    public (NetworkObject obj, uint key) PredictSpawn(Entity entity, Connection owner = default) {
        if (entity is null)
            throw new ArgumentNullException(nameof(entity));
        if (IsServer) {
            return (Spawn(entity, owner), 0u);
        }
        NetworkObject netObj = entity.GetComponent<NetworkObject>() ?? entity.AddComponent<NetworkObject>();
        uint key = nextPredictKey++;
        netObj.PredictKey = key;
        netObj.PredictConfirmDeadline = SendClock.LocalTick + PredictRollbackWindowTicks;
        netObj.Owner = LocalConnection;
        netObj.Authority = ResolveAuthority(Topology, LocalConnection, LocalConnection);
        netObj.IsSpawned = true;
        predicted[key] = netObj;
        DriveNetSpawnStrand(netObj.Entity);
        return (netObj, key);
    }

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
                    nb.DriveNetDespawn();
            obj.IsSpawned = false;
            obj.PredictKey = 0;
            SceneManager.GetCurrentScene()?.DestroyEntity(obj.Entity);
        }
    }

    public int PendingPredictedSpawns => predicted.Count;

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

    internal NetworkObject SpawnObject(NetworkObject netObj, Connection owner, uint predictKey = 0) {
        if (netObj.IsSpawned)
            return netObj;

        netObj.Owner = owner;
        netObj.Authority = ResolveAuthority(Topology, LocalConnection, owner);
        netObj.IsSpawned = true;
        netObj.NetId = objects.Add(netObj);

        DriveNetSpawnStrand(netObj.Entity);

        WarnIfMultipleNetworkedBehaviours(netObj.Entity);

        BroadcastSpawn(netObj, predictKey);
        foreach (Behaviour b in netObj.Entity.Behaviours.ToArray())
            if (b is NetworkBehaviour { HasNetworkedState: true } nb)
                nb.CaptureNetworkBaseline();
        return netObj;
    }

    void WarnIfMultipleNetworkedBehaviours(Entity entity) {
        if (entity is null)
            return;
        int registered = 0;
        foreach (Behaviour b in entity.Behaviours)
            if (b is NetworkBehaviour { NetworkTypeId: not 0 })
                registered++;
        if (registered > 1 && warnedMultiNet.Add(entity.Name))
            Debugging.LogWarning(
                $"Network: entity '{entity.Name}' has {registered} registered NetworkBehaviours. The framework " +
                "supports ONE per NetworkObject (put pawn/controller/player-state on SEPARATE entities). " +
                "Multiple will mirror as colliding entities + a starved per-client baseline.");
    }

    internal static NetworkAuthority ResolveAuthority(
        NetworkTopology topology, Connection localConnection, Connection owner) {
        NetworkAuthority a = NetworkAuthority.None;

        if (topology is NetworkTopology.Server or NetworkTopology.Host)
            a |= NetworkAuthority.State;

        if (owner.IsValid && owner.Equals(localConnection))
            a |= NetworkAuthority.Input;

        return a;
    }

    static void DriveNetSpawnStrand(Entity entity) {
        foreach (Behaviour b in entity.Behaviours.ToArray())
            if (b is NetworkBehaviour nb)
                nb.DriveNetSpawn();
    }

    public void Despawn(NetworkObject netObj) {
        if (netObj is null || !netObj.IsSpawned)
            return;
        int netId = netObj.NetId;
        BroadcastDespawn(netId);
        foreach (Behaviour b in netObj.Entity.Behaviours.ToArray())
            if (b is NetworkBehaviour nb)
                nb.DriveNetDespawn();
        foreach (ClientReplState state in clientState.Values)
            state.Forget(netId);
        objects.Remove(netId);
        netObj.IsSpawned = false;
        netObj.NetId = 0;
        netObj.Authority = NetworkAuthority.None;
        netObj.Owner = Connection.None;
    }

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

    public void RemoveOwnership(NetworkObject netObj) => TransferOwnership(netObj, Connection.None);

    readonly Dictionary<Connection, ConnectionToken> connectionTokens = new();

    sealed class ReconnectOrphan { public List<NetworkObject> Pawns; public long Expiry; }
    readonly Dictionary<ConnectionToken, ReconnectOrphan> orphans = new();

    public ConnectionToken PersistentToken { get; set; } = ConnectionToken.None;

    public long ReconnectTtlTicks { get; set; } = 60 * 30;

    ulong mintCounter;
    ConnectionToken MintToken() => ConnectionToken.Mint(MintSeed, mintCounter++);

    ulong MintSeed => 0xC2B2AE3D27D4EB4FUL ^ (ulong)(LocalConnection.Id + 1);

    public Action<Connection> OnPlayerReconnected { get; set; }

    void RecordReconnectOrphan(Connection c, ConnectionToken token) {
        var pawns = new List<NetworkObject>();
        foreach (NetworkObject obj in objects.All())
            if (obj is { IsSpawned: true } && obj.Owner.IsValid && obj.Owner.Equals(c)) {
                TransferOwnership(obj, Connection.None);
                pawns.Add(obj);
            }
        orphans[token] = new ReconnectOrphan { Pawns = pawns, Expiry = SendClock.LocalTick + ReconnectTtlTicks };
    }

    bool TryReclaimOrphan(Connection newConn, ConnectionToken token) {
        if (!orphans.TryGetValue(token, out ReconnectOrphan o))
            return false;
        orphans.Remove(token);
        foreach (NetworkObject pawn in o.Pawns)
            if (pawn is { IsSpawned: true })
                TransferOwnership(pawn, newConn);
        OnPlayerReconnected?.Invoke(newConn);
        return true;
    }

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
                    Despawn(pawn);
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

    internal void OnServerPossess(PlayerController controller, Pawn pawn) {
        if (!IsServer)
            return;
        NetworkObject cObj = controller?.NetworkObject;
        NetworkObject pObj = pawn?.NetworkObject;
        if (cObj is not { IsSpawned: true } || pObj is not { IsSpawned: true })
            return;
        BroadcastPossess(cObj.NetId, pObj.NetId);
    }

    internal void OnServerUnpossess(PlayerController controller) {
        if (!IsServer)
            return;
        NetworkObject cObj = controller?.NetworkObject;
        if (cObj is { IsSpawned: true })
            BroadcastPossess(cObj.NetId, 0);
    }

    void BroadcastPossess(int controllerNetId, int pawnNetId) {
        byte[] msg = NetworkWire.Possess(controllerNetId, pawnNetId);
        foreach (Connection c in clients)
            Transport.Send(c, msg, Channel.Reliable);
    }

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
        pc.Possess(pawn);
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

    public PlayerState LocalPlayerState() {
        PlayerController pc = LocalPlayerController();
        if (pc?.Entity is not null && pc.Entity.GetComponent<PlayerState>() is { } sibling)
            return sibling;
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
