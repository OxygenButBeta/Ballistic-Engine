namespace BallisticEngine;

public sealed class DeterministicResolver<T> {
    public readonly struct Entry {
        public Entry(T value, int priority, string tieKey) {
            Value = value;
            Priority = priority;
            TieKey = tieKey ?? string.Empty;
        }
        public T Value { get; }
        public int Priority { get; }
        public string TieKey { get; }
    }

    readonly List<Entry> entries = new();

    public void Register(T value, int priority = 0, string tieKey = null) =>
        entries.Add(new Entry(value, priority, tieKey ?? value?.GetType().FullName));

    public int Count => entries.Count;

    public T Resolve(Func<T, bool> match) {
        bool found = false;
        Entry best = default;
        foreach (Entry e in entries) {
            if (!match(e.Value)) continue;
            if (!found || Better(e, best)) {
                best = e;
                found = true;
            }
        }
        return found ? best.Value : default;
    }

    public IReadOnlyList<T> ResolveAll(Func<T, bool> match) {
        var matched = new List<Entry>();
        foreach (Entry e in entries)
            if (match(e.Value))
                matched.Add(e);
        matched.Sort(Compare);
        var result = new T[matched.Count];
        for (int i = 0; i < matched.Count; i++)
            result[i] = matched[i].Value;
        return result;
    }

    public IReadOnlyList<T> All() => ResolveAll(_ => true);

    public void Clear() => entries.Clear();

    static bool Better(Entry candidate, Entry incumbent) => Compare(candidate, incumbent) < 0;

    static int Compare(Entry a, Entry b) {
        int byPriority = b.Priority.CompareTo(a.Priority);
        if (byPriority != 0) return byPriority;
        return string.CompareOrdinal(a.TieKey, b.TieKey);
    }
}
