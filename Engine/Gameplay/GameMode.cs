using BallisticEngine.Networking;

namespace BallisticEngine;

// Server-only rules + the init/spawn driver (plan §2 / §6). A SceneBehaviour, so it lives scene-wide
// and the editor's "Scene" tab + registry discover it free. NEVER replicated (clients never see it).
//
// The framework activates ONLY when a scene declares a GameMode (plan §1): its absence ⇒ today's exact
// play-start behaviour (the byte-identity invariant). When present, SceneManager.StartPlay runs the
// ordered phases (§5) instead of the bare scene.FireBegin():
//   Phase 0  GameMode.InitGame()                 — this, server-only
//   Phase 1  per PlayerController: ResolvePawn + Possess
//   Phase 2  HUD.Init()
//   Phase 3  scene.FireBegin()                    — the single Unity-strand site
//
// P0 = the structure + the single-player path (one local player, host). Per-connection joins over the
// wire (OnPlayerJoined for late joiners) land with the transport (P3); the method exists so the shape
// is final.
[Component("Game Mode", "Gameplay")]
public class GameMode : SceneBehaviour {
    // The active GameMode (the phase runner reads it). Set in OnAttach/OnDetach — the SceneBehaviour
    // "static Active" pattern (Skybox/SceneLighting). Null ⇒ no GameMode ⇒ today's behaviour.
    public static GameMode Active { get; private set; }

    // The default pawn spawned for a player with no scene-placed pawn to claim (Unreal's DefaultPawn,
    // D4 mode A). Null + no scene pawn left ⇒ a config error InitGame logs (a player with no pawn).
    public PrefabAsset DefaultPawn { get; set; }

    protected internal override void OnAttach() => Active = this;

    protected internal override void OnDetach() {
        if (ReferenceEquals(Active, this))
            Active = null;
    }

    // ---- Phase 0: server-only one-shot init (plan §5). Runs ONCE at StartPlay; a late joiner does
    // NOT re-run it (§5 global-phase rule). Override for match rules / spawn-point setup. -----------
    public virtual void InitGame() { }

    // ---- Phase 1 / per-join: resolve the pawn for a connection (D4). ------------------------------
    // Default policy: if an unassigned scene-placed Pawn exists, possess it (Unity familiarity, claimed
    // in deterministic entity-id order — §6 MP rule); otherwise spawn DefaultPawn (Unreal fallback).
    // Override to customize (team spawns, character select). Returns null only on the config error
    // above (no scene pawn and no DefaultPawn) — logged, not silent.
    public virtual Pawn ResolvePawn(Connection connection) {
        Pawn scenePawn = ClaimScenePawn();
        if (scenePawn is not null)
            return scenePawn;

        if (DefaultPawn is null) {
            Debugging.LogError(
                $"GameMode: no scene Pawn left to claim and DefaultPawn is unset — connection {connection} gets no pawn.");
            return null;
        }

        Entity spawned = DefaultPawn.Instantiate(SpawnPosition(connection), Quaternion.Identity);
        if (spawned is null)
            return null;
        // Server-authoritative spawn: assign identity + owner, drive the net strand (§6).
        NetworkObject netObj = Network.Spawn(spawned, connection.IsValid ? connection : Network.LocalConnection);
        Pawn pawn = spawned.GetComponent<Pawn>();
        if (pawn is null)
            Debugging.LogWarning($"GameMode: DefaultPawn '{DefaultPawn.Name}' has no Pawn component.");
        return pawn;
    }

    // A spawn point for the connection. P0: origin (override for real spawn points / a SpawnPoint
    // component). Deterministic — the same connection always gets the same point unless overridden.
    public virtual Vector3 SpawnPosition(Connection connection) => Vector3.Zero;

    // Find the next unassigned scene-placed Pawn in deterministic order (by entity InstanceId, §6) and
    // mark it claimed. Returns null when none remain. Scene pawns are spawned-in-place (they already
    // exist) — claiming assigns ownership + drives their net strand.
    Pawn ClaimScenePawn() {
        Pawn best = null;
        foreach (Entity e in SceneManager.GetCurrentScene().Entities) {
            Pawn p = e.GetComponent<Pawn>();
            if (p is null || p.Controller is not null || p.IsClaimed)
                continue;
            if (best is null || string.CompareOrdinal(e.InstanceId.ToString(), best.Entity.InstanceId.ToString()) < 0)
                best = p;
        }
        if (best is not null)
            best.IsClaimed = true;
        return best;
    }

    // ---- Per-connection join (the wire path, P3). At StartPlay the local player is driven directly
    // by the phase runner; this is the same flow applied to one connection (§5 reconciliation). ------
    public virtual void OnPlayerJoined(Connection connection) { }
    public virtual void OnPlayerLeft(Connection connection) { }
}
