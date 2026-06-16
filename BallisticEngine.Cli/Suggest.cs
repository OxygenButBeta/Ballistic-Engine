namespace BallisticEngine.Cli;

// Shared "did you mean" scoring for CLI error messages: case-insensitive prefix beats contains
// beats small edit distance. Returns null when nothing is close enough to be worth suggesting.
internal static class Suggest {
    public static string? Closest(string typed, IEnumerable<string?> candidates) {
        string? best = null;
        int bestScore = int.MaxValue;
        foreach (string? name in candidates) {
            if (string.IsNullOrEmpty(name)) continue;
            int score = name.StartsWith(typed, StringComparison.OrdinalIgnoreCase) ? 0
                : name.Contains(typed, StringComparison.OrdinalIgnoreCase) ? 1
                : Levenshtein(name.ToLowerInvariant(), typed.ToLowerInvariant());
            if (score < bestScore) { bestScore = score; best = name; }
        }
        return bestScore <= 2 ? best : null;
    }

    static int Levenshtein(string a, string b) {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
            for (var j = 1; j <= b.Length; j++) {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
            }
        return d[a.Length, b.Length];
    }
}
