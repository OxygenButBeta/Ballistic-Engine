namespace BallisticEngine;

// The ordered play-start driver (plan §5) — runs ONLY when a scene declares a GameMode. SceneManager.
// StartPlay calls Run() in place of the bare scene.FireBegin(); no GameMode ⇒ StartPlay keeps calling
// scene.FireBegin() directly (the byte-identity invariant — this class is never touched).
//
// The B1/B2 fix (gate 0a, proven in %TEMP%\bal-gate-test): Phases 0-2 drive ONLY the NET strand
// (OnSpawned/OnStartX via DriveNetSpawn) and mark each NetworkBehaviour NetBegun. Phase 3's single
// scene.FireBegin() is the ONLY Unity-strand (OnBegin/OnEnabled) site; the HasEnabled guard in
// Behaviour.FireEnable makes that idempotent so OnEnabled fires exactly once even though a framework
// component was touched in Phase 1.
//
//   Phase 0  GameMode.InitGame()                server-only one-shot
//   Phase 1  per PlayerController: ResolvePawn + Possess  (net strand fires here, owner-gated SetupInput)
//   Phase 2  HUD.Init()                         client-only one-shot
//   Phase 3  scene.FireBegin()                  the single Unity-strand site
public static class GamePhaseRunner {
    // True if the scene has a GameMode (StartPlay routes here instead of the bare FireBegin).
    public static bool HasGameMode(Scene scene) {
        foreach (SceneBehaviour sb in scene.SceneBehaviours)
            if (sb is GameMode { IsEnabled: true })
                return true;
        return false;
    }

    public static void Run(Scene scene) {
        GameMode gameMode = Find<GameMode>(scene);
        if (gameMode is null) {
            // Defensive: HasGameMode gated us in, so this shouldn't happen — fall back to today's path.
            scene.FireBegin();
            return;
        }

        // Single-player / listen-server: bring up the loopback host so the SAME netcode path runs (D5).
        // GameMode spawns over it; multiplayer is a transport swap, not a code change. Idempotent — if a
        // host script already started one, StartHost replaces it; offline-only scenes never reach here.
        if (Network.IsOffline)
            Network.StartHost();

        // ---- Phase 0: server-only rules + spawn setup -----------------------------------------------
        try { gameMode.InitGame(); }
        catch (Exception e) { ScriptGuard.Report(gameMode, "InitGame", e); }

        // ---- Phase 1: per PlayerController, resolve + possess a pawn (net strand fires) --------------
        // Deterministic order (entity InstanceId) so claim-order matches across machines (§6). At
        // StartPlay this is the local player(s) present now; a late joiner re-runs this flow via
        // GameMode.OnPlayerJoined (§5 reconciliation).
        foreach (PlayerController controller in ScenePlayerControllers(scene)) {
            // Spawn the controller's own net identity (so IsOwner/role resolve before Possess).
            EnsureSpawned(controller, Network.LocalConnection);

            Pawn pawn = null;
            try { pawn = gameMode.ResolvePawn(Network.LocalConnection); }
            catch (Exception e) { ScriptGuard.Report(gameMode, "ResolvePawn", e); }

            if (pawn is not null) {
                EnsureSpawned(pawn, Network.LocalConnection);
                controller.Possess(pawn);
            }
        }

        // ---- Phase 2: client-only HUD init (binds to the local player, which now exists) ------------
        HUD hud = Find<HUD>(scene);
        if (hud is not null) {
            try { hud.Init(); }
            catch (Exception e) { ScriptGuard.Report(hud, "Init", e); }
        }

        // ---- Phase 3: the SINGLE Unity-strand site (OnBegin/OnEnabled for EVERY component) ----------
        // Framework components already ran their net strand in Phase 1 (NetBegun); the HasEnabled guard
        // ensures their OnEnabled fires exactly once here. Plain Behaviours get today's OnBegin/OnEnabled.
        scene.FireBegin();
    }

    // Spawn a framework component's NetworkObject if it isn't already (drives its net strand). Scene-
    // placed pawns/controllers have no NetworkObject until claimed; Network.Spawn adds one and drives
    // OnSpawned/OnStartX in order, BEFORE Phase 3's Unity strand.
    static void EnsureSpawned(NetworkBehaviour nb, Networking.Connection owner) {
        NetworkObject netObj = nb.NetworkObject;
        if (netObj is { IsSpawned: true })
            return;
        // Network.Spawn adds a NetworkObject if missing and drives the net strand of the whole entity.
        Network.Spawn(nb.Entity, owner);
    }

    static IEnumerable<PlayerController> ScenePlayerControllers(Scene scene) {
        var list = new List<PlayerController>();
        foreach (Entity e in scene.Entities) {
            PlayerController pc = e.GetComponent<PlayerController>();
            if (pc is not null)
                list.Add(pc);
        }
        // Deterministic claim order (§6): stable by entity identity.
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
