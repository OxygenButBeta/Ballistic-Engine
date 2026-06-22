namespace BallisticEngine;

public readonly struct KeyChord<TKey> : IEquatable<KeyChord<TKey>> where TKey : struct {
    public KeyChord(TKey key, bool ctrl = false, bool shift = false, bool alt = false) {
        Key = key;
        Ctrl = ctrl;
        Shift = shift;
        Alt = alt;
    }

    public TKey Key { get; }
    public bool Ctrl { get; }
    public bool Shift { get; }
    public bool Alt { get; }

    public bool Equals(KeyChord<TKey> other) =>
        EqualityComparer<TKey>.Default.Equals(Key, other.Key) &&
        Ctrl == other.Ctrl && Shift == other.Shift && Alt == other.Alt;

    public override bool Equals(object obj) => obj is KeyChord<TKey> o && Equals(o);
    public override int GetHashCode() => HashCode.Combine(Key, Ctrl, Shift, Alt);

    public override string ToString() {
        string m = (Ctrl ? "Ctrl+" : "") + (Shift ? "Shift+" : "") + (Alt ? "Alt+" : "");
        return m + Key;
    }
}

public sealed class InputAction<TKey> where TKey : struct {
    public InputAction(string id, KeyChord<TKey> chord, int context, int priority, Action invoke) {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        Chord = chord;
        Context = context;
        Priority = priority;
        Invoke = invoke ?? throw new ArgumentNullException(nameof(invoke));
    }

    public string Id { get; }
    public KeyChord<TKey> Chord { get; }
    public int Context { get; }
    public int Priority { get; }
    public Action Invoke { get; }
}

public readonly struct InputConflict {
    public InputConflict(string idA, string idB, string chord, int context) {
        IdA = idA; IdB = idB; Chord = chord; Context = context;
    }
    public string IdA { get; }
    public string IdB { get; }
    public string Chord { get; }
    public int Context { get; }
    public override string ToString() => $"{Chord} in ctx {Context}: '{IdA}' vs '{IdB}'";
}

public sealed class InputActionChain<TKey> where TKey : struct {
    readonly List<InputAction<TKey>> actions = new();
    InputAction<TKey>[] ordered = Array.Empty<InputAction<TKey>>();
    bool built;

    public InputActionChain<TKey> Add(InputAction<TKey> action) {
        if (action is null) throw new ArgumentNullException(nameof(action));
        actions.Add(action);
        built = false;
        return this;
    }

    public InputActionChain<TKey> Add(string id, KeyChord<TKey> chord, int context, int priority, Action invoke) =>
        Add(new InputAction<TKey>(id, chord, context, priority, invoke));

    public int Count => actions.Count;

    public void Build() {
        ordered = actions
            .OrderByDescending(a => a.Priority)
            .ThenBy(a => a.Id, StringComparer.Ordinal)
            .ToArray();
        built = true;
    }

    public InputAction<TKey> Resolve(int contextMask, Func<KeyChord<TKey>, bool> isChordActive,
                                     Func<InputAction<TKey>, bool> isEnabled = null) {
        if (isChordActive is null) throw new ArgumentNullException(nameof(isChordActive));
        if (!built) Build();
        InputAction<TKey>[] list = ordered;
        for (int i = 0; i < list.Length; i++) {
            InputAction<TKey> a = list[i];
            if ((a.Context & contextMask) == 0) continue;
            if (isEnabled != null && !isEnabled(a)) continue;
            if (!isChordActive(a.Chord)) continue;
            return a;
        }
        return null;
    }

    public bool Dispatch(int contextMask, Func<KeyChord<TKey>, bool> isChordActive,
                         Func<InputAction<TKey>, bool> isEnabled = null) {
        InputAction<TKey> winner = Resolve(contextMask, isChordActive, isEnabled);
        if (winner is null) return false;
        winner.Invoke();
        return true;
    }

    public IReadOnlyList<InputConflict> CheckConflicts() {
        var conflicts = new List<InputConflict>();
        for (int i = 0; i < actions.Count; i++)
            for (int j = i + 1; j < actions.Count; j++) {
                InputAction<TKey> a = actions[i], b = actions[j];
                if (a.Priority == b.Priority && a.Context == b.Context && a.Chord.Equals(b.Chord))
                    conflicts.Add(new InputConflict(a.Id, b.Id, a.Chord.ToString(), a.Context));
            }
        return conflicts;
    }

    public void Clear() {
        actions.Clear();
        ordered = Array.Empty<InputAction<TKey>>();
        built = false;
    }
}
