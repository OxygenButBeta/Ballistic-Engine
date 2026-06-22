using BallisticEngine.Networking;

namespace BallisticEngine;

public abstract class NetworkBehaviour : Behaviour {
    NetworkObject netObject;

    public NetworkObject NetworkObject =>
        netObject ??= Entity?.GetComponent<NetworkObject>();

    internal bool NetBegun;

    public bool IsSpawned        => NetworkObject?.IsSpawned ?? false;
    public bool IsOwner          => NetworkObject?.IsOwner ?? false;
    public bool HasStateAuthority => NetworkObject?.HasStateAuthority ?? false;
    public bool HasInputAuthority => NetworkObject?.HasInputAuthority ?? false;
    public bool IsProxy          => NetworkObject is null || NetworkObject.IsProxy;
    public bool IsAutonomousProxy => NetworkObject?.IsAutonomousProxy ?? false;
    public bool IsSimulatedProxy => NetworkObject is null || NetworkObject.IsSimulatedProxy;
    public Connection Owner      => NetworkObject?.Owner ?? Connection.None;

    protected internal virtual void OnSpawned() { }

    protected internal virtual void OnDespawned() { }

    protected internal virtual void OnStartServer() { }
    protected internal virtual void OnStartClient() { }
    protected internal virtual void OnStartLocalPlayer() { }

    protected internal virtual void OnOwnershipChanged(Connection previous, Connection next) { }

    protected internal virtual void NetworkTick() { }

    protected internal virtual void OnInterestLost() { }
    protected internal virtual void OnInterestGained() { }

    protected internal virtual void OnStateApplied() { }

    public virtual bool HasNetworkedState => false;
    public virtual int NetworkTypeId => 0;
    public virtual int NetworkLayoutHash => 0;

    public virtual void SerializeState(BitWriter writer) { }

    public virtual void SerializeFullState(BitWriter writer) { }

    public virtual void DeserializeState(ref BitReader reader) { }

    public virtual void CaptureNetworkBaseline() { }

    public virtual object __GetNetBaseline() => null;
    public virtual void __SetNetBaseline(object token) { }

    public virtual bool __NetStateEquals(object token) => true;

    internal void DriveNetSpawn() {
        if (NetBegun)
            return;
        NetBegun = true;

        try { OnSpawned(); }
        catch (Exception e) { ScriptGuard.Report(this, "OnSpawned", e); }

        if (Network.IsServer) {
            try { OnStartServer(); }
            catch (Exception e) { ScriptGuard.Report(this, "OnStartServer", e); }
        }
        if (Network.IsClient) {
            try { OnStartClient(); }
            catch (Exception e) { ScriptGuard.Report(this, "OnStartClient", e); }
        }

        if (IsOwner) {
            try { OnStartLocalPlayer(); }
            catch (Exception e) { ScriptGuard.Report(this, "OnStartLocalPlayer", e); }
        }
    }

    internal void DriveNetDespawn() {
        if (!NetBegun)
            return;
        NetBegun = false;

        try { OnDespawned(); }
        catch (Exception e) { ScriptGuard.Report(this, "OnDespawned", e); }
    }

    internal void DriveOwnershipChanged(Connection previous, Connection next) {
        if (!NetBegun)
            return;
        try { OnOwnershipChanged(previous, next); }
        catch (Exception e) { ScriptGuard.Report(this, "OnOwnershipChanged", e); }
    }

    internal void DriveInterestLost() {
        if (!NetBegun) return;
        try { OnInterestLost(); }
        catch (Exception e) { ScriptGuard.Report(this, "OnInterestLost", e); }
    }
    internal void DriveInterestGained() {
        if (!NetBegun) return;
        try { OnInterestGained(); }
        catch (Exception e) { ScriptGuard.Report(this, "OnInterestGained", e); }
    }

    public Connection RpcCaller { get; internal set; } = Connection.None;

    public NetworkRef<TSelf> AsRef<TSelf>() where TSelf : NetworkBehaviour =>
        NetworkObject is { IsSpawned: true } no ? new NetworkRef<TSelf>(no.NetId) : default;
}
