using BallisticEngine.Networking;

namespace BallisticEngine;

[Component("Game Mode", "Gameplay")]
public class GameMode : SceneBehaviour {
    public static GameMode Active { get; private set; }

    public PrefabAsset DefaultPawn { get; set; }

    protected internal override void OnAttach() => Active = this;

    protected internal override void OnDetach() {
        if (ReferenceEquals(Active, this))
            Active = null;
    }

    public virtual void InitGame() { }

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
        NetworkObject netObj = Network.Spawn(spawned, connection.IsValid ? connection : Network.LocalConnection);
        Pawn pawn = spawned.GetComponent<Pawn>();
        if (pawn is null)
            Debugging.LogWarning($"GameMode: DefaultPawn '{DefaultPawn.Name}' has no Pawn component.");
        return pawn;
    }

    public virtual Vector3 SpawnPosition(Connection connection) => Vector3.Zero;

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

    public virtual void OnPlayerJoined(Connection connection) { }
    public virtual void OnPlayerLeft(Connection connection) { }
}
