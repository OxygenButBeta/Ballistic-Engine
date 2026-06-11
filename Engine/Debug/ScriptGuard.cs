namespace BallisticEngine;

// Exception firewall for user-script callbacks: game code must NEVER take the engine down.
// Every lifecycle dispatch site (Tick/FixedTick, OnBegin/OnEnabled/OnDisabled, OnAttach/OnDetach,
// gizmos, physics contacts) catches per component per callback, routes here, and keeps running —
// the frame completes, every other component still ticks.
//
// Stack traces resolve to Assets/...cs:line because GameScripts loads the portable PDB next to
// the dll — the log line IS the fix-it feedback for whoever (or whatever) wrote the script.
public static class ScriptGuard {
    // Per-frame callbacks (Tick, FixedTick, OnDrawGizmos) that keep throwing get their component
    // disabled instead of flooding the console at 60 Hz; one-shot callbacks just log each fault.
    public const int DisableAfterConsecutiveFaults = 3;

    public static void Report(Behaviour behaviour, string callback, Exception exception) =>
        Debugging.LogError($"{Describe(behaviour, callback)} threw:\n{exception}");

    public static void Report(SceneBehaviour behaviour, string callback, Exception exception) =>
        Debugging.LogError($"{behaviour.GetType().Name}.{callback} (scene component) threw:\n{exception}");

    // For callbacks that run every frame/step. The streak is PER CALLBACK: a Tick that throws
    // every frame must still hit the threshold even though the same component's FixedTick keeps
    // succeeding (the success reset in the dispatch loops only applies to the owning callback).
    public static void ReportRepeating(Behaviour behaviour, string callback, Exception exception) {
        if (!ReferenceEquals(behaviour.FaultCallback, callback)) {
            behaviour.FaultCallback = callback;
            behaviour.FaultStreak = 0;
        }
        behaviour.FaultStreak++;
        if (behaviour.FaultStreak < DisableAfterConsecutiveFaults) {
            Report(behaviour, callback, exception);
            return;
        }

        behaviour.FaultStreak = 0;
        Debugging.LogError(
            $"{Describe(behaviour, callback)} threw {DisableAfterConsecutiveFaults} times in a row — " +
            $"component DISABLED (fix the script, then re-enable it in the Inspector):\n{exception}");
        behaviour.IsEnabled = false; // setter guards its own OnDisabled call
    }

    public static void ReportRepeating(SceneBehaviour behaviour, string callback, Exception exception) {
        if (!ReferenceEquals(behaviour.FaultCallback, callback)) {
            behaviour.FaultCallback = callback;
            behaviour.FaultStreak = 0;
        }
        behaviour.FaultStreak++;
        if (behaviour.FaultStreak < DisableAfterConsecutiveFaults) {
            Report(behaviour, callback, exception);
            return;
        }

        behaviour.FaultStreak = 0;
        behaviour.IsEnabled = false;
        Debugging.LogError(
            $"{behaviour.GetType().Name}.{callback} threw {DisableAfterConsecutiveFaults} times in a row — " +
            $"scene component DISABLED (fix the script, then re-enable it):\n{exception}");
    }

    static string Describe(Behaviour behaviour, string callback) =>
        $"{behaviour.GetType().Name}.{callback} on '{behaviour.Entity?.Name ?? "?"}'";
}
