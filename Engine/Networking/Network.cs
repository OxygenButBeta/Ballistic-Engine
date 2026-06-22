using BallisticEngine.Networking;
using BallisticEngine.Loopback;

namespace BallisticEngine;

public static class Network {
    public static NetworkManager Manager { get; set; }

    public static bool IsServer  => Manager?.IsServer ?? false;
    public static bool IsClient  => Manager?.IsClient ?? false;
    public static bool IsHost    => Manager?.IsHost ?? false;
    public static bool IsOffline => Manager?.IsOffline ?? true;

    public static Connection LocalConnection => Manager?.LocalConnection ?? Connection.None;

    public static PlayerController LocalPlayerController => Manager?.LocalPlayerController();
    public static PlayerState LocalPlayerState => Manager?.LocalPlayerState();

    public static ConnectionToken ReconnectToken {
        get => Manager?.PersistentToken ?? ConnectionToken.None;
        set { if (Manager is not null) Manager.PersistentToken = value; }
    }

    public static long ReconnectTtlTicks {
        get => Manager?.ReconnectTtlTicks ?? 0;
        set { if (Manager is not null) Manager.ReconnectTtlTicks = value; }
    }

    public static Action<Connection> OnPlayerReconnected {
        get => Manager?.OnPlayerReconnected;
        set { if (Manager is not null) Manager.OnPlayerReconnected = value; }
    }

    public static void StartHost(ITransport transport = null) =>
        Require().StartHost(transport ?? new LoopbackTransport());

    public static void StartServer(ITransport transport) => Require().StartServer(transport);
    public static void StartClient(ITransport transport) => Require().StartClient(transport);
    public static void Stop() => Manager?.Stop();

    public static NetworkObject Spawn(Entity entity, Connection owner = default) =>
        Require().Spawn(entity, owner);

    public static void Despawn(NetworkObject netObj) => Manager?.Despawn(netObj);

    public static (NetworkObject obj, uint key) PredictSpawn(Entity entity, Connection owner = default) =>
        Require().PredictSpawn(entity, owner);

    public static NetworkObject SpawnPredicted(Entity entity, uint predictKey, Connection owner = default) =>
        Require().SpawnPredicted(entity, predictKey, owner);

    public static void TransferOwnership(NetworkObject netObj, Connection newOwner) =>
        Manager?.TransferOwnership(netObj, newOwner);

    public static void RemoveOwnership(NetworkObject netObj) => Manager?.RemoveOwnership(netObj);

    public static double RenderTick => Manager?.RenderTick ?? 0;

    public static uint ServerTick => Manager?.ServerTick ?? 0;

    public static bool LagCompensatedRaycast(Vector3 origin, Vector3 direction, double renderTick,
        NetworkObject shooter, out LagRaycastHit hit) {
        if (Manager is null) { hit = default; return false; }
        return Manager.LagCompensatedRaycast(origin, direction, renderTick, shooter, out hit);
    }

    public static double InterpDelayTicks {
        get => Manager?.InterpDelayTicks ?? 0;
        set { if (Manager is not null) Manager.InterpDelayTicks = value; }
    }
    public static int MaxRewindTicks {
        get => Manager?.MaxRewindTicks ?? 0;
        set { if (Manager is not null) Manager.MaxRewindTicks = value; }
    }

    public static bool InterestManagement {
        get => Manager?.InterestManagement ?? false;
        set { if (Manager is not null) Manager.InterestManagement = value; }
    }

    public static float DefaultRelevancyRadius {
        get => Manager?.DefaultRelevancyRadius ?? 0;
        set { if (Manager is not null) Manager.DefaultRelevancyRadius = value; }
    }

    public static bool IsInInterest(Connection c, NetworkObject obj) =>
        Manager?.IsInInterest(c, obj) ?? false;

    public static bool IsRelevant(bool alwaysRelevant, bool ownedByViewer, bool hasView,
        Vector3 view, Vector3 objectPos, float radius) =>
        NetworkManager.IsRelevantPure(alwaysRelevant, ownedByViewer, hasView, view, objectPos, radius);

    public static void SendRpc(NetworkBehaviour self, int methodId, RpcTarget target, bool reliable,
        ReadOnlySpan<byte> args) =>
        Manager?.SendRpc(self, methodId, target, reliable, args);

    public static NetworkAuthority ResolveRole(
        NetworkTopology topology, Connection localConnection, Connection owner) =>
        NetworkManager.ResolveAuthority(topology, localConnection, owner);

    internal static NetworkObject Resolve(int netId) => Manager?.Resolve(netId);

    static NetworkManager Require() =>
        Manager ?? throw new InvalidOperationException(
            "Network has no NetworkManager. EngineBootstrap injects one; call this only after bootstrap.");
}
