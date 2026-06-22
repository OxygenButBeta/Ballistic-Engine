using BallisticEngine.Networking;

namespace BallisticEngine;

[Component("Network Object", "Networking")]
public sealed class NetworkObject : Behaviour {
    internal int NetId;

    [NotSerialized]
    public Connection Owner { get; internal set; } = Connection.None;

    [NotSerialized]
    public bool IsSpawned { get; internal set; }

    [NotSerialized]
    public NetworkAuthority Authority { get; internal set; } = NetworkAuthority.None;

    public bool HasStateAuthority => (Authority & NetworkAuthority.State) != 0;
    public bool HasInputAuthority => (Authority & NetworkAuthority.Input) != 0;

    public bool IsProxy => !HasStateAuthority && !HasInputAuthority;

    public bool IsAutonomousProxy => !HasStateAuthority && HasInputAuthority;
    public bool IsSimulatedProxy => IsProxy;

    public bool IsOwner => HasInputAuthority;

    public int OwnerId => Owner.Id;

    [NotSerialized]
    internal Queue<BallisticEngine.Networking.NetworkInput> ServerInputInbox { get; set; }

    [NotSerialized]
    public uint LastProcessedSeq { get; internal set; }

    [NotSerialized]
    internal BallisticEngine.Networking.NetworkInput LastServerInput { get; set; }
    [NotSerialized]
    internal bool HaveLastServerInput { get; set; }

    [NotSerialized]
    internal SnapshotInterpolator Interpolator { get; set; }

    [NotSerialized]
    internal double InterpClock { get; set; }

    [NotSerialized]
    internal PredictionSmoother Smoother { get; set; }

    public bool IsSmoothingCorrection => Smoother is { IsActive: true };

    [NotSerialized]
    public uint PredictKey { get; internal set; }

    [NotSerialized]
    internal long PredictConfirmDeadline { get; set; }

    public bool IsPredictedSpawn => PredictKey != 0;

    [NotSerialized]
    public float LagHitboxRadius { get; set; }

    public bool IsLagCompensated => LagHitboxRadius > 0f;

    [NotSerialized]
    internal PoseHistory LagHistory { get; set; }

    public int LagHistoryCount => LagHistory?.Count ?? 0;

    [NotSerialized]
    public float RelevancyRadius { get; set; }

    [NotSerialized]
    public bool AlwaysRelevant { get; set; }
}
