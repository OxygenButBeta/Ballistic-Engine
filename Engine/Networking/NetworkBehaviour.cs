using BallisticEngine.Networking;

namespace BallisticEngine;

// The one networked base (plan §2/§4): is-a Behaviour, so single-player code is unchanged and the
// editor/serializer/CLI discover it free. Carries the NET strand (OnSpawned -> OnStartX ->
// NetworkTick -> OnDespawned) alongside the inherited Unity strand (OnBegin/OnEnabled/Tick/...).
//
// The §8.5 contract, enforced by the drivers below (not by FireEnable):
//   net-logic lives ONLY in OnSpawned/OnDespawned; OnBegin/OnEnabled/OnDisabled are LOCAL cosmetic.
// The §5 phase runner / Network.Spawn calls DriveNetSpawn FIRST (net strand, marks NetBegun), then the
// Unity strand fires OnBegin/OnEnabled exactly once (the HasEnabled guard prevents the double-fire).
//
// P0 = the skeleton (§14 0b): identity/role via the entity's NetworkObject, the callbacks, and
// trivially-true loopback ownership. NetworkTick, [Networked], RPCs, prediction are later phases.
public abstract class NetworkBehaviour : Behaviour {
    // The identity holder on this entity. Resolved lazily (the NetworkObject may be added after this
    // component, or by Network.Spawn). Cached once found — no per-frame reflection (the standing rule).
    NetworkObject netObject;

    public NetworkObject NetworkObject =>
        netObject ??= Entity?.GetComponent<NetworkObject>();

    // The net strand already ran (OnSpawned fired). The §5 mark so Phase 3's FireBegin knows to fire
    // only the Unity strand, and so a double DriveNetSpawn is a no-op.
    internal bool NetBegun;

    // ---- role queries (forward to the NetworkObject; the ONE place authority is decided) ----------
    // Before spawn (no NetworkObject, or unspawned) these read as a non-authority proxy — safe defaults
    // so a stray pre-spawn check never claims authority.
    public bool IsSpawned        => NetworkObject?.IsSpawned ?? false;
    public bool IsOwner          => NetworkObject?.IsOwner ?? false;
    public bool HasStateAuthority => NetworkObject?.HasStateAuthority ?? false;
    public bool HasInputAuthority => NetworkObject?.HasInputAuthority ?? false;
    public bool IsProxy          => NetworkObject is null || NetworkObject.IsProxy;
    public Connection Owner      => NetworkObject?.Owner ?? Connection.None;

    // ---- net-strand callbacks (virtual; subclasses override) --------------------------------------
    // Networked state is valid here; init visuals/subscriptions, spawn predicted children. NOT a place
    // to assume REFERENCED objects exist (§8.5.2 — runtime spawn order is arbitrary).
    protected internal virtual void OnSpawned() { }

    // Symmetric teardown — unsubscribe everything from OnSpawned (§8.5.3 exit matrix: fires for every
    // graceful exit). Best-effort only on hard process kill.
    protected internal virtual void OnDespawned() { }

    // Role-gated start hooks (plan §4e) — the framework targets each on the right machine, so the body
    // has zero `if (IsServer)` / `if (IsOwner)`. P0 fires OnStartLocalPlayer on the input authority only
    // (the owner-routed gate that SetupInput rides). OnStartServer/Client land with the transport (P3).
    protected internal virtual void OnStartServer() { }
    protected internal virtual void OnStartClient() { }
    protected internal virtual void OnStartLocalPlayer() { }

    // The single simulation step (plan §4c) — the only place [Networked] state mutates, once prediction
    // lands (P5). P0 declares it so the contract is stable; the network tick wires it in P2+.
    protected internal virtual void NetworkTick() { }

    // ---- net-strand drivers (called by the phase runner / Network.Spawn, NOT by FireEnable) --------
    // Drive OnSpawned + role hooks IN ORDER, before the Unity strand. Idempotent: a second call (the
    // object touched by both Phase 1 and a later path) is a no-op via NetBegun. ScriptGuard-firewalled
    // exactly like the Unity dispatch sites — a throwing OnSpawned can't crash play-start.
    internal void DriveNetSpawn() {
        if (NetBegun)
            return;
        NetBegun = true;

        try { OnSpawned(); }
        catch (Exception e) { ScriptGuard.Report(this, "OnSpawned", e); }

        // Topology role hooks (P0: server/client fire on a host since it is both; refined in P3 when
        // the transport distinguishes the local machine's role per object).
        if (Network.IsServer) {
            try { OnStartServer(); }
            catch (Exception e) { ScriptGuard.Report(this, "OnStartServer", e); }
        }
        if (Network.IsClient) {
            try { OnStartClient(); }
            catch (Exception e) { ScriptGuard.Report(this, "OnStartClient", e); }
        }
        // Owner-gated: fires ONLY on the input authority. On a proxy this is never reached — the
        // Grade-1 unrepresentable non-owner path (§3): there is no else, nothing to misuse.
        if (IsOwner) {
            try { OnStartLocalPlayer(); }
            catch (Exception e) { ScriptGuard.Report(this, "OnStartLocalPlayer", e); }
        }
    }

    // Drive OnDespawned (graceful exit). Clears NetBegun so a pooled reuse re-runs OnSpawned (§8.5.4).
    internal void DriveNetDespawn() {
        if (!NetBegun)
            return;
        NetBegun = false;

        try { OnDespawned(); }
        catch (Exception e) { ScriptGuard.Report(this, "OnDespawned", e); }
    }
}
