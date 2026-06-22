namespace BallisticEngine;

[Component("Player State", "Gameplay")]
public class PlayerState : NetworkBehaviour {
    public string PlayerName { get; set; } = "Player";
}
