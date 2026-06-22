namespace BallisticEngine;

public static class ScriptGuard {
    public const int DisableAfterConsecutiveFaults = 3;

    public static void Report(Behaviour behaviour, string callback, Exception exception) =>
        Debugging.LogError($"{Describe(behaviour, callback)} threw:\n{exception}");

    public static void Report(SceneBehaviour behaviour, string callback, Exception exception) =>
        Debugging.LogError($"{behaviour.GetType().Name}.{callback} (scene component) threw:\n{exception}");

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
        behaviour.IsEnabled = false;
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
