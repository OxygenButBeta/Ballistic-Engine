using System;
using System.Collections.Generic;

namespace BallisticEngine;

// P0.4 (Trap 2, folded into Chunk 1): the deterministic resolution substrate every self-registering registry
// shares. The current DrawerRegistry is "LAST-registered-that-CanDraw wins" with NO priority (ITypeDrawer.cs)
// — and with self-registration across assemblies, registration order == assembly-load order ==
// NONDETERMINISTIC, so WHICH drawer wins varies by machine/build. That is the exact bug this fixes.
//
// The rule, applied identically to drawers, attribute-drawers, windows, and commands: each entry carries an
// explicit PRIORITY; among entries that match a query, the HIGHEST priority wins; ties break by a STABLE
// ORDINAL key (a string the registry supplies, e.g. the entry type's full name). The winner is therefore a
// total, machine-independent function of the registered set — never of load order.
//
// Lives in the engine (not the editor) because both the headless serializer registries (G3's
// [SerializeReference] concrete-type resolution) and the editor drawer/window registries need the SAME
// guarantee. Phase B0 migrates DrawerRegistry onto this; A1/D1 use it for windows/commands.
public sealed class DeterministicResolver<T> {
    public readonly struct Entry {
        public Entry(T value, int priority, string tieKey) {
            Value = value;
            Priority = priority;
            TieKey = tieKey ?? string.Empty;
        }
        public T Value { get; }
        public int Priority { get; }
        public string TieKey { get; }   // stable ordinal tie-break (typically the entry's type FullName)
    }

    readonly List<Entry> entries = new();

    // Register an entry. `priority` higher = preferred (default 0); `tieKey` breaks equal priorities
    // deterministically — pass something stable + unique like `value.GetType().FullName`.
    public void Register(T value, int priority = 0, string tieKey = null) =>
        entries.Add(new Entry(value, priority, tieKey ?? value?.GetType().FullName));

    public int Count => entries.Count;

    // The single best entry for which `match` is true, or default(T) if none. Deterministic: highest
    // priority, then lowest ordinal tieKey — independent of registration/assembly-load order.
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

    // ALL matching entries, in deterministic best-first order (priority desc, then tieKey asc). For
    // registries that need the full ordered set (e.g. a menu, or layering decorators) rather than one winner.
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

    // Every registered entry in deterministic order (no predicate) — for window/command menus.
    public IReadOnlyList<T> All() => ResolveAll(_ => true);

    public void Clear() => entries.Clear();

    static bool Better(Entry candidate, Entry incumbent) => Compare(candidate, incumbent) < 0;

    // Best-first: higher priority sorts first; equal priority breaks by ordinal tieKey ascending.
    static int Compare(Entry a, Entry b) {
        int byPriority = b.Priority.CompareTo(a.Priority);   // descending priority
        if (byPriority != 0) return byPriority;
        return string.CompareOrdinal(a.TieKey, b.TieKey);    // ascending stable key
    }
}
