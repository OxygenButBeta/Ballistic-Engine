namespace BallisticEngine;

public static class ReloadCaches {
    static readonly List<Action> invalidators = new();

    public static void Register(Action invalidate) {
        if (invalidate is null || invalidators.Contains(invalidate))
            return;
        invalidators.Add(invalidate);
    }

    public static void InvalidateAll() {
        foreach (Action invalidate in invalidators)
            invalidate();
    }

    public static int RegisteredCount => invalidators.Count;
}
