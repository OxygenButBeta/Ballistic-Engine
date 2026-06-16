using System.Numerics;
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

    // ---- P7: local-player resolution (the HUD binding seam, plan §2/§5 Phase 2) -------------------
    // The PlayerController / PlayerState this machine OWNS (the local player) — HUD.Init binds to these.
    // Null on a dedicated server or before possession. Reflection-free; call at HUD.Init / on demand.
    public static PlayerController LocalPlayerController => Manager?.LocalPlayerController();
    public static PlayerState LocalPlayerState => Manager?.LocalPlayerState();

    // ---- P7: reconnect (ConnectionToken, §8.5.5 / §9.8) -------------------------------------------
    // The persistent token the LOCAL client presents at the next connect. A first join leaves it None
    // (the server mints one, delivered back via HandshakeOk and stored here). To RECONNECT and reclaim the
    // pawn, persist this token across the disconnect (a real client writes it to disk), then set it before
    // StartClient. Server-side: a presented token that matches a live reconnect orphan transfers the pawn's
    // ownership back automatically (the framework default).
    public static ConnectionToken ReconnectToken {
        get => Manager?.PersistentToken ?? ConnectionToken.None;
        set { if (Manager is not null) Manager.PersistentToken = value; }
    }

    // The reconnect window TTL in fixed ticks (server-side). A disconnected player has this long to reclaim.
    public static long ReconnectTtlTicks {
        get => Manager?.ReconnectTtlTicks ?? 0;
        set { if (Manager is not null) Manager.ReconnectTtlTicks = value; }
    }

    // Fired on the SERVER when a reconnect reclaimed an orphaned pawn (the rejoin hook — re-bind HUD,
    // announce). The arg is the reclaiming connection.
    public static Action<Connection> OnPlayerReconnected {
        get => Manager?.OnPlayerReconnected;
        set { if (Manager is not null) Manager.OnPlayerReconnected = value; }
    }

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

    // ---- predicted spawn (P5f, §8.5.1) ------------------------------------------------------------
    // CLIENT-side predicted spawn: create a networked object INSTANTLY (a fired bullet) before the server
    // round-trip, tagged with a prediction key. The triggering RPC must carry the returned key UP so the
    // server echoes it on the authoritative spawn → the client LINKS (no duplicate, OnSpawned fires once)
    // rather than building a second mirror. If the server rejects the action (no echo within the rollback
    // window), the predicted object is destroyed cleanly. On a host this is a plain Spawn (key 0 — the
    // authority does not predict). Returns (object, key).
    public static (NetworkObject obj, uint key) PredictSpawn(Entity entity, Connection owner = default) =>
        Require().PredictSpawn(entity, owner);

    // SERVER-side answer to a predicted spawn (P5f): spawn the authoritative object ECHOING the prediction
    // key the client's RPC carried up, so the owning client LINKS it to its predicted object (no
    // duplicate). Call from inside a To.Server RPC impl (the dev passes the key arg through). owner
    // defaults to the firing client (RpcCaller) when left default at the call site.
    public static NetworkObject SpawnPredicted(Entity entity, uint predictKey, Connection owner = default) =>
        Require().SpawnPredicted(entity, predictKey, owner);

    // ---- ownership transfer (server-only, replicated; plan §4d) -----------------------------------
    // Move input authority to a new connection (pick-up, vehicle-enter, detachable turret). Server-only
    // — a client cannot grant itself ownership. Fires OnOwnershipChanged on the affected objects.
    public static void TransferOwnership(NetworkObject netObj, Connection newOwner) =>
        Manager?.TransferOwnership(netObj, newOwner);

    // Drop ownership back to the server (Connection.None).
    public static void RemoveOwnership(NetworkObject netObj) => Manager?.RemoveOwnership(netObj);

    // ---- lag compensation (P8a, plan §9 item 9 / §13) — favor-the-shooter hitscan -----------------
    // The render-tick a LOCAL hitscan shot should carry UP to the server: the PAST server-moment the
    // client's screen actually showed (it renders proxies InterpDelay ticks behind the latest server tick,
    // P5c). The game reads this when firing — `weapon.Fire(origin, dir, Network.RenderTick)` — and the
    // server's To.Server impl passes it to LagCompensatedRaycast so the shot is resolved as the shooter saw
    // it. On a host/server this is the current tick (the host renders the authoritative present).
    public static double RenderTick => Manager?.RenderTick ?? 0;

    // The current authoritative server tick (the fixed-step counter); on a client it trails via snapshots.
    public static uint ServerTick => Manager?.ServerTick ?? 0;

    // SERVER-side lag-compensated hitscan (§9.9): rewind every OTHER lag-compensated pawn's hitbox to the
    // pose it occupied at `renderTick` (clamped to the server's max rewind), run the ray, restore — so a
    // shot the shooter saw connect HITS even though the target has since moved. Call from inside a
    // [Rpc(To.Server)] shot impl, passing the renderTick the client carried up. A pawn opts into being a
    // target by setting NetworkObject.LagHitboxRadius > 0 (a sphere hitbox) on spawn. Returns the nearest hit.
    public static bool LagCompensatedRaycast(Vector3 origin, Vector3 direction, double renderTick,
        NetworkObject shooter, out LagRaycastHit hit) {
        if (Manager is null) { hit = default; return false; }
        return Manager.LagCompensatedRaycast(origin, direction, renderTick, shooter, out hit);
    }

    // Tuning knobs (§9.9): the interp delay the render-tick is derived from (MUST match the proxy
    // interpolation delay), and the server-side max rewind (anti-abuse + the history ring length).
    public static double InterpDelayTicks {
        get => Manager?.InterpDelayTicks ?? 0;
        set { if (Manager is not null) Manager.InterpDelayTicks = value; }
    }
    public static int MaxRewindTicks {
        get => Manager?.MaxRewindTicks ?? 0;
        set { if (Manager is not null) Manager.MaxRewindTicks = value; }
    }

    // ---- interest management (P8b, plan §14 item 14) — per-connection AOI culling ------------------
    // OFF by default (every object replicates to every client — byte-identical to pre-P8b). Turn ON to cull
    // replication by area-of-interest: an out-of-interest object isn't flushed to a client (a scale/bandwidth
    // subsystem). An AOI transition fires OnInterestLost/OnInterestGained on the object — NOT despawn
    // (relevancy != disconnect; the object stays spawned, subscriptions intact). A pawn opts into a custom
    // bubble via NetworkObject.RelevancyRadius, or AlwaysRelevant to bypass AOI (its own pawn, a global object).
    public static bool InterestManagement {
        get => Manager?.InterestManagement ?? false;
        set { if (Manager is not null) Manager.InterestManagement = value; }
    }

    // The default AOI radius for an object with RelevancyRadius == 0 (the common per-game bubble).
    public static float DefaultRelevancyRadius {
        get => Manager?.DefaultRelevancyRadius ?? 0;
        set { if (Manager is not null) Manager.DefaultRelevancyRadius = value; }
    }

    // Observability (P8b): is `obj` currently in connection `c`'s area of interest? Reads the per-client
    // relevancy frontier (populated only while interest management is on). A tool/test seam.
    public static bool IsInInterest(Connection c, NetworkObject obj) =>
        Manager?.IsInInterest(c, obj) ?? false;

    // The relevancy DECISION as a pure function (the ResolveRole pattern) — predict whether an object with
    // the given (alwaysRelevant, owned-by-the-viewer) flags and position is relevant to a viewer at `view`
    // (hasView=false => the viewer has no pawn). The live cull calls the SAME function, so this never drifts
    // — it's the testable form of the §14-item-14 AOI rule.
    public static bool IsRelevant(bool alwaysRelevant, bool ownedByViewer, bool hasView,
        Vector3 view, Vector3 objectPos, float radius) =>
        NetworkManager.IsRelevantPure(alwaysRelevant, ownedByViewer, hasView, view, objectPos, radius);

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
