namespace BallisticEngine;

// Per-player replicated data (plan §2) — name, score, team, ping. A NetworkBehaviour, replicated to
// all (so every client can show the scoreboard). Lives on the player's entity alongside the
// PlayerController, or on a dedicated player-info entity. Survives a pawn respawn (the player persists
// even when the pawn dies — the Unreal split).
//
// P0 = the type (discovered, replicable-ready). [Networked] members + the scoreboard binding are P2/P7.
[Component("Player State", "Gameplay")]
public class PlayerState : NetworkBehaviour {
    // Display name. P0: a plain serialized member; becomes [Networked] in P2 so it replicates.
    public string PlayerName { get; set; } = "Player";
}
