namespace BallisticEngine;

public static class GamePhaseRunner {
    public static bool HasGameMode(Scene scene) {
        foreach (SceneBehaviour sb in scene.SceneBehaviours)
            if (sb is GameMode { IsEnabled: true })
                return true;
        return false;
    }

    public static void Run(Scene scene) {
        GameMode gameMode = Find<GameMode>(scene);
        if (gameMode is null) {
            scene.FireBegin();
            return;
        }

        if (Network.IsOffline)
            Network.StartHost();

        try { gameMode.InitGame(); }
        catch (Exception e) { ScriptGuard.Report(gameMode, "InitGame", e); }

        foreach (PlayerController controller in ScenePlayerControllers(scene)) {
            EnsureSpawned(controller, Network.LocalConnection);

            Pawn pawn = null;
            try { pawn = gameMode.ResolvePawn(Network.LocalConnection); }
            catch (Exception e) { ScriptGuard.Report(gameMode, "ResolvePawn", e); }

            if (pawn is not null) {
                EnsureSpawned(pawn, Network.LocalConnection);
                controller.Possess(pawn);
            }
        }

        HUD hud = Find<HUD>(scene);
        if (hud is not null) {
            try { hud.RunInit(); }
            catch (Exception e) { ScriptGuard.Report(hud, "Init", e); }
        }

        scene.FireBegin();
    }

    static void EnsureSpawned(NetworkBehaviour nb, Networking.Connection owner) {
        NetworkObject netObj = nb.NetworkObject;
        if (netObj is { IsSpawned: true })
            return;
        Network.Spawn(nb.Entity, owner);
    }

    static IEnumerable<PlayerController> ScenePlayerControllers(Scene scene) {
        var list = new List<PlayerController>();
        foreach (Entity e in scene.Entities) {
            PlayerController pc = e.GetComponent<PlayerController>();
            if (pc is not null)
                list.Add(pc);
        }

        list.Sort((a, b) => string.CompareOrdinal(a.Entity.InstanceId.ToString(), b.Entity.InstanceId.ToString()));
        return list;
    }

    static T Find<T>(Scene scene) where T : SceneBehaviour {
        foreach (SceneBehaviour sb in scene.SceneBehaviours)
            if (sb is T t)
                return t;
        return null;
    }
}
