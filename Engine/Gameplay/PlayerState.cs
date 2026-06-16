namespace BallisticEngine;

// Per-player replicated data (plan §2) — name, score, team, ping. A NetworkBehaviour, replicated to all
// (so every client can show the scoreboard). Lives on the player's entity alongside the PlayerController,
// or on a dedicated player-info entity. SURVIVES a pawn respawn (the player persists even when the pawn
// dies — the Unreal split: the pawn is the body, the PlayerState is the player).
//
// P7: PlayerState replicates through the EXISTING NetworkBehaviour path — a game subclass declares
// `[Networked]` auto-properties and is `partial`, and the source generator emits its wire serializer just
// like any other NetworkBehaviour (no special machinery — unlike GameState, PlayerState IS on an entity,
// so it is NOT the §10 entity-less carve-out). HUD.Init (Phase 2) binds to the LOCAL player's PlayerState
// via Network.LocalPlayerState. The reconnect window (§8.5.5) keeps the PlayerState alive through a
// disconnect (it is owned by the pawn's NetworkObject; the orphan stays spawned), so a reclaim restores
// the player's name/score with no respawn.
[Component("Player State", "Gameplay")]
public class PlayerState : NetworkBehaviour {
    // Display name. A plain serialized member on the engine base; a game subclass that wants it replicated
    // declares `[Networked] public string PlayerName { get; set; }` in a partial subclass (string [Networked]
    // is a §14-item-13 follow-up — P7 ships scalar/Vector PlayerState fields; PlayerName stays a scene member
    // until string replication lands). HUD reads it from the local PlayerState.
    public string PlayerName { get; set; } = "Player";
}
